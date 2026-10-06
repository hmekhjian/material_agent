using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MaterialAgent.Core.Colors
{
    public enum RalSystem
    {
        Classic,
        Design,
    }

    /// <summary>Special RAL Classic finishes a flat paint material can't show as-is.</summary>
    public enum RalSpecial
    {
        None,
        /// <summary>Pearl/metallic-effect colours (e.g. RAL 9006, 1035): rendered with some metalness.</summary>
        Metallic,
        /// <summary>Fluorescent colours (e.g. RAL 2005): brighter in reality than any screen colour.</summary>
        Luminous,
    }

    public sealed class RalColor
    {
        public RalSystem System { get; internal set; }
        /// <summary>"RAL 9010" or "RAL 210 50 15".</summary>
        public string Code { get; internal set; }
        public string Name { get; internal set; }
        public byte R { get; internal set; }
        public byte G { get; internal set; }
        public byte B { get; internal set; }
        /// <summary>Colour family for Classic ("white and black", "grey"...), empty for Design.</summary>
        public string Group { get; internal set; }
        public RalSpecial Special { get; internal set; }

        public string Hex => $"#{R:X2}{G:X2}{B:X2}";
        public string Display => $"{Code} {Name}";
        public override string ToString() => Display;
    }

    public sealed class RalMatch
    {
        public RalColor Color { get; set; }
        /// <summary>Other colours the query could mean (name searches), best first.</summary>
        public List<RalColor> Alternatives { get; set; } = new List<RalColor>();
    }

    /// <summary>
    /// Built-in RAL Classic (215) and RAL Design System (1825) colours, so "RAL 9010" needs no web search.
    /// Values are sRGB approximations of physical samples (see RalData.g.cs for the source).
    /// </summary>
    public static class RalCatalog
    {
        // Pearl and metallic RAL Classic colours (RAL 9006/9007 aluminium plus the pearl range).
        static readonly HashSet<string> MetallicCodes = new HashSet<string>
        {
            "1035", "1036", "2013", "3032", "3033", "4011", "4012", "5025", "5026",
            "6035", "6036", "7048", "8029", "9006", "9007", "9022", "9023",
        };
        static readonly HashSet<string> LuminousCodes = new HashSet<string> { "1026", "2005", "2007", "3024", "3026", "6038" };

        static readonly Lazy<List<RalColor>> ClassicList = new Lazy<List<RalColor>>(() =>
            RalData.Classic.Select(c => new RalColor
            {
                System = RalSystem.Classic,
                Code = "RAL " + c.code,
                Name = c.name,
                R = c.r, G = c.g, B = c.b,
                Group = c.group,
                Special = MetallicCodes.Contains(c.code) ? RalSpecial.Metallic : LuminousCodes.Contains(c.code) ? RalSpecial.Luminous : RalSpecial.None,
            }).ToList());

        static readonly Lazy<List<RalColor>> DesignList = new Lazy<List<RalColor>>(() =>
            RalData.Design.Select(c => new RalColor
            {
                System = RalSystem.Design,
                Code = $"RAL {c.h:000} {c.l:00} {c.c:00}",
                Name = c.name,
                R = c.r, G = c.g, B = c.b,
                Group = "",
            }).ToList());

        public static IReadOnlyList<RalColor> Classic => ClassicList.Value;
        public static IReadOnlyList<RalColor> Design => DesignList.Value;

        static readonly Regex RalPrefix = new Regex(@"^\s*RAL(?:\s*(?:classic|design|colou?r))?(?![a-z])[\s\-:#]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ClassicCode = new Regex(@"^(\d{4})(?!\d)", RegexOptions.Compiled);
        static readonly Regex DesignCode = new Regex(@"^(\d{3})[\s\-./]*(\d{2})[\s\-./]*(\d{2})(?!\d)", RegexOptions.Compiled);
        static readonly Regex DesignHlc = new Regex(@"^\s*H(\d{3})L(\d{2})C(\d{2})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Recognises "RAL 9010", "ral9010 pure white", "RAL-7016", "RAL 210 50 15", "RAL 2105015",
        /// "H210L50C15", and names after "RAL" ("RAL anthracite grey"). Returns false for anything else,
        /// so ordinary product searches go to the agent. A bare 4-digit number is not treated as RAL.
        /// </summary>
        public static bool TryMatch(string query, out RalMatch match)
        {
            match = null;
            if (string.IsNullOrWhiteSpace(query)) return false;

            var hlc = DesignHlc.Match(query);
            if (hlc.Success) return FindDesign(hlc, out match);

            var prefix = RalPrefix.Match(query);
            if (!prefix.Success) return false;
            var rest = query.Substring(prefix.Length).Trim();
            if (rest.Length == 0) return false;

            var design = DesignCode.Match(rest);
            if (design.Success && FindDesign(design, out match)) return true;

            var classic = ClassicCode.Match(rest);
            if (classic.Success)
            {
                var c = Classic.FirstOrDefault(x => x.Code == "RAL " + classic.Groups[1].Value);
                if (c == null) return false;
                match = new RalMatch { Color = c };
                return true;
            }

            var byName = SearchByName(rest).ToList();
            if (byName.Count == 0) return false;
            match = new RalMatch { Color = byName[0], Alternatives = byName.Skip(1).Take(8).ToList() };
            return true;
        }

        static bool FindDesign(Match m, out RalMatch match)
        {
            int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int l = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int c = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var code = $"RAL {h:000} {l:00} {c:00}";
            var color = Design.FirstOrDefault(x => x.Code == code);
            match = color == null ? null : new RalMatch { Color = color };
            return color != null;
        }

        /// <summary>Name search over Classic first, then Design: exact name, then all words present.</summary>
        public static IEnumerable<RalColor> SearchByName(string text)
        {
            var words = Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9äöüß']+").Where(w => w.Length > 0).ToArray();
            if (words.Length == 0) yield break;
            var phrase = string.Join(" ", words);
            var seen = new HashSet<RalColor>();
            foreach (var list in new[] { Classic, Design })
            {
                foreach (var c in list.Where(c => c.Name.ToLowerInvariant() == phrase))
                    if (seen.Add(c)) yield return c;
                foreach (var c in list.Where(c => words.All(w => c.Name.ToLowerInvariant().Contains(w))).OrderBy(c => c.Name.Length))
                    if (seen.Add(c)) yield return c;
            }
        }
    }
}
