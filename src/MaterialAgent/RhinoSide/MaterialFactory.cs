using System;
using System.Collections.Generic;
using System.Linq;
using MaterialAgent.Core;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Render;

namespace MaterialAgent.RhinoSide
{
    public sealed class ImportResult
    {
        public RenderMaterial Material { get; set; }
        public bool Reused { get; set; }
        public int AssignedCount { get; set; }
        public int MappedCount { get; set; }
        public bool MapsGenerated { get; set; }
        public string MapError { get; set; }
        public bool IsEnscape { get; set; }
        /// <summary>Set when Enscape conversion was asked for but didn't happen.</summary>
        public string EnscapeWarning { get; set; }
    }

    /// <summary>Creates (or reuses) the material and applies it to objects. Call on the UI thread.</summary>
    public static class MaterialFactory
    {
        public static ImportResult Import(RhinoDoc doc, ImportSettings settings, IList<RhinoObject> targets, RenderMaterial reuse)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (settings?.Image == null && reuse == null) throw new InvalidOperationException("No image loaded.");

            var result = new ImportResult { Reused = reuse != null };

            // Bake maps before the undo record: it's file work, not document work.
            BakedMaps maps = null;
            if (reuse == null && settings.GenerateMaps)
            {
                try { maps = MapBaker.Bake(settings.Image, settings.Finish); result.MapsGenerated = true; }
                catch (Exception ex) { result.MapError = ex.Message; }
            }

            uint undo = doc.BeginUndoRecord("Material Agent import");
            try
            {
                result.Material = reuse ?? CreateRenderMaterial(doc, settings, maps);

                // Convert before assigning, so objects get the final material.
                if (reuse == null && settings.AsEnscape)
                {
                    var (converted, warning) = EnscapeSupport.Convert(doc, result.Material);
                    result.Material = converted;
                    result.EnscapeWarning = warning;
                }
                var enscapeType = settings.AsEnscape ? EnscapeSupport.MaterialType : null;
                result.IsEnscape = enscapeType != null && result.Material.TypeId == enscapeType.Id;

                foreach (var obj in targets)
                {
                    if (obj == null || obj.IsDeleted) continue;
                    obj.RenderMaterial = result.Material;
                    obj.CommitChanges();
                    result.AssignedCount++;
                }

                result.MappedCount = MappingApplier.Apply(doc, targets, settings.Mapping);

                if (!result.Reused)
                    ProvenanceStore.WriteUserStrings(doc, result.Material, settings.Provenance);

                doc.Views.Redraw();
                return result;
            }
            finally
            {
                doc.EndUndoRecord(undo);
            }
        }

        static Texture BitmapTexture(string path) => new Texture
        {
            FileName = path,
            WrapU = TextureUvwWrapping.Repeat,
            WrapV = TextureUvwWrapping.Repeat,
            WrapW = TextureUvwWrapping.Repeat,
        };
        // Texture.MappingChannelId is read-only; new textures use mapping channel 1 (MappingApplier.Channel).

        static RenderMaterial CreateRenderMaterial(RhinoDoc doc, ImportSettings settings, BakedMaps maps)
        {
            var material = new Material { Name = settings.MaterialName };
            material.ToPhysicallyBased();
            var pbr = material.PhysicallyBased;
            pbr.BaseColor = new Color4f(1f, 1f, 1f, 1f); // don't tint the texture
            pbr.Metallic = 0;
            pbr.Roughness = EnumText.Roughness(settings.Finish);
            pbr.SetTexture(BitmapTexture(settings.Image.LocalPath), TextureType.PBR_BaseColor);
            if (maps != null)
            {
                pbr.SetTexture(BitmapTexture(maps.RoughnessPath), TextureType.PBR_Roughness);
                // The PBR bump slot takes either a bump or a normal map; Rhino detects which (OpenGL-style normals, as generated).
                pbr.SetTexture(BitmapTexture(maps.NormalPath), TextureType.Bump);
            }

            var rm = RenderMaterial.FromMaterial(material, doc) ?? RenderMaterial.CreateBasicMaterial(material, doc);
            rm.Name = settings.MaterialName;
            if (settings.Provenance != null)
                rm.Notes = settings.Provenance.ToNotes();

            doc.RenderMaterials.Add(rm);
            // Add() may wrap or copy the content; return the instance that lives in the document.
            return doc.RenderMaterials.Find(rm.Id) ?? rm;
        }
    }
}
