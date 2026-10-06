using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MaterialAgent.Core.Agent
{
    public sealed class GeminiPart
    {
        public string Text { get; set; }
        public string MimeType { get; set; }
        public byte[] Data { get; set; }

        public static GeminiPart FromText(string text) => new GeminiPart { Text = text };
        public static GeminiPart FromImage(byte[] data, string mimeType) => new GeminiPart { Data = data, MimeType = mimeType };
    }

    public sealed class GeminiMessage
    {
        /// <summary>"user" or "model".</summary>
        public string Role { get; set; }
        public List<GeminiPart> Parts { get; set; } = new List<GeminiPart>();
    }

    public sealed class GeminiRequest
    {
        public string SystemInstruction { get; set; }
        public List<GeminiMessage> Messages { get; set; } = new List<GeminiMessage>();
        public bool UseGoogleSearch { get; set; }
        public bool UseUrlContext { get; set; }
        /// <summary>JSON schema for structured output, or null for free text.</summary>
        public JsonNode ResponseSchema { get; set; }
        public string ThinkingLevel { get; set; }
        public int MaxOutputTokens { get; set; } = 8192;
    }

    public sealed class GeminiUsage
    {
        public int PromptTokens { get; set; }
        public int OutputTokens { get; set; }
        public int ThoughtTokens { get; set; }
        public int ToolPromptTokens { get; set; }
        public int Total => PromptTokens + OutputTokens + ThoughtTokens + ToolPromptTokens;

        public void Add(GeminiUsage other)
        {
            if (other == null) return;
            PromptTokens += other.PromptTokens;
            OutputTokens += other.OutputTokens;
            ThoughtTokens += other.ThoughtTokens;
            ToolPromptTokens += other.ToolPromptTokens;
        }
    }

    public sealed class GeminiResponse
    {
        public string Text { get; set; }
        public string FinishReason { get; set; }
        public GeminiUsage Usage { get; set; } = new GeminiUsage();
        /// <summary>Pages Google Search grounded the answer on (title + redirect URI).</summary>
        public List<KeyValuePair<string, string>> SearchSources { get; } = new List<KeyValuePair<string, string>>();
        /// <summary>URLs the URL context tool fetched successfully.</summary>
        public List<string> FetchedUrls { get; } = new List<string>();
        public List<string> SearchQueries { get; } = new List<string>();
    }

    public sealed class GeminiApiException : Exception
    {
        public GeminiApiException(HttpStatusCode status, string message) : base(message) { Status = status; }
        public HttpStatusCode Status { get; }
    }

    /// <summary>Minimal REST client for the Gemini API generateContent endpoint.</summary>
    public sealed class GeminiClient
    {
        public const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

        readonly HttpClient _http;
        readonly string _apiKey;
        readonly string _model;

        public GeminiClient(HttpClient http, string apiKey, string model)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException($"No Gemini API key. Set the {AgentSettings.EnvApiKey} environment variable or enter a key in the panel's settings.");
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _apiKey = apiKey.Trim();
            _model = string.IsNullOrWhiteSpace(model) ? AgentSettings.DefaultModel : model.Trim();
        }

        public string Model => _model;

        public async Task<GeminiResponse> GenerateAsync(GeminiRequest request, CancellationToken ct)
        {
            var body = BuildBody(request).ToJsonString();
            for (int attempt = 0; ; attempt++)
            {
                using (var msg = new HttpRequestMessage(HttpMethod.Post, BaseUrl + Uri.EscapeDataString(_model) + ":generateContent"))
                {
                    msg.Headers.Add("x-goog-api-key", _apiKey);
                    msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await _http.SendAsync(msg, ct).ConfigureAwait(false))
                    {
                        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                            return ParseResponse(text);

                        var status = response.StatusCode;
                        bool retryable = (int)status == 429 || (int)status >= 500;
                        if (retryable && attempt < 2)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct).ConfigureAwait(false);
                            continue;
                        }
                        throw new GeminiApiException(status, $"Gemini API error {(int)status}: {ErrorMessage(text)}");
                    }
                }
            }
        }

        /// <summary>
        /// Checks the key and model name without spending tokens (GET models/{model}).
        /// Returns the model's display name.
        /// </summary>
        public async Task<string> CheckAsync(CancellationToken ct)
        {
            using (var msg = new HttpRequestMessage(HttpMethod.Get, BaseUrl + Uri.EscapeDataString(_model)))
            {
                msg.Headers.Add("x-goog-api-key", _apiKey);
                using (var response = await _http.SendAsync(msg, ct).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var status = (int)response.StatusCode;
                        var hint = status == 404 ? $" (no model called '{_model}')" : status == 400 || status == 401 || status == 403 ? " (check the key)" : "";
                        throw new GeminiApiException(response.StatusCode, $"Gemini API error {status}{hint}: {ErrorMessage(text)}");
                    }
                    try { return JsonNode.Parse(text)?["displayName"]?.GetValue<string>() ?? _model; }
                    catch (JsonException) { return _model; }
                }
            }
        }

        public static JsonObject BuildBody(GeminiRequest r)
        {
            var body = new JsonObject();
            if (!string.IsNullOrEmpty(r.SystemInstruction))
                body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = r.SystemInstruction }) };

            var contents = new JsonArray();
            foreach (var m in r.Messages)
            {
                var parts = new JsonArray();
                foreach (var p in m.Parts)
                {
                    if (p.Data != null)
                        parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = p.MimeType, ["data"] = Convert.ToBase64String(p.Data) } });
                    else
                        parts.Add(new JsonObject { ["text"] = p.Text ?? "" });
                }
                contents.Add(new JsonObject { ["role"] = m.Role ?? "user", ["parts"] = parts });
            }
            body["contents"] = contents;

            var tools = new JsonArray();
            if (r.UseGoogleSearch) tools.Add(new JsonObject { ["googleSearch"] = new JsonObject() });
            if (r.UseUrlContext) tools.Add(new JsonObject { ["urlContext"] = new JsonObject() });
            if (tools.Count > 0) body["tools"] = tools;

            var config = new JsonObject { ["maxOutputTokens"] = r.MaxOutputTokens };
            if (r.ResponseSchema != null)
            {
                config["responseMimeType"] = "application/json";
                config["responseJsonSchema"] = JsonNode.Parse(r.ResponseSchema.ToJsonString());
            }
            if (!string.IsNullOrWhiteSpace(r.ThinkingLevel))
                config["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = r.ThinkingLevel.Trim().ToUpperInvariant() };
            body["generationConfig"] = config;
            return body;
        }

        public static GeminiResponse ParseResponse(string json)
        {
            var root = JsonNode.Parse(json);
            var result = new GeminiResponse();

            var usage = root?["usageMetadata"];
            if (usage != null)
            {
                result.Usage.PromptTokens = Int(usage["promptTokenCount"]);
                result.Usage.OutputTokens = Int(usage["candidatesTokenCount"]);
                result.Usage.ThoughtTokens = Int(usage["thoughtsTokenCount"]);
                result.Usage.ToolPromptTokens = Int(usage["toolUsePromptTokenCount"]);
            }

            var candidate = root?["candidates"]?.AsArray().FirstOrDefault();
            if (candidate == null)
            {
                var block = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
                throw new InvalidOperationException(block != null ? $"Gemini blocked the request ({block})." : "Gemini returned no answer.");
            }

            result.FinishReason = candidate["finishReason"]?.GetValue<string>();
            var sb = new StringBuilder();
            if (candidate["content"]?["parts"] is JsonArray parts)
            {
                foreach (var part in parts)
                {
                    if (part?["thought"]?.GetValue<bool>() == true) continue;
                    var t = part?["text"]?.GetValue<string>();
                    if (t != null) sb.Append(t);
                }
            }
            result.Text = sb.ToString();

            var grounding = candidate["groundingMetadata"];
            if (grounding?["groundingChunks"] is JsonArray chunks)
            {
                foreach (var c in chunks)
                {
                    var web = c?["web"];
                    var uri = web?["uri"]?.GetValue<string>();
                    if (uri != null) result.SearchSources.Add(new KeyValuePair<string, string>(web["title"]?.GetValue<string>() ?? uri, uri));
                }
            }
            if (grounding?["webSearchQueries"] is JsonArray queries)
                foreach (var q in queries)
                    if (q != null) result.SearchQueries.Add(q.GetValue<string>());

            if (candidate["urlContextMetadata"]?["urlMetadata"] is JsonArray urls)
            {
                foreach (var u in urls)
                {
                    var status = u?["urlRetrievalStatus"]?.GetValue<string>() ?? "";
                    var url = u?["retrievedUrl"]?.GetValue<string>();
                    if (url != null && status.EndsWith("SUCCESS", StringComparison.Ordinal)) result.FetchedUrls.Add(url);
                }
            }
            return result;
        }

        static int Int(JsonNode n) => n == null ? 0 : n.GetValue<int>();

        static string ErrorMessage(string body)
        {
            try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? Truncate(body); }
            catch (JsonException) { return Truncate(body); }
        }

        static string Truncate(string s) => string.IsNullOrEmpty(s) ? "(empty response)" : s.Length > 300 ? s.Substring(0, 300) + "…" : s;
    }
}
