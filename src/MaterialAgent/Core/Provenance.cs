using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MaterialAgent.Core
{
    /// <summary>
    /// Where a material came from. Written to the material's user strings (and mirrored into the
    /// render material's Notes) so the .3dm carries its own history and we can reuse materials
    /// instead of re-fetching them.
    /// </summary>
    public sealed class Provenance
    {
        public const string Prefix = "matagent.";

        public const string KeyProductCode = Prefix + "product_code";
        public const string KeyProductName = Prefix + "product_name";
        public const string KeyManufacturer = Prefix + "manufacturer";
        public const string KeyPageUrl = Prefix + "source_url";
        public const string KeyImageUrl = Prefix + "image_url";
        public const string KeyFetchDate = Prefix + "fetch_date";
        public const string KeyScaleSource = Prefix + "scale_source";
        public const string KeyScaleConfidence = Prefix + "scale_confidence";
        public const string KeyWidthMm = Prefix + "width_mm";
        public const string KeyHeightMm = Prefix + "height_mm";
        public const string KeyMapping = Prefix + "mapping";
        public const string KeyGrain = Prefix + "grain_axis";
        public const string KeyFinish = Prefix + "finish";
        public const string KeyRotate90 = Prefix + "rotate90";
        public const string KeyColor = Prefix + "color";
        public const string KeyCategory = Prefix + "category";

        const string NotesHeader = "[Material Agent provenance]";

        public string ProductCode { get; set; }
        public string ProductName { get; set; }
        public string Manufacturer { get; set; }
        public string PageUrl { get; set; }
        /// <summary>The image URL, or the local file path for a manually chosen file.</summary>
        public string ImageUrl { get; set; }
        public DateTime FetchDateUtc { get; set; }
        public ScaleSource ScaleSource { get; set; } = ScaleSource.User;
        public ScaleConfidence ScaleConfidence { get; set; } = ScaleConfidence.Low;
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public MappingKind Mapping { get; set; } = MappingKind.Box;
        public GrainAxis Grain { get; set; } = GrainAxis.None;
        public Finish Finish { get; set; } = Finish.Matt;
        public bool Rotate90 { get; set; }
        /// <summary>"#RRGGBB" for plain-colour materials (RAL etc.); null for textured ones.</summary>
        public string ColorHex { get; set; }
        public bool IsSolidColor => !string.IsNullOrEmpty(ColorHex);
        public string Category { get; set; }

        public IEnumerable<KeyValuePair<string, string>> ToPairs()
        {
            var ci = CultureInfo.InvariantCulture;
            yield return Pair(KeyProductCode, ProductCode);
            yield return Pair(KeyProductName, ProductName);
            yield return Pair(KeyManufacturer, Manufacturer);
            yield return Pair(KeyPageUrl, PageUrl);
            yield return Pair(KeyImageUrl, ImageUrl);
            yield return Pair(KeyFetchDate, FetchDateUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", ci));
            yield return Pair(KeyScaleSource, EnumText.ToWire(ScaleSource));
            yield return Pair(KeyScaleConfidence, EnumText.ToWire(ScaleConfidence));
            yield return Pair(KeyWidthMm, WidthMm.ToString("0.###", ci));
            yield return Pair(KeyHeightMm, HeightMm.ToString("0.###", ci));
            yield return Pair(KeyMapping, EnumText.ToWire(Mapping));
            yield return Pair(KeyGrain, EnumText.ToWire(Grain));
            yield return Pair(KeyFinish, EnumText.ToWire(Finish));
            yield return Pair(KeyRotate90, Rotate90 ? "true" : "false");
            yield return Pair(KeyColor, ColorHex);
            yield return Pair(KeyCategory, Category);
        }

        static KeyValuePair<string, string> Pair(string k, string v) => new KeyValuePair<string, string>(k, v ?? "");

        /// <summary>Builds provenance from key/value pairs. Returns null if no Material Agent keys are present.</summary>
        public static Provenance FromLookup(Func<string, string> get)
        {
            var code = get(KeyProductCode);
            var image = get(KeyImageUrl);
            var page = get(KeyPageUrl);
            if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(image) && string.IsNullOrEmpty(page))
                return null;

            var ci = CultureInfo.InvariantCulture;
            var p = new Provenance
            {
                ProductCode = NullIfEmpty(code),
                ProductName = NullIfEmpty(get(KeyProductName)),
                Manufacturer = NullIfEmpty(get(KeyManufacturer)),
                PageUrl = NullIfEmpty(page),
                ImageUrl = NullIfEmpty(image),
            };
            if (DateTime.TryParse(get(KeyFetchDate), ci, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                p.FetchDateUtc = d;
            if (EnumText.TryParseScaleSource(get(KeyScaleSource), out var s)) p.ScaleSource = s;
            if (EnumText.TryParseConfidence(get(KeyScaleConfidence), out var c)) p.ScaleConfidence = c;
            if (double.TryParse(get(KeyWidthMm), NumberStyles.Float, ci, out var w)) p.WidthMm = w;
            if (double.TryParse(get(KeyHeightMm), NumberStyles.Float, ci, out var h)) p.HeightMm = h;
            if (EnumText.TryParseMapping(get(KeyMapping), out var m)) p.Mapping = m;
            if (EnumText.TryParseGrain(get(KeyGrain), out var g)) p.Grain = g;
            if (EnumText.TryParseFinish(get(KeyFinish), out var f)) p.Finish = f;
            p.Rotate90 = string.Equals(get(KeyRotate90), "true", StringComparison.OrdinalIgnoreCase);
            p.ColorHex = NullIfEmpty(get(KeyColor));
            p.Category = NullIfEmpty(get(KeyCategory));
            return p;
        }

        /// <summary>Human-readable block for RenderContent.Notes. Round-trips through <see cref="FromNotes"/>.</summary>
        public string ToNotes()
        {
            var sb = new StringBuilder();
            sb.AppendLine(NotesHeader);
            foreach (var kv in ToPairs())
                sb.Append(kv.Key).Append('=').AppendLine(kv.Value.Replace("\r", " ").Replace("\n", " "));
            return sb.ToString();
        }

        public static Provenance FromNotes(string notes)
        {
            if (string.IsNullOrEmpty(notes) || notes.IndexOf(NotesHeader, StringComparison.Ordinal) < 0)
                return null;
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in notes.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (!line.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                dict[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return FromLookup(k => dict.TryGetValue(k, out var v) ? v : null);
        }

        /// <summary>
        /// True if this provenance refers to the same product (by code, case/space-insensitive)
        /// or the same image source.
        /// </summary>
        public bool Matches(string productCode, string imageUrl)
        {
            if (!string.IsNullOrWhiteSpace(productCode) && !string.IsNullOrWhiteSpace(ProductCode) &&
                NormalizeCode(productCode) == NormalizeCode(ProductCode))
                return true;
            if (!string.IsNullOrWhiteSpace(imageUrl) && !string.IsNullOrWhiteSpace(ImageUrl) &&
                string.Equals(imageUrl.Trim(), ImageUrl.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>
        /// True if a free-text search ("Egger H1145 ST10") names this material: its normalised product code
        /// appears in the normalised query, or the query equals the product name.
        /// </summary>
        public bool MatchesQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return false;
            var q = NormalizeCode(query);
            var code = NormalizeCode(ProductCode);
            if (code.Length >= 3 && q.Contains(code)) return true;
            return !string.IsNullOrWhiteSpace(ProductName) && q == NormalizeCode(ProductName);
        }

        public static string NormalizeCode(string code)
        {
            var sb = new StringBuilder();
            foreach (var ch in code ?? "")
                if (!char.IsWhiteSpace(ch) && ch != '-' && ch != '_')
                    sb.Append(char.ToUpperInvariant(ch));
            return sb.ToString();
        }

        static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
