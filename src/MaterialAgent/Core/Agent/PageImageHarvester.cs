using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MaterialAgent.Core.Agent
{
    /// <summary>
    /// Deterministic fallback for image candidates: reads the product page's HTML and pulls out image URLs
    /// (og:image, JSON-LD, img src/srcset, direct image links), ranked by how likely they are to be the decor.
    /// The model often can't see real image URLs, so this is what keeps hallucinated URLs from mattering.
    /// </summary>
    public static class PageImageHarvester
    {
        const long MaxHtmlBytes = 4L * 1024 * 1024;

        static readonly Regex MetaImage = new Regex(
            @"<meta\b[^>]*?(?:property|name)\s*=\s*[""'](?:og:image(?::secure_url)?|twitter:image)[""'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ContentAttr = new Regex(@"\bcontent\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ImgTag = new Regex(@"<(?:img|source)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SrcAttr = new Regex(@"\b(?:data-zoom-image|data-large|data-full|data-original|data-src|data-lazy-src|src)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SrcSetAttr = new Regex(@"\b(?:data-srcset|srcset)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex HrefImage = new Regex(@"\bhref\s*=\s*[""']([^""']+\.(?:jpe?g|png|tiff?)(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex JsonLdImage = new Regex(@"""image""\s*:\s*(\[[^\]]*\]|""[^""]+"")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex QuotedUrl = new Regex(@"""(https?:[^""]+)""", RegexOptions.Compiled);

        static readonly string[] Junk = { "logo", "icon", "sprite", "favicon", "placeholder", "flag", "avatar", "badge", "pixel", "spacer", "blank.", "loader", "social", "payment", "/cms/icons", "1x1" };
        static readonly string[] Good = { "decor", "dekor", "texture", "swatch", "sample", "detail", "zoom", "large", "original", "full", "hires", "high-res", "download", "muster", "pattern", "surface" };
        static readonly string[] Weak = { "room", "interior", "ambient", "ambiente", "lifestyle", "inspiration", "scene", "thumb", "small", "teaser", "banner", "hero", "kitchen", "living" };

        public static async Task<IReadOnlyList<string>> HarvestAsync(HttpClient http, string pageUrl, string productCode, int max, CancellationToken ct)
        {
            if (!MaterialResolution.IsHttpUrl(pageUrl)) return Array.Empty<string>();
            using (var request = new HttpRequestMessage(HttpMethod.Get, pageUrl))
            {
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return Array.Empty<string>();
                    var bytes = await ImageFetcher.ReadLimitedAsync(response.Content, MaxHtmlBytes, ct).ConfigureAwait(false);
                    var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? pageUrl;
                    return Extract(Encoding.UTF8.GetString(bytes), finalUrl, productCode, max);
                }
            }
        }

        /// <summary>Extracts and ranks image URLs from HTML. Pure, for testing.</summary>
        public static IReadOnlyList<string> Extract(string html, string pageUrl, string productCode, int max)
        {
            if (string.IsNullOrEmpty(html) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri))
                return Array.Empty<string>();

            var found = new List<(string url, double bonus)>();

            foreach (Match m in MetaImage.Matches(html))
            {
                var c = ContentAttr.Match(m.Value);
                if (c.Success) found.Add((c.Groups[1].Value, 2.0));
            }
            foreach (Match m in JsonLdImage.Matches(html))
                foreach (Match u in QuotedUrl.Matches(m.Groups[1].Value))
                    found.Add((u.Groups[1].Value, 1.5));
            foreach (Match m in HrefImage.Matches(html))
                found.Add((m.Groups[1].Value, 2.5)); // explicit links to image files: usually downloads / full size
            foreach (Match tag in ImgTag.Matches(html))
            {
                var set = SrcSetAttr.Match(tag.Value);
                if (set.Success)
                {
                    var largest = LargestFromSrcSet(set.Groups[1].Value);
                    if (largest != null) found.Add((largest, 0.5));
                }
                foreach (Match s in SrcAttr.Matches(tag.Value))
                    found.Add((s.Groups[1].Value, s.Value.StartsWith("src", StringComparison.OrdinalIgnoreCase) ? 0 : 0.5));
            }

            var codeTokens = CodeTokens(productCode);
            var scored = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            int order = 0;
            foreach (var (raw, bonus) in found)
            {
                var url = Normalize(raw, baseUri);
                if (url == null) continue;
                double score = bonus + Score(url, codeTokens) - order++ * 0.001; // earlier on the page wins ties
                if (!scored.TryGetValue(url, out var existing) || score > existing) scored[url] = score;
            }

            return scored.Where(kv => kv.Value > -5)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .Take(max)
                .ToList();
        }

        static string Normalize(string raw, Uri baseUri)
        {
            var s = WebUtility.HtmlDecode(raw.Trim()).Replace("\\/", "/");
            if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            if (s.StartsWith("//", StringComparison.Ordinal)) s = baseUri.Scheme + ":" + s;
            if (!Uri.TryCreate(baseUri, s, out var u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            var path = u.AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".svg") || path.EndsWith(".gif") || path.EndsWith(".ico") || path.EndsWith(".js") || path.EndsWith(".css")) return null;
            return u.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        }

        static double Score(string url, IReadOnlyList<string> codeTokens)
        {
            var lower = Uri.UnescapeDataString(url).ToLowerInvariant();
            var squashed = Provenance.NormalizeCode(lower).ToLowerInvariant();
            double score = 0;
            foreach (var j in Junk) if (lower.Contains(j)) score -= 10;
            foreach (var g in Good) if (lower.Contains(g)) score += 1;
            foreach (var w in Weak) if (lower.Contains(w)) score -= 1;
            foreach (var t in codeTokens) if (squashed.Contains(t)) score += 3;
            if (Regex.IsMatch(lower, @"[?&](w|width)=([1-9]\d{3})")) score += 0.5;
            if (Regex.IsMatch(lower, @"[?&](w|width)=(\d{1,2}|[1-2]\d{2})(&|$)")) score -= 1.5;
            return score;
        }

        /// <summary>Distinctive parts of a product code: "H1145 ST10" → "h1145st10", "h1145", "1145".</summary>
        public static IReadOnlyList<string> CodeTokens(string code)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) return tokens;
            var whole = Provenance.NormalizeCode(code).ToLowerInvariant();
            if (whole.Length >= 3) tokens.Add(whole);
            foreach (var part in Regex.Split(code.ToLowerInvariant(), @"[\s\-_/]+"))
            {
                if (part.Length >= 4 && !tokens.Contains(part)) tokens.Add(part);
                var digits = Regex.Match(part, @"\d{3,}");
                if (digits.Success && !tokens.Contains(digits.Value)) tokens.Add(digits.Value);
            }
            return tokens;
        }

        static string LargestFromSrcSet(string srcset)
        {
            string best = null;
            double bestW = -1;
            foreach (var entry in srcset.Split(','))
            {
                var bits = entry.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (bits.Length == 0) continue;
                double w = 0;
                if (bits.Length > 1)
                {
                    var d = bits[1].TrimEnd('w', 'x', 'W', 'X');
                    double.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out w);
                }
                if (w > bestW) { bestW = w; best = bits[0]; }
            }
            return best;
        }
    }
}
