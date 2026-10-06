using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MaterialAgent.Tests
{
    /// <summary>Routes requests to canned responses by URL; records what was sent.</summary>
    sealed class FakeHttp : HttpMessageHandler
    {
        readonly List<(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, string, HttpResponseMessage> respond)> _routes = new();
        public List<(string url, string body)> Requests { get; } = new();

        public FakeHttp On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, string, HttpResponseMessage> respond)
        {
            _routes.Add((match, respond));
            return this;
        }

        public FakeHttp OnUrl(string url, byte[] body, string contentType) =>
            On(r => r.RequestUri.ToString() == url, (r, b) => Bytes(body, contentType));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            lock (Requests) Requests.Add((request.RequestUri.ToString(), body));
            foreach (var (match, respond) in _routes)
                if (match(request))
                {
                    var response = respond(request, body);
                    response.RequestMessage = request;
                    return response;
                }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request, Content = new StringContent("not found") };
        }

        public static HttpResponseMessage Bytes(byte[] body, string contentType)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

        /// <summary>A Gemini generateContent response wrapping <paramref name="text"/>.</summary>
        public static string GeminiReply(string text)
        {
            var escaped = System.Text.Json.JsonSerializer.Serialize(text);
            return "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"thinking...\",\"thought\":true},{\"text\":" + escaped + "}]},"
                 + "\"finishReason\":\"STOP\",\"groundingMetadata\":{\"webSearchQueries\":[\"egger h1145\"],\"groundingChunks\":[{\"web\":{\"uri\":\"https://vertexaisearch.example/redirect/1\",\"title\":\"egger.com\"}}]}}],"
                 + "\"usageMetadata\":{\"promptTokenCount\":1200,\"candidatesTokenCount\":300,\"thoughtsTokenCount\":150,\"toolUsePromptTokenCount\":5000}}";
        }
    }
}
