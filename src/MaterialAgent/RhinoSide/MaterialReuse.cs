using System;
using System.Linq;
using MaterialAgent.Core;
using Rhino;
using Rhino.Render;

namespace MaterialAgent.RhinoSide
{
    public sealed class ExistingMaterial
    {
        public RenderMaterial Material { get; set; }
        public Provenance Provenance { get; set; }
    }

    /// <summary>Finds a material already in the open document for the same product code or image URL.</summary>
    public static class MaterialReuse
    {
        public static ExistingMaterial Find(RhinoDoc doc, string productCode, string imageUrl)
        {
            if (doc == null) return null;
            if (string.IsNullOrWhiteSpace(productCode) && string.IsNullOrWhiteSpace(imageUrl)) return null;

            // 1. Render material notes (always written).
            foreach (var rm in doc.RenderMaterials)
            {
                var p = Provenance.FromNotes(rm.Notes);
                if (p != null && p.Matches(productCode, imageUrl))
                    return new ExistingMaterial { Material = rm, Provenance = p };
            }

            // 2. Material table user strings (written when the material was assigned to objects).
            foreach (var m in doc.Materials.Where(x => x != null && !x.IsDeleted))
            {
                var p = Provenance.FromLookup(m.GetUserString);
                if (p == null || !p.Matches(productCode, imageUrl)) continue;
                var rm = m.RenderMaterialInstanceId != Guid.Empty ? doc.RenderMaterials.Find(m.RenderMaterialInstanceId) : null;
                if (rm != null) return new ExistingMaterial { Material = rm, Provenance = p };
            }

            return null;
        }
    }
}
