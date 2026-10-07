using System;
using System.Collections.Generic;
using System.Linq;

namespace MaterialAgent.Core
{
    /// <summary>What Rhino tells us about one registered material type.</summary>
    public sealed class MaterialTypeInfo
    {
        public Guid Id { get; set; }
        /// <summary>RDK internal name; third-party renderers often register a bare GUID here.</summary>
        public string InternalName { get; set; }
        /// <summary>Display name shown in Rhino's material editor ("Physically Based", "Enscape"...).</summary>
        public string TypeName { get; set; }
        public Guid PlugInId { get; set; }
        public string PlugInName { get; set; }
        public string PlugInFile { get; set; }
        public string PlugInOrganization { get; set; }
    }

    /// <summary>
    /// Picks Enscape's material type from the registered types. Internal names are often GUIDs, so this also
    /// looks at the display name and the owning plug-in's name, file and organisation.
    /// </summary>
    public static class EnscapeMatcher
    {
        public static MaterialTypeInfo Pick(IEnumerable<MaterialTypeInfo> types)
        {
            return types?
                .Select(t => (t, score: Score(t)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Select(x => x.t)
                .FirstOrDefault();
        }

        public static int Score(MaterialTypeInfo t)
        {
            if (t == null) return 0;
            int score = 0;
            if (Has(t.TypeName, "enscape")) score += 8;
            if (Has(t.InternalName, "enscape")) score += 8;
            if (Has(t.PlugInName, "enscape")) score += 4;
            if (Has(t.PlugInFile, "enscape")) score += 4;
            if (Has(t.PlugInOrganization, "enscape")) score += 2;
            if (score == 0) return 0;
            // Within Enscape's types, prefer the general material over special ones.
            if (Has(t.TypeName, "material") || Has(t.InternalName, "material")) score += 1;
            return score;
        }

        static bool Has(string s, string word) => s != null && s.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
