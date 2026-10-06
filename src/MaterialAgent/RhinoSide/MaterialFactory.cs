using System;
using System.Collections.Generic;
using System.Linq;
using MaterialAgent.Core;
using MaterialAgent.Core.Colors;
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
        /// <summary>Layers whose material was set (Import to layer).</summary>
        public int LayerCount { get; set; }
        /// <summary>Objects on those layers that keep their own object material.</summary>
        public int KeptOwnMaterialCount { get; set; }
        /// <summary>Set when Enscape conversion was asked for but didn't happen.</summary>
        public string EnscapeWarning { get; set; }
    }

    /// <summary>Creates (or reuses) the material and applies it to objects. Call on the UI thread.</summary>
    public static class MaterialFactory
    {
        /// <param name="targets">Objects to assign (selection mode), or ignored when <paramref name="layers"/> is given.</param>
        /// <param name="layers">Layer indices to set the material on (layer mode), or null.</param>
        public static ImportResult Import(RhinoDoc doc, ImportSettings settings, IList<RhinoObject> targets, RenderMaterial reuse, IList<int> layers = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (settings?.Image == null && settings?.SolidColor == null && reuse == null) throw new InvalidOperationException("No image or colour loaded.");
            bool solid = settings.SolidColor != null || (reuse != null && ProvenanceStore.Read(doc, reuse)?.IsSolidColor == true);

            var result = new ImportResult { Reused = reuse != null };

            // Bake maps before the undo record: it's file work, not document work.
            BakedMaps maps = null;
            if (reuse == null && settings.GenerateMaps && settings.SolidColor == null)
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

                IList<RhinoObject> toMap;
                if (layers != null)
                {
                    // Layer mode: the layer carries the material, so objects drawn later get it too.
                    foreach (var index in layers.Distinct())
                    {
                        var layer = doc.Layers.FindIndex(index);
                        if (layer == null || layer.IsDeleted) continue;
                        layer.RenderMaterial = result.Material;
                        doc.Layers.Modify(layer, index, true);
                        result.LayerCount++;
                    }
                    var onLayers = LayerObjects(doc, layers);
                    toMap = onLayers.Where(o => o.Attributes.MaterialSource == ObjectMaterialSource.MaterialFromLayer).ToList();
                    result.AssignedCount = toMap.Count;
                    result.KeptOwnMaterialCount = onLayers.Count - toMap.Count;
                }
                else
                {
                    toMap = new List<RhinoObject>();
                    foreach (var obj in targets)
                    {
                        if (obj == null || obj.IsDeleted) continue;
                        obj.RenderMaterial = result.Material;
                        obj.CommitChanges();
                        result.AssignedCount++;
                        toMap.Add(obj);
                    }
                }

                // Plain colours need no texture mapping.
                if (!solid) result.MappedCount = MappingApplier.Apply(doc, toMap, settings.Mapping);

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

        /// <summary>Renderable objects on the given layers (not sublayers).</summary>
        public static List<RhinoObject> LayerObjects(RhinoDoc doc, IEnumerable<int> layers)
        {
            const ObjectType renderable = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.SubD;
            var result = new List<RhinoObject>();
            foreach (var index in layers.Distinct())
            {
                var layer = doc.Layers.FindIndex(index);
                if (layer == null || layer.IsDeleted) continue;
                result.AddRange((doc.Objects.FindByLayer(layer) ?? Array.Empty<RhinoObject>()).Where(o => (o.ObjectType & renderable) != 0));
            }
            return result;
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
            pbr.Metallic = 0;
            pbr.Roughness = EnumText.Roughness(settings.Finish);

            var color = settings.SolidColor;
            if (color != null)
            {
                pbr.BaseColor = new Color4f(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
                if (color.Special == RalSpecial.Metallic)
                {
                    // Pearl and aluminium colours: a metallic flake look, never mirror-like.
                    pbr.Metallic = 0.6;
                    pbr.Roughness = Math.Max(0.3, Math.Min(pbr.Roughness, 0.45));
                }
            }
            else
            {
                pbr.BaseColor = new Color4f(1f, 1f, 1f, 1f); // don't tint the texture
                pbr.SetTexture(BitmapTexture(settings.Image.LocalPath), TextureType.PBR_BaseColor);
            }
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
