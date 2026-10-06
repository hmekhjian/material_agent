using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MaterialAgent.Core.Agent
{
    /// <summary>
    /// Agent pipeline on Gemini:
    /// 1. research call (Google Search + URL context) → MaterialResolution JSON, validated, one repair round;
    /// 2. code downloads the model's candidate URLs and harvests images from the product page;
    /// 3. a vision call ranks the downloaded images, reads grain and counts features;
    /// 4. code applies the scale evidence ladder.
    /// The model never touches image bytes it fetched itself; everything it sees was downloaded by code.
    /// </summary>
    public sealed class GeminiMaterialResolver : IMaterialResolver
    {
        /// <summary>Images larger than this are not sent to the vision call (cost and request size).</summary>
        const int MaxVisionImageBytes = 4 * 1024 * 1024;

        readonly GeminiClient _gemini;
        readonly HttpClient _web;
        readonly AgentSettings _settings;
        bool _structuredOutput = true;
        bool _thinkingConfig = true;

        public GeminiMaterialResolver(AgentSettings settings, HttpClient geminiHttp = null, HttpClient webHttp = null)
        {
            _settings = settings ?? new AgentSettings();
            _web = webHttp ?? ImageFetcher.Default;
            _gemini = new GeminiClient(geminiHttp ?? SharedGeminiHttp.Value, _settings.ApiKey, _settings.Model);
        }

        static readonly Lazy<HttpClient> SharedGeminiHttp = new Lazy<HttpClient>(() => new HttpClient { Timeout = TimeSpan.FromMinutes(4) });

        public async Task<ResolveResult> ResolveAsync(string query, IProgress<string> progress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Enter a product name or code.");
            var result = new ResolveResult { Query = query.Trim() };

            // 1. Research.
            progress?.Report("Searching the web for the product…");
            var resolution = await ResearchAsync(query, result, ct).ConfigureAwait(false);
            result.Resolution = resolution;

            // 2. Download candidates (model-suggested + harvested from the page).
            progress?.Report("Downloading candidate images…");
            await GatherCandidatesAsync(resolution, result, ct).ConfigureAwait(false);
            if (result.Candidates.Count == 0)
                throw new InvalidOperationException($"Found '{resolution.Product?.Name}' but could not download any usable image. Open the product page and paste an image URL instead.");

            // 3. Vision check.
            ScaleFeature visionFeature = null;
            GrainAxis? visionGrain = null;
            try
            {
                progress?.Report("Checking the images…");
                (visionFeature, visionGrain) = await RankWithVisionAsync(resolution, result, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Warnings.Add("Image check skipped: " + ex.Message);
            }

            // 4. Decide.
            var best = result.Candidates[0];
            result.Scale = ScaleLadder.Decide(resolution.Scale, visionFeature, best.Image.Aspect);
            EnumText.TryParseGrain(resolution.GrainAxis, out var grain);
            result.Grain = visionGrain ?? grain;
            EnumText.TryParseMapping(resolution.Mapping, out var mapping);
            result.Mapping = mapping;
            result.FinishKnown = EnumText.TryParseFinish(resolution.Finish, out var finish);
            result.Finish = finish;
            result.Category = resolution.Category;
            result.ResolvedUtc = DateTime.UtcNow;

            // Keep the resolution's candidate list in sync with what was actually usable (schema requires it).
            resolution.Candidates = result.Candidates.Select(c => new ImageCandidate { Url = c.Url, Kind = c.Kind, LikelyTileable = c.LikelyTileable, Note = c.Note }).ToList();
            return result;
        }

        // ------------------------------------------------------------------ research

        async Task<MaterialResolution> ResearchAsync(string query, ResolveResult result, CancellationToken ct)
        {
            var request = new GeminiRequest
            {
                SystemInstruction = Prompts.ResearchSystem,
                UseGoogleSearch = true,
                UseUrlContext = true,
                MaxOutputTokens = 4096,
            };
            request.Messages.Add(new GeminiMessage { Role = "user", Parts = { GeminiPart.FromText(Prompts.ResearchUser(query)) } });

            for (int round = 0; round < 2; round++)
            {
                var response = await SendAsync(request, Prompts.ResearchSchema(), result, ct).ConfigureAwait(false);
                foreach (var s in response.SearchSources)
                    if (!result.Sources.Any(x => x.Value == s.Value)) result.Sources.Add(s);

                string problem;
                var resolution = TryParseResolution(response.Text, out problem);
                if (resolution != null)
                {
                    // Candidates may legitimately be empty here: the page harvest fills them later.
                    var errors = resolution.Validate().Where(e => !e.StartsWith("candidates", StringComparison.Ordinal)).ToList();
                    if (errors.Count == 0)
                    {
                        resolution.Candidates = resolution.Candidates ?? new List<ImageCandidate>();
                        return resolution;
                    }
                    problem = string.Join("; ", errors);
                }

                if (round == 1) throw new InvalidOperationException("The agent's answer was unusable: " + problem);
                request.Messages.Add(new GeminiMessage { Role = "model", Parts = { GeminiPart.FromText(response.Text ?? "") } });
                request.Messages.Add(new GeminiMessage { Role = "user", Parts = { GeminiPart.FromText(Prompts.Repair(problem)) } });
            }
            throw new InvalidOperationException("unreachable");
        }

        public static MaterialResolution TryParseResolution(string text, out string problem)
        {
            problem = null;
            var json = JsonText.ExtractObject(text);
            if (json == null) { problem = "no JSON object found in the reply"; return null; }
            try
            {
                var r = MaterialResolution.FromJson(json);
                if (r == null) problem = "empty JSON";
                return r;
            }
            catch (JsonException ex)
            {
                problem = "invalid JSON (" + ex.Message + ")";
                return null;
            }
        }

        /// <summary>
        /// Sends a request with structured output and thinking config, and falls back without them if the
        /// configured model rejects either (older or lighter models).
        /// </summary>
        async Task<GeminiResponse> SendAsync(GeminiRequest request, JsonNode schema, ResolveResult result, CancellationToken ct)
        {
            while (true)
            {
                request.ResponseSchema = _structuredOutput ? schema : null;
                request.ThinkingLevel = _thinkingConfig ? _settings.ThinkingLevel : null;
                try
                {
                    var response = await _gemini.GenerateAsync(request, ct).ConfigureAwait(false);
                    result.Usage.Add(response.Usage);
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

        // ------------------------------------------------------------------ candidates

        async Task GatherCandidatesAsync(MaterialResolution resolution, ResolveResult result, CancellationToken ct)
        {
            int max = Math.Max(1, _settings.MaxCandidates);
            var urls = new List<(string url, ImageCandidate meta, bool fromPage)>();
            foreach (var c in resolution.Candidates ?? new List<ImageCandidate>())
                if (c != null && MaterialResolution.IsHttpUrl(c.Url) && !urls.Any(u => SameUrl(u.url, c.Url)))
                    urls.Add((c.Url.Trim(), c, false));

            try
            {
                var harvested = await PageImageHarvester.HarvestAsync(_web, resolution.Product.PageUrl, resolution.Product.Code ?? resolution.Product.Name, max * 2, ct).ConfigureAwait(false);
                foreach (var h in harvested)
                    if (!urls.Any(u => SameUrl(u.url, h))) urls.Add((h, null, true));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result.Warnings.Add("Product page timed out."); }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                result.Warnings.Add("Could not read the product page: " + ex.Message);
            }

            // Model suggestions first, then page images; download a few more than we show since some will fail.
            var attempts = urls.Take(max * 2).ToList();
            var tasks = attempts.Select(async a =>
            {
                try
                {
                    var img = await ImageFetcher.FetchAsync(a.url, _web, ct).ConfigureAwait(false);
                    // Tiny images are thumbnails or icons, useless as textures.
                    if (img.PixelWidth > 0 && (img.PixelWidth < 200 || img.PixelHeight < 200)) return null;
                    return new CandidateImage
                    {
                        Url = a.url,
                        Image = img,
                        Kind = a.meta?.Kind,
                        LikelyTileable = a.meta?.LikelyTileable ?? false,
                        Note = a.meta?.Note,
                        FromPage = a.fromPage,
                    };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    if (!a.fromPage)
                        lock (result.Warnings) result.Warnings.Add("Suggested image could not be downloaded: " + a.url);
                    return null;
                }
            }).ToList();

            var downloaded = await Task.WhenAll(tasks).ConfigureAwait(false);
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

        static bool SameUrl(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ vision

        async Task<(ScaleFeature, GrainAxis?)> RankWithVisionAsync(MaterialResolution resolution, ResolveResult result, CancellationToken ct)
        {
            var sendable = result.Candidates.Where(c => c.Image.Bytes.Length <= MaxVisionImageBytes).ToList();
            if (sendable.Count == 0) return (null, null);

            var message = new GeminiMessage { Role = "user" };
            message.Parts.Add(GeminiPart.FromText(Prompts.VisionUser(resolution)));
            for (int i = 0; i < sendable.Count; i++)
            {
                message.Parts.Add(GeminiPart.FromText($"Image {i}:"));
                message.Parts.Add(GeminiPart.FromImage(sendable[i].Image.Bytes, ImageFormat.MimeType(sendable[i].Image.Kind)));
            }
            var request = new GeminiRequest { SystemInstruction = Prompts.VisionSystem, MaxOutputTokens = 2048 };
            request.Messages.Add(message);

            var response = await SendAsync(request, Prompts.VisionSchema(), result, ct).ConfigureAwait(false);
            var json = JsonText.ExtractObject(response.Text);
            if (json == null) throw new InvalidOperationException("no JSON in the image check reply");
            var verdict = JsonSerializer.Deserialize<VisionVerdict>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (verdict == null) return (null, null);

            var score = new Dictionary<CandidateImage, double>();
            foreach (var c in result.Candidates) score[c] = c.Image.Bytes.Length > MaxVisionImageBytes ? -1 : 0;
            foreach (var v in verdict.Images ?? new List<VisionImage>())
            {
                if (v == null || v.Index < 0 || v.Index >= sendable.Count) continue;
                var c = sendable[v.Index];
                c.Kind = v.Kind;
                c.LikelyTileable = v.LikelyTileable;
                c.MatchesProduct = v.MatchesProduct;
                if (!string.IsNullOrWhiteSpace(v.Note)) c.Note = v.Note;
                double s = v.Kind == "swatch" ? 3 : v.Kind == "detail" ? 2 : v.Kind == "room" ? 1 : -2;
                if (v.LikelyTileable) s += 1;
                if (!v.MatchesProduct) s -= 5;
                score[c] = s;
            }
            CandidateImage best = verdict.BestIndex >= 0 && verdict.BestIndex < sendable.Count ? sendable[verdict.BestIndex] : null;
            if (best != null) score[best] += 10;

            // Stable sort: ties keep the original (model, then page) order.
            var ordered = result.Candidates.Select((c, i) => (c, i)).OrderByDescending(t => score[t.c]).ThenBy(t => t.i).Select(t => t.c).ToList();
            result.Candidates.Clear();
            result.Candidates.AddRange(ordered);

            if (best == null) return (null, null);
            GrainAxis? grain = EnumText.TryParseGrain(verdict.GrainAxis, out var g) ? g : (GrainAxis?)null;
            return (verdict.Feature?.IsUsable == true ? verdict.Feature : null, grain);
        }

        sealed class VisionVerdict
        {
            [System.Text.Json.Serialization.JsonPropertyName("images")] public List<VisionImage> Images { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("best_index")] public int BestIndex { get; set; } = -1;
            [System.Text.Json.Serialization.JsonPropertyName("grain_axis")] public string GrainAxis { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("feature")] public ScaleFeature Feature { get; set; }
        }

        sealed class VisionImage
        {
            [System.Text.Json.Serialization.JsonPropertyName("index")] public int Index { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("kind")] public string Kind { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("likely_tileable")] public bool LikelyTileable { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("matches_product")] public bool MatchesProduct { get; set; } = true;
            [System.Text.Json.Serialization.JsonPropertyName("note")] public string Note { get; set; }
        }
    }
}
