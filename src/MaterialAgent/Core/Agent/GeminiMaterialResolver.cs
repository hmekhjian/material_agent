using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MaterialAgent.Core.Bricks;

namespace MaterialAgent.Core.Agent
{
    /// <summary>
    /// Agent pipeline on Gemini, built for speed and low cost:
    /// 1. locate: one search-only call finds the product page (+ up to 3 alternatives), minimal thinking;
    /// 2. read: code downloads those pages, extracts their text and images, and downloads the images;
    /// 3. analyse: one call with no tools gets the page text and downscaled images and returns everything
    ///    (image classification, best texture, scale evidence, grain, finish, mapping, brick size).
    /// If the pages are script-rendered (no readable text), step 3 falls back to Gemini's URL context tool.
    /// Code applies the scale evidence ladder. Results are cached for the session.
    /// </summary>
    public sealed class GeminiMaterialResolver : IMaterialResolver
    {
        const int MaxVisionImageBytes = 4 * 1024 * 1024;
        const int MaxOtherPages = 3;
        /// <summary>Below this much readable text across all pages, assume they're rendered by script.</summary>
        const int ThinPageChars = 150;
        static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

        static readonly ConcurrentDictionary<string, (DateTime at, ResolveResult result)> Cache =
            new ConcurrentDictionary<string, (DateTime, ResolveResult)>();

        readonly GeminiClient _gemini;
        GeminiClient _locator;
        readonly HttpClient _web;
        readonly AgentSettings _settings;
        bool _structuredOutput = true;
        bool _thinkingConfig = true;

        public GeminiMaterialResolver(AgentSettings settings, HttpClient geminiHttp = null, HttpClient webHttp = null)
        {
            _settings = settings ?? new AgentSettings();
            _web = webHttp ?? ImageFetcher.Default;
            _gemini = new GeminiClient(geminiHttp ?? SharedHttp, _settings.ApiKey, _settings.Model);
            _locator = string.IsNullOrWhiteSpace(_settings.LocateModel) || _settings.LocateModel.Trim() == _gemini.Model
                ? _gemini
                : new GeminiClient(geminiHttp ?? SharedHttp, _settings.ApiKey, _settings.LocateModel);
        }

        /// <summary>Shared HTTP client for Gemini calls.</summary>
        public static HttpClient SharedHttp => SharedGeminiHttp.Value;

        static readonly Lazy<HttpClient> SharedGeminiHttp = new Lazy<HttpClient>(() => new HttpClient { Timeout = TimeSpan.FromMinutes(4) });

        public static void ClearCache() => Cache.Clear();

        public async Task<ResolveResult> ResolveAsync(string query, IProgress<string> progress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Enter a product name or code.");
            var key = _gemini.Model + "|" + Provenance.NormalizeCode(query);
            if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.at < CacheLifetime)
            {
                hit.result.FromCache = true;
                return hit.result;
            }

            var result = new ResolveResult { Query = query.Trim() };
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // 1. Locate.
            progress?.Report("Finding the product page…");
            var located = await LocateAsync(query, result, ct).ConfigureAwait(false);
            Lap(result, "search", clock);

            // 2. Read pages and download images (all in parallel).
            progress?.Report("Reading the product pages…");
            var urls = new[] { located.Product.PageUrl }.Concat(located.OtherPages ?? new List<string>())
                .Where(MaterialResolution.IsHttpUrl).Distinct(StringComparer.OrdinalIgnoreCase).Take(1 + MaxOtherPages).ToList();
            var pages = (await Task.WhenAll(urls.Select(u => PageReader.FetchAsync(_web, u, ct))).ConfigureAwait(false)).ToList();
            foreach (var p in pages.Where(p => !p.Ok)) result.Warnings.Add($"Could not read {p.Url}: {p.Error}");
            await DownloadCandidatesAsync(pages, located.Product, result, ct).ConfigureAwait(false);
            Lap(result, "pages", clock);

            // 3. Analyse text + images in one call.
            progress?.Report("Analysing the page and images…");
            bool unreadable = pages.Sum(p => PageReader.ContentLength(p.Text)) < ThinPageChars;
            var analysis = await AnalyseAsync(query, located, pages, result, unreadable, ct).ConfigureAwait(false);
            Lap(result, "analysis", clock);

            Apply(analysis, located, result);
            Cache[key] = (DateTime.UtcNow, result);
            return result;
        }

        static void Lap(ResolveResult result, string phase, System.Diagnostics.Stopwatch clock)
        {
            result.Timings.Add(new KeyValuePair<string, TimeSpan>(phase, clock.Elapsed));
            clock.Restart();
        }

        // ------------------------------------------------------------------ 1. locate

        sealed class Located
        {
            [JsonPropertyName("product")] public ProductInfo Product { get; set; }
            [JsonPropertyName("other_pages")] public List<string> OtherPages { get; set; }
            [JsonPropertyName("category")] public string Category { get; set; }
        }

        async Task<Located> LocateAsync(string query, ResolveResult result, CancellationToken ct)
        {
            var request = new GeminiRequest { SystemInstruction = Prompts.LocateSystem, UseGoogleSearch = true, MaxOutputTokens = 1024 };
            request.Messages.Add(new GeminiMessage { Role = "user", Parts = { GeminiPart.FromText(Prompts.LocateUser(query)) } });

            for (int round = 0; round < 2; round++)
            {
                GeminiResponse response;
                try
                {
                    response = await SendAsync(request, Prompts.LocateSchema(), result, ct, "minimal", _locator).ConfigureAwait(false);
                }
                catch (GeminiApiException ex) when (_locator != _gemini && ((int)ex.Status == 400 || (int)ex.Status == 404))
                {
                    // The fast model isn't available (or doesn't support search) for this key: use the main model.
                    result.Warnings.Add($"Page-finding model {_locator.Model} failed ({(int)ex.Status}); used {_gemini.Model}.");
                    _locator = _gemini;
                    _structuredOutput = _thinkingConfig = true; // the fast model's failures say nothing about the main model
                    response = await SendAsync(request, Prompts.LocateSchema(), result, ct, "minimal", _locator).ConfigureAwait(false);
                }
                foreach (var s in response.SearchSources)
                    if (!result.Sources.Any(x => x.Value == s.Value)) result.Sources.Add(s);

                var located = Parse<Located>(response.Text, out var problem);
                if (located != null)
                {
                    if (located.Product == null || string.IsNullOrWhiteSpace(located.Product.Name)) problem = "product.name is required";
                    else if (!MaterialResolution.IsHttpUrl(located.Product.PageUrl)) problem = "product.page_url must be an http(s) URL";
                    else return located;
                }
                if (round == 1) throw new InvalidOperationException("Couldn't find a product page: " + problem);
                request.Messages.Add(new GeminiMessage { Role = "model", Parts = { GeminiPart.FromText(response.Text ?? "") } });
                request.Messages.Add(new GeminiMessage { Role = "user", Parts = { GeminiPart.FromText(Prompts.Repair(problem)) } });
            }
            throw new InvalidOperationException("unreachable");
        }

        // ------------------------------------------------------------------ 2. images

        async Task DownloadCandidatesAsync(List<FetchedPage> pages, ProductInfo product, ResolveResult result, CancellationToken ct)
        {
            int max = Math.Max(1, _settings.MaxCandidates);
            var code = product.Code ?? product.Name;
            // Best images per page, interleaved so one page can't crowd out the others.
            var perPage = pages.Where(p => p.Ok)
                .Select(p => PageImageHarvester.Extract(p.Html, p.FinalUrl ?? p.Url, code, max).ToList()).ToList();
            var urls = new List<string>();
            for (int i = 0; urls.Count < max * 2 && perPage.Any(l => i < l.Count); i++)
                foreach (var list in perPage)
                    if (i < list.Count && !urls.Contains(list[i], StringComparer.OrdinalIgnoreCase)) urls.Add(list[i]);

            var downloaded = await Task.WhenAll(urls.Take(max * 2).Select(async url =>
            {
                try
                {
                    var img = await ImageFetcher.FetchAsync(url, _web, ct).ConfigureAwait(false);
                    // Tiny images are thumbnails or icons, useless as textures.
                    if (img.PixelWidth > 0 && (img.PixelWidth < 200 || img.PixelHeight < 200)) return null;
                    return new CandidateImage { Url = url, Image = img, FromPage = true };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { return null; }
            })).ConfigureAwait(false);

            // Drop byte-identical duplicates (same image under two URLs).
            var seen = new HashSet<string>();
            foreach (var c in downloaded)
            {
                if (c == null) continue;
                var key = c.Image.Bytes.Length + ":" + Convert.ToBase64String(c.Image.Bytes, 0, Math.Min(64, c.Image.Bytes.Length));
                if (seen.Add(key)) result.Candidates.Add(c);
                if (result.Candidates.Count >= max) break;
            }
        }

        // ------------------------------------------------------------------ 3. analyse

        sealed class Analysis
        {
            [JsonPropertyName("product")] public ProductInfo Product { get; set; }
            [JsonPropertyName("images")] public List<ImageVerdict> Images { get; set; }
            [JsonPropertyName("best_index")] public int BestIndex { get; set; } = -1;
            [JsonPropertyName("scale")] public ScaleInfo Scale { get; set; }
            [JsonPropertyName("grain_axis")] public string GrainAxis { get; set; }
            [JsonPropertyName("mapping")] public string Mapping { get; set; }
            [JsonPropertyName("finish")] public string Finish { get; set; }
            [JsonPropertyName("category")] public string Category { get; set; }
            [JsonPropertyName("brick")] public BrickUnit Brick { get; set; }
        }

        sealed class ImageVerdict
        {
            [JsonPropertyName("index")] public int Index { get; set; }
            [JsonPropertyName("kind")] public string Kind { get; set; }
            [JsonPropertyName("likely_tileable")] public bool LikelyTileable { get; set; }
            [JsonPropertyName("matches_product")] public bool MatchesProduct { get; set; } = true;
            [JsonPropertyName("note")] public string Note { get; set; }
        }

        async Task<Analysis> AnalyseAsync(string query, Located located, List<FetchedPage> pages, ResolveResult result, bool unreadable, CancellationToken ct)
        {
            var sendable = result.Candidates.Where(c => c.Image.Bytes.Length <= MaxVisionImageBytes).ToList();
            var message = new GeminiMessage { Role = "user" };
            message.Parts.Add(GeminiPart.FromText(Prompts.AnalyseIntro(query, located.Product, located.Category, sendable.Count, unreadable)));
            for (int i = 0; i < pages.Count; i++)
                if (pages[i].Ok && !string.IsNullOrWhiteSpace(pages[i].Text))
                    message.Parts.Add(GeminiPart.FromText(Prompts.PageBlock(i, pages[i].FinalUrl ?? pages[i].Url, pages[i].Text)));
            for (int i = 0; i < sendable.Count; i++)
            {
                // Downscaled copies: classification doesn't need full resolution, and smaller uploads are faster and cheaper.
                var small = ImagePrep.ForVision(sendable[i].Image.Bytes);
                message.Parts.Add(GeminiPart.FromText($"Image {i} (from {sendable[i].Url}):"));
                message.Parts.Add(GeminiPart.FromImage(small, ImageFormat.MimeType(ImageFormat.Sniff(small))));
            }

            var request = new GeminiRequest { SystemInstruction = Prompts.AnalyseSystem, UseUrlContext = unreadable, MaxOutputTokens = 4096 };
            request.Messages.Add(message);

            for (int round = 0; round < 2; round++)
            {
                var response = await SendAsync(request, Prompts.AnalyseSchema(), result, ct, "minimal").ConfigureAwait(false);
                var analysis = Parse<Analysis>(response.Text, out var problem);
                if (analysis != null)
                {
                    problem = ValidateAnalysis(analysis);
                    if (problem == null)
                    {
                        ApplyVerdicts(analysis, sendable, result);
                        return analysis;
                    }
                }
                if (round == 1) throw new InvalidOperationException("The agent's answer was unusable: " + problem);
                request.Messages.Add(new GeminiMessage { Role = "model", Parts = { GeminiPart.FromText(response.Text ?? "") } });
                request.Messages.Add(new GeminiMessage { Role = "user", Parts = { GeminiPart.FromText(Prompts.Repair(problem)) } });
            }
            throw new InvalidOperationException("unreachable");
        }

        static string ValidateAnalysis(Analysis a)
        {
            var errors = new List<string>();
            if (a.Scale == null) errors.Add("scale is missing");
            else
            {
                if (!(a.Scale.WidthMm > 0) || !(a.Scale.HeightMm > 0)) errors.Add("scale.width_mm and height_mm must be > 0");
                if (!EnumText.TryParseScaleSource(a.Scale.Source, out var src) || src == ScaleSource.User) errors.Add("scale.source must be page_text|image_feature|category_prior");
                if (!EnumText.TryParseConfidence(a.Scale.Confidence, out _)) errors.Add("scale.confidence must be high|medium|low");
            }
            if (!EnumText.TryParseMapping(a.Mapping, out _)) errors.Add("mapping must be planar|box|per_face");
            return errors.Count == 0 ? null : string.Join("; ", errors);
        }

        static void ApplyVerdicts(Analysis a, List<CandidateImage> sendable, ResolveResult result)
        {
            var score = result.Candidates.ToDictionary(c => c, c => c.Image.Bytes.Length > MaxVisionImageBytes ? -1.0 : 0.0);
            foreach (var v in a.Images ?? new List<ImageVerdict>())
            {
                if (v == null || v.Index < 0 || v.Index >= sendable.Count) continue;
                var c = sendable[v.Index];
                c.Kind = v.Kind;
                c.LikelyTileable = v.LikelyTileable;
                c.MatchesProduct = v.MatchesProduct;
                c.Note = v.Note;
                double s = v.Kind == "swatch" ? 3 : v.Kind == "detail" ? 2 : v.Kind == "room" ? 1 : -2;
                if (v.LikelyTileable) s += 1;
                if (!v.MatchesProduct) s -= 5;
                score[c] = s;
            }
            if (a.BestIndex >= 0 && a.BestIndex < sendable.Count) score[sendable[a.BestIndex]] += 10;
            var ordered = result.Candidates.Select((c, i) => (c, i)).OrderByDescending(t => score[t.c]).ThenBy(t => t.i).Select(t => t.c).ToList();
            result.Candidates.Clear();
            result.Candidates.AddRange(ordered);
        }

        void Apply(Analysis a, Located located, ResolveResult result)
        {
            bool classified = a.Images != null && a.Images.Count > 0;
            SplitTexturesFromReferences(result, checkedByVision: classified || result.Candidates.Count == 0);
            if (result.Candidates.Count == 0)
                result.Warnings.Add(result.References.Count > 0
                    ? "Only room or perspective photos were found, no flat texture. Use Generate seamless (AI) to make one from them, or paste an image URL."
                    : "No usable product image could be downloaded. Paste an image URL under Use your own image.");

            var product = a.Product ?? located.Product;
            if (string.IsNullOrWhiteSpace(product.Name)) product.Name = located.Product.Name;
            if (!MaterialResolution.IsHttpUrl(product.PageUrl)) product.PageUrl = located.Product.PageUrl;
            if (string.IsNullOrWhiteSpace(product.Code)) product.Code = located.Product.Code;
            if (string.IsNullOrWhiteSpace(product.Manufacturer)) product.Manufacturer = located.Product.Manufacturer;

            var best = result.Candidates.FirstOrDefault();
            var feature = best != null && a.Scale?.Feature?.IsUsable == true ? a.Scale.Feature : null;
            result.Scale = ScaleLadder.Decide(a.Scale, feature, best?.Image.Aspect ?? 0);
            EnumText.TryParseGrain(a.GrainAxis, out var grain);
            result.Grain = grain;
            EnumText.TryParseMapping(a.Mapping, out var mapping);
            result.Mapping = mapping;
            result.FinishKnown = EnumText.TryParseFinish(a.Finish, out var finish);
            result.Finish = finish;
            result.Category = string.IsNullOrWhiteSpace(a.Category) ? located.Category : a.Category;
            result.Brick = a.Brick?.IsUsable == true ? a.Brick : null;
            result.ResolvedUtc = DateTime.UtcNow;

            result.Resolution = new MaterialResolution
            {
                Product = product,
                Candidates = result.Candidates.Select(c => new ImageCandidate { Url = c.Url, Kind = c.Kind, LikelyTileable = c.LikelyTileable, Note = c.Note }).ToList(),
                Scale = a.Scale,
                GrainAxis = a.GrainAxis,
                Mapping = a.Mapping,
                Category = result.Category,
                Finish = a.Finish,
            };
        }

        /// <summary>
        /// Moves anything that isn't a usable texture out of <see cref="ResolveResult.Candidates"/>:
        /// room/perspective shots, unrelated images and images of a different product.
        /// </summary>
        public static void SplitTexturesFromReferences(ResolveResult result, bool checkedByVision)
        {
            bool IsTexture(CandidateImage c) => c.MatchesProduct && c.Kind != "room" && c.Kind != "other";

            var keep = result.Candidates.Where(IsTexture).ToList();
            result.References.AddRange(result.Candidates.Where(c => !IsTexture(c)));
            result.Candidates.Clear();
            result.Candidates.AddRange(keep);
            if (!checkedByVision && keep.Count > 0)
                result.Warnings.Add("Images were not checked by the vision model; some may still be room shots.");
        }

        // ------------------------------------------------------------------ plumbing

        static T Parse<T>(string text, out string problem) where T : class
        {
            problem = null;
            var json = JsonText.ExtractObject(text);
            if (json == null) { problem = "no JSON object found in the reply"; return null; }
            try
            {
                var value = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true });
                if (value == null) problem = "empty JSON";
                return value;
            }
            catch (JsonException ex)
            {
                problem = "invalid JSON (" + ex.Message + ")";
                return null;
            }
        }

        /// <summary>
        /// Sends with structured output and a thinking level, falling back without them if the configured model
        /// rejects either (older or lighter models).
        /// </summary>
        async Task<GeminiResponse> SendAsync(GeminiRequest request, JsonNode schema, ResolveResult result, CancellationToken ct, string thinkingLevel, GeminiClient client = null)
        {
            client = client ?? _gemini;
            while (true)
            {
                request.ResponseSchema = _structuredOutput ? schema : null;
                request.ThinkingLevel = _thinkingConfig ? thinkingLevel ?? _settings.ThinkingLevel : null;
                try
                {
                    var response = await client.GenerateAsync(request, ct).ConfigureAwait(false);
                    result.Usage.Add(response.Usage);
                    result.ModelCalls++;
                    return response;
                }
                catch (GeminiApiException ex) when (ex.Status == HttpStatusCode.BadRequest && (_structuredOutput || _thinkingConfig))
                {
                    var msg = ex.Message.ToLowerInvariant();
                    if (_thinkingConfig && msg.Contains("think")) _thinkingConfig = false;
                    else if (_structuredOutput) _structuredOutput = false;
                    else _thinkingConfig = false;
                }
            }
        }
    }
}
