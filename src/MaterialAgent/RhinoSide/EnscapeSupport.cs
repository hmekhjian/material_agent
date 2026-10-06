using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Render;

namespace MaterialAgent.RhinoSide
{
    /// <summary>
    /// Optional conversion of our material into Enscape's own material type, so it appears in (and can be
    /// edited with) the Enscape Material Editor. Enscape doesn't publish an API or its type GUID, so we:
    /// find the Enscape material type among the render content types registered with Rhino, then use the
    /// same "Change Type, copy similar settings" conversion Rhino's material editor offers
    /// (Rhino.Render.Utilities.ChangeContentType with parameter harvesting).
    /// Per Enscape's docs/forum: Enscape renders Rhino PBR materials as they are (albedo, normal/bump, roughness
    /// value), uses Rhino's texture mapping, and applies its own texture scale on top (leave it at 1). Converting
    /// is only needed to edit the material in Enscape's editor. Enscape has no public API for materials.
    /// TODO verify in Rhino with Enscape installed: detection, and which settings survive harvesting
    /// (the docs don't say; Enscape's "Use Albedo" etc. suggest the albedo texture carries over).
    /// </summary>
    public static class EnscapeSupport
    {
        public sealed class TypeInfo
        {
            public Guid Id { get; set; }
            public string InternalName { get; set; }
        }

        static TypeInfo _detected;
        static bool _searched;

        /// <summary>A type ID typed into Settings, used instead of detection when set.</summary>
        public static Guid OverrideTypeId { get; set; }

        /// <summary>The Enscape material type, or null if Enscape isn't installed/loaded.</summary>
        public static TypeInfo MaterialType
        {
            get
            {
                if (OverrideTypeId != Guid.Empty) return new TypeInfo { Id = OverrideTypeId, InternalName = "(set in Settings)" };
                if (!_searched) { _detected = Detect(); _searched = true; }
                return _detected;
            }
        }

        /// <summary>Forget the cached result (e.g. after Enscape was loaded).</summary>
        public static void Redetect() { _searched = false; _detected = null; }

        static TypeInfo Detect()
        {
            try
            {
                var candidates = new List<TypeInfo>();
                foreach (var t in RenderContentType.GetAllAvailableTypes() ?? Array.Empty<RenderContentType>())
                {
                    var name = t.InternalName ?? "";
                    if (name.IndexOf("enscape", StringComparison.OrdinalIgnoreCase) >= 0)
                        candidates.Add(new TypeInfo { Id = t.Id, InternalName = name });
                }
                // Prefer types that say "material", then confirm by instantiating a temporary content.
                foreach (var c in candidates.OrderByDescending(c => c.InternalName.IndexOf("material", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    try
                    {
                        if (RenderContentType.NewContentFromTypeId(c.Id) is RenderMaterial) return c;
                    }
                    catch { /* not instantiable outside a document; try the next */ }
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Material Agent: could not list render content types: " + ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Replaces <paramref name="rm"/> (already in the document) with an Enscape material carrying over
        /// similar settings. Returns the new material, or the original plus a warning if conversion failed.
        /// </summary>
        public static (RenderMaterial material, string warning) Convert(RhinoDoc doc, RenderMaterial rm)
        {
            var type = MaterialType;
            if (type == null) return (rm, "Enscape is not installed or not loaded, so a standard Rhino material was created.");
            if (rm.TypeId == type.Id) return (rm, null);

            var name = rm.Name;
            var notes = rm.Notes;
            try
            {
                var replaced = Rhino.Render.Utilities.ChangeContentType(rm, type.Id, true) as RenderMaterial;
                var result = replaced == null ? null : doc.RenderMaterials.Find(replaced.Id) ?? replaced;
                if (result == null || result.TypeId != type.Id)
                    return (result ?? doc.RenderMaterials.Find(rm.Id) ?? rm, "Enscape conversion did not take; a standard Rhino material was kept.");

                // Harvesting copies render settings, not our name/notes (provenance), so restore them.
                result.BeginChange(RenderContent.ChangeContexts.Program);
                result.Name = name;
                result.Notes = notes;
                result.EndChange();
                return (result, null);
            }
            catch (Exception ex)
            {
                return (doc.RenderMaterials.Find(rm.Id) ?? rm, "Enscape conversion failed (" + ex.Message + "); a standard Rhino material was kept.");
            }
        }

        /// <summary>Prints all registered material types to the command line, to find Enscape's ID by hand.</summary>
        public static int ListMaterialTypes()
        {
            int n = 0;
            foreach (var t in RenderContentType.GetAllAvailableTypes() ?? Array.Empty<RenderContentType>())
            {
                bool isMaterial;
                try { isMaterial = RenderContentType.NewContentFromTypeId(t.Id) is RenderMaterial; }
                catch { isMaterial = false; }
                if (!isMaterial) continue;
                RhinoApp.WriteLine($"Material type: {t.InternalName}  {t.Id}  (plug-in {t.PlugInId})");
                n++;
            }
            return n;
        }
    }
}
