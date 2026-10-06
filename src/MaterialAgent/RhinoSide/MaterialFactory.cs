using System;
using System.Collections.Generic;
using System.Linq;
using MaterialAgent.Core;
using Rhino;
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
    }

    /// <summary>Creates (or reuses) the material and applies it to objects. Call on the UI thread.</summary>
    public static class MaterialFactory
    {
        public static ImportResult Import(RhinoDoc doc, ImportSettings settings, IList<RhinoObject> targets, RenderMaterial reuse)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (settings?.Image == null && reuse == null) throw new InvalidOperationException("No image loaded.");

            uint undo = doc.BeginUndoRecord("Material Agent import");
            try
            {
                var result = new ImportResult { Reused = reuse != null };
                result.Material = reuse ?? CreateRenderMaterial(doc, settings);

                foreach (var obj in targets)
                {
                    if (obj == null || obj.IsDeleted) continue;
                    obj.RenderMaterial = result.Material;
                    obj.CommitChanges();
                    result.AssignedCount++;
                }

                result.MappedCount = MappingApplier.Apply(doc, targets, settings.Mapping);

                if (!result.Reused)
                    WriteUserStrings(doc, result.Material, settings.Provenance);

                doc.Views.Redraw();
                return result;
            }
            finally
            {
                doc.EndUndoRecord(undo);
            }
        }

        static RenderMaterial CreateRenderMaterial(RhinoDoc doc, ImportSettings settings)
        {
            var texture = new Texture
            {
                FileName = settings.Image.LocalPath,
                WrapU = TextureUvwWrapping.Repeat,
                WrapV = TextureUvwWrapping.Repeat,
                WrapW = TextureUvwWrapping.Repeat,
            };
            // Texture.MappingChannelId is read-only; new textures use mapping channel 1 (MappingApplier.Channel).

            var material = new Material { Name = settings.MaterialName };
            material.SetBitmapTexture(texture);

            var rm = RenderMaterial.CreateBasicMaterial(material, doc);
            rm.Name = settings.MaterialName;
            if (settings.Provenance != null)
                rm.Notes = settings.Provenance.ToNotes();

            doc.RenderMaterials.Add(rm);
            // Add() may wrap or copy the content; return the instance that lives in the document.
            return doc.RenderMaterials.Find(rm.Id) ?? rm;
        }

        /// <summary>
        /// Writes provenance to the document Material's user strings. The material table entry only exists
        /// once the render material is used by an object, so this is best-effort; the render material's
        /// Notes always carry the same data.
        /// </summary>
        static void WriteUserStrings(RhinoDoc doc, RenderMaterial rm, Provenance provenance)
        {
            if (provenance == null || rm == null) return;
            foreach (var m in doc.Materials.Where(x => x != null && !x.IsDeleted && x.RenderMaterialInstanceId == rm.Id).ToList())
            {
                foreach (var kv in provenance.ToPairs())
                    m.SetUserString(kv.Key, kv.Value);
                doc.Materials.Modify(m, m.Index, true);
            }
        }
    }
}
