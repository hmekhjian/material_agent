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
    /// <summary>A product page downloaded by code: its readable text (for the model) and HTML (for image harvesting).</summary>
    public sealed class FetchedPage
    {
        public string Url { get; set; }
        public string FinalUrl { get; set; }
        public string Html { get; set; }
        public string Text { get; set; }
        public string Error { get; set; }
        public bool Ok => Error == null && Html != null;
    }

    /// <summary>
    /// Downloads pages and reduces them to the text a model needs: title, meta description, structured data
    /// (JSON-LD often has dimensions and descriptions) and visible text, with scripts, styles and repeated
    /// navigation removed. Much smaller than letting the model fetch the page itself.
    /// </summary>
    public static class PageReader
    {
        const long MaxHtmlBytes = 4L * 1024 * 1024;
        public const int MaxTextChars = 9000;

        public static async Task<FetchedPage> FetchAsync(HttpClient http, string url, CancellationToken ct)
        {
            var page = new FetchedPage { Url = url };
            if (!MaterialResolution.IsHttpUrl(url)) { page.Error = "not an http(s) URL"; return page; }
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) { page.Error = $"HTTP {(int)response.StatusCode}"; return page; }
                        var bytes = await ImageFetcher.ReadLimitedAsync(response.Content, MaxHtmlBytes, ct).ConfigureAwait(false);
                        page.FinalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
                        page.Html = Encoding.UTF8.GetString(bytes);
                        page.Text = ExtractText(page.Html, MaxTextChars);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                page.Error = ex is OperationCanceledException ? "timed out" : ex.Message;
            }
            return page;
        }

        static readonly Regex Title = new Regex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex MetaDescription = new Regex(@"<meta\b[^>]*(?:name|property)\s*=\s*[""'](?:description|og:description|og:title)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ContentAttr = new Regex(@"\bcontent\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex JsonLd = new Regex(@"<script[^>]*application/ld\+json[^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex Invisible = new Regex(@"<(script|style|noscript|svg|template|iframe)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex Comments = new Regex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex BlockTags = new Regex(@"<\s*/?\s*(p|div|li|tr|td|th|h[1-6]|br|section|article|dt|dd|table|ul|ol|header|footer|nav)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex AnyTag = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        static readonly Regex Spaces = new Regex(@"[ \t ]+", RegexOptions.Compiled);

        /// <summary>Readable text of an HTML page, at most <paramref name="maxChars"/> characters. Pure, for testing.</summary>
        public static string ExtractText(string html, int maxChars = MaxTextChars)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var sb = new StringBuilder();

            var title = Title.Match(html);
            if (title.Success) sb.Append("Title: ").AppendLine(Clean(title.Groups[1].Value));
            foreach (Match m in MetaDescription.Matches(html))
            {
                var c = ContentAttr.Match(m.Value);
                if (c.Success && c.Groups[1].Value.Trim().Length > 0) sb.Append("Meta: ").AppendLine(Clean(c.Groups[1].Value));
            }
            foreach (Match m in JsonLd.Matches(html))
            {
                var json = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
                if (json.Length > 2500) json = json.Substring(0, 2500) + "…";
                sb.Append("Structured data: ").AppendLine(json);
            }

            var body = Comments.Replace(Invisible.Replace(html, " "), " ");
            body = BlockTags.Replace(body, "\n");
            body = WebUtility.HtmlDecode(AnyTag.Replace(body, " "));

            // Drop empty and repeated lines (menus, footers and cookie banners repeat across a page).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            sb.AppendLine("Page text:");
            foreach (var raw in body.Split('\n'))
            {
                var line = Spaces.Replace(raw, " ").Trim();
                if (line.Length < 2 || !seen.Add(line)) continue;
                sb.AppendLine(line);
                if (sb.Length > maxChars) break;
            }

            var text = sb.ToString();
            return text.Length > maxChars ? text.Substring(0, maxChars) + "…" : text;
        }

        /// <summary>Characters of real page text (excluding our labels), to detect script-rendered pages.</summary>
        public static int ContentLength(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int i = text.IndexOf("Page text:", StringComparison.Ordinal);
            var meta = i >= 0 ? text.Substring(0, i) : "";
            var body = i >= 0 ? text.Substring(i + 10) : text;
            return body.Trim().Length + (meta.Contains("Structured data:") ? 400 : 0);
        }

        static string Clean(string s) => Spaces.Replace(WebUtility.HtmlDecode(AnyTag.Replace(s, " ")), " ").Trim();
    }
}
