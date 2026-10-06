using System.Linq;
using MaterialAgent.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.Render;

namespace MaterialAgent.RhinoSide
{
    /// <summary>Reads and writes provenance on render materials (Notes) and document materials (user strings).</summary>
    public static class ProvenanceStore
    {
        public static Provenance Read(RhinoDoc doc, RenderMaterial rm)
        {
            if (rm == null) return null;
            var p = Provenance.FromNotes(rm.Notes);
            if (p != null) return p;
            var m = doc?.Materials.FirstOrDefault(x => x != null && !x.IsDeleted && x.RenderMaterialInstanceId == rm.Id);
            return m == null ? null : Provenance.FromLookup(m.GetUserString);
        }

        public static void Write(RhinoDoc doc, RenderMaterial rm, Provenance provenance)
        {
            if (rm == null || provenance == null) return;
            var notes = provenance.ToNotes();
            if (rm.Notes != notes)
            {
                rm.BeginChange(RenderContent.ChangeContexts.Program);
                rm.Notes = notes;
                rm.EndChange();
            }
            WriteUserStrings(doc, rm, provenance);
        }

        /// <summary>
        /// The material table entry only exists once the render material is used by an object, so this is
        /// best-effort; the render material's Notes always carry the same data.
        /// </summary>
        public static void WriteUserStrings(RhinoDoc doc, RenderMaterial rm, Provenance provenance)
        {
            if (doc == null || provenance == null || rm == null) return;
            foreach (var m in doc.Materials.Where(x => x != null && !x.IsDeleted && x.RenderMaterialInstanceId == rm.Id).ToList())
            {
                foreach (var kv in provenance.ToPairs())
                    m.SetUserString(kv.Key, kv.Value);
                doc.Materials.Modify(m, m.Index, true);
            }
        }

        /// <summary>The mapping settings last used for a material, from its provenance.</summary>
        public static MappingSettings MappingFrom(Provenance p) => p == null || p.IsSolidColor || p.WidthMm <= 0 || p.HeightMm <= 0 ? null : new MappingSettings
        {
            WidthMm = p.WidthMm,
            HeightMm = p.HeightMm,
            Kind = p.Mapping,
            Grain = p.Grain,
            Rotate90 = p.Rotate90,
        };
    }
}
