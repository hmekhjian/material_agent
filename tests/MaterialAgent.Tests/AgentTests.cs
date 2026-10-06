using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MaterialAgent.Core;
using MaterialAgent.Core.Agent;
using Xunit;

namespace MaterialAgent.Tests
{
    public class JsonTextTests
    {
        [Theory]
        [InlineData("{\"a\":1}", "{\"a\":1}")]
        [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
        [InlineData("Here you go:\n```\n{\"a\":{\"b\":\"}\"}}\n```\nDone.", "{\"a\":{\"b\":\"}\"}}")]
        [InlineData("Sure! {\"a\":\"x \\\" {\"} trailing", "{\"a\":\"x \\\" {\"}")]
        [InlineData("no json here", null)]
        public void ExtractsObject(string input, string expected)
        {
            Assert.Equal(expected, JsonText.ExtractObject(input));
        }
    }

    public class GeminiClientTests
    {
        [Fact]
        public void BuildsRequestBody()
        {
            var req = new GeminiRequest { SystemInstruction = "sys", UseGoogleSearch = true, UseUrlContext = true, ResponseSchema = JsonNode.Parse("{\"type\":\"object\"}"), ThinkingLevel = "low" };
            req.Messages.Add(new GeminiMessage { Role = "user", Parts = { GeminiPart.FromText("hi"), GeminiPart.FromImage(new byte[] { 1, 2, 3 }, "image/png") } });
            var body = GeminiClient.BuildBody(req);

            Assert.Equal("sys", (string)body["systemInstruction"]["parts"][0]["text"]);
            Assert.Equal("hi", (string)body["contents"][0]["parts"][0]["text"]);
            Assert.Equal("AQID", (string)body["contents"][0]["parts"][1]["inlineData"]["data"]);
            Assert.NotNull(body["tools"][0]["googleSearch"]);
            Assert.NotNull(body["tools"][1]["urlContext"]);
            Assert.Equal("application/json", (string)body["generationConfig"]["responseMimeType"]);
            Assert.Equal("LOW", (string)body["generationConfig"]["thinkingConfig"]["thinkingLevel"]);
        }

        [Fact]
        public void ParsesResponseSkippingThoughts()
        {
            var r = GeminiClient.ParseResponse(FakeHttp.GeminiReply("{\"ok\":true}"));
            Assert.Equal("{\"ok\":true}", r.Text);
            Assert.Equal(6650, r.Usage.Total);
            Assert.Single(r.SearchSources);
            Assert.Equal("egger h1145", r.SearchQueries.Single());
        }

        [Fact]
        public async Task SendsKeyHeaderAndReportsErrors()
        {
            var http = new FakeHttp().On(_ => true, (req, body) => FakeHttp.Json("{\"error\":{\"code\":403,\"message\":\"API key not valid\"}}", HttpStatusCode.Forbidden));
            var client = new GeminiClient(new HttpClient(http), "k123", "gemini-flash-latest");
            var ex = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateAsync(new GeminiRequest(), CancellationToken.None));
            Assert.Contains("API key not valid", ex.Message);
            Assert.EndsWith("/models/gemini-flash-latest:generateContent", http.Requests[0].url);
        }

        [Fact]
        public void RequiresKey()
        {
            Assert.Throws<InvalidOperationException>(() => new GeminiClient(new HttpClient(), " ", null));
        }
    }

    public class ScaleLadderTests
    {
        [Fact]
        public void PageTextWinsAndKeepsPixelProportions()
        {
            var d = ScaleLadder.Decide(new ScaleInfo { WidthMm = 600, HeightMm = 300, Source = "page_text", Confidence = "high" }, null, 0.5);
            Assert.Equal((600, 300), (d.WidthMm, d.HeightMm));
            Assert.Equal(ScaleSource.PageText, d.Source);
            Assert.Equal(ScaleConfidence.High, d.Confidence);
        }

        [Fact]
        public void MismatchedProportionsAdjustHeightAndLowerConfidence()
        {
            // Page says the board is 2800 x 2070 but the image is square: it must be a crop.
            var d = ScaleLadder.Decide(new ScaleInfo { WidthMm = 2800, HeightMm = 2070, Source = "page_text", Confidence = "high" }, null, 1.0);
            Assert.Equal(2800, d.WidthMm);
            Assert.Equal(2800, d.HeightMm);
            Assert.Equal(ScaleConfidence.Medium, d.Confidence);
            Assert.Contains("adjusted", d.Rationale);
        }

        [Fact]
        public void FeatureCountIsMultipliedInCode()
        {
            var feature = new ScaleFeature { Name = "plank", RealMm = 190, CountAcross = 4, Axis = "height" };
            var d = ScaleLadder.Decide(new ScaleInfo { WidthMm = 1000, HeightMm = 1000, Source = "category_prior", Confidence = "low" }, feature, 0.5);
            Assert.Equal(ScaleSource.ImageFeature, d.Source);
            Assert.Equal(ScaleConfidence.Medium, d.Confidence);
            Assert.Equal(760, d.HeightMm);
            Assert.Equal(1520, d.WidthMm);
        }

        [Fact]
        public void CategoryPriorIsLowConfidence()
        {
            var d = ScaleLadder.Decide(new ScaleInfo { WidthMm = 1000, HeightMm = 500, Source = "category_prior", Confidence = "high" }, null, 0.5);
            Assert.Equal(ScaleSource.CategoryPrior, d.Source);
            Assert.Equal(ScaleConfidence.Low, d.Confidence);
        }

        [Fact]
        public void NoEvidenceGivesPlaceholder()
        {
            var d = ScaleLadder.Decide(null, null, 0.25);
            Assert.Equal((1000, 250), (d.WidthMm, d.HeightMm));
            Assert.Equal(ScaleConfidence.Low, d.Confidence);
        }
    }

    public class PageImageHarvesterTests
    {
        const string Html = @"<html><head>
<meta property=""og:image"" content=""https://cdn.example.com/img/room-scene-h1145.jpg"">
<script type=""application/ld+json"">{""@type"":""Product"",""image"":[""https:\/\/cdn.example.com\/decor\/H1145_ST10_decor.jpg""]}</script>
</head><body>
<img src=""/static/logo.png"">
<img src=""/thumbs/h1145.jpg?w=120"" srcset=""/img/h1145-640.jpg 640w, /img/h1145-2000.jpg 2000w"">
<img data-src=""//cdn.example.com/other/kitchen.jpg"" src=""data:image/gif;base64,AAAA"">
<a href=""/downloads/H1145_ST10_texture_full.jpg"">Download decor</a>
<img src=""/icons/sprite.svg"">
</body></html>";

        [Fact]
        public void RanksDecorImagesFirstAndDropsJunk()
        {
            var urls = PageImageHarvester.Extract(Html, "https://www.example.com/products/h1145", "H1145 ST10", 10);
            Assert.Equal("https://www.example.com/downloads/H1145_ST10_texture_full.jpg", urls[0]);
            Assert.Contains("https://cdn.example.com/decor/H1145_ST10_decor.jpg", urls.Take(3));
            Assert.Contains("https://www.example.com/img/h1145-2000.jpg", urls);
            Assert.DoesNotContain(urls, u => u.Contains("logo") || u.EndsWith(".svg") || u.StartsWith("data:"));
            Assert.Contains("https://cdn.example.com/other/kitchen.jpg", urls);
        }

        [Fact]
        public void CodeTokens()
        {
            var t = PageImageHarvester.CodeTokens("H1145 ST10");
            Assert.Contains("h1145st10", t);
            Assert.Contains("h1145", t);
            Assert.Contains("1145", t);
        }
    }

    public class ResolverTests : IDisposable
    {
        readonly string _folder = Path.Combine(Path.GetTempPath(), "matagent-tests-" + Guid.NewGuid().ToString("N"));

        public ResolverTests() { ImageFetcher.DownloadFolderOverride = _folder; }
        public void Dispose()
        {
            ImageFetcher.DownloadFolderOverride = null;
            try { Directory.Delete(_folder, true); } catch { }
        }

        const string Research = @"```json
{""product"":{""name"":""Natural Halifax Oak"",""code"":""H1145 ST10"",""manufacturer"":""Egger"",""page_url"":""https://shop.example.com/h1145""},
 ""candidates"":[{""url"":""https://hallucinated.example.com/nope.jpg"",""kind"":""swatch"",""likely_tileable"":true,""note"":""guess""}],
 ""scale"":{""width_mm"":2800,""height_mm"":2070,""source"":""category_prior"",""confidence"":""low"",""rationale"":""typical decor swatch""},
 ""grain_axis"":""horizontal"",""mapping"":""planar"",""finish"":""textured"",""category"":""wood decor laminate""}
```";

        // Images arrive decor first (ranked by the page harvester), then the og:image room shot.
        const string Vision = @"{""images"":[{""index"":0,""kind"":""swatch"",""likely_tileable"":true,""matches_product"":true,""note"":""flat decor""},
 {""index"":1,""kind"":""room"",""likely_tileable"":false,""matches_product"":true,""note"":""kitchen""}],
 ""best_index"":0,""grain_axis"":""horizontal"",""feature"":{""name"":""plank"",""real_mm"":200,""count_across"":2.5,""axis"":""height""}}";

        static FakeHttp Web()
        {
            var html = @"<html><head><meta property=""og:image"" content=""https://shop.example.com/img/room.jpg""></head>
<body><a href=""https://shop.example.com/img/H1145_decor.png"">decor</a><img src=""https://shop.example.com/img/tiny-h1145.png""></body></html>";
            return new FakeHttp()
                .OnUrl("https://shop.example.com/h1145", System.Text.Encoding.UTF8.GetBytes(html), "text/html")
                .OnUrl("https://shop.example.com/img/room.jpg", TestImages.Png(400, 300, 90), "image/png")
                .OnUrl("https://shop.example.com/img/H1145_decor.png", TestImages.Png(800, 400, 160), "image/png")
                .OnUrl("https://shop.example.com/img/tiny-h1145.png", TestImages.Png(64, 64), "image/png");
        }

        [Fact]
        public async Task EndToEnd()
        {
            int geminiCalls = 0;
            var gemini = new FakeHttp().On(r => r.RequestUri.Host == "generativelanguage.googleapis.com", (r, body) =>
            {
                geminiCalls++;
                return FakeHttp.Json(FakeHttp.GeminiReply(body.Contains("googleSearch") ? Research : Vision));
            });
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web()));

            var r = await resolver.ResolveAsync("Egger H1145 ST10", null, CancellationToken.None);

            Assert.Equal(2, geminiCalls);
            Assert.Equal("Natural Halifax Oak", r.Resolution.Product.Name);
            // The hallucinated URL fails, the tiny thumbnail is dropped, the page images survive.
            Assert.Equal(2, r.Candidates.Count);
            Assert.Equal("https://shop.example.com/img/H1145_decor.png", r.Candidates[0].Url);
            Assert.Equal("swatch", r.Candidates[0].Kind);
            Assert.Equal("room", r.Candidates[1].Kind);
            Assert.Contains(r.Warnings, w => w.Contains("hallucinated"));
            // Vision counted 2.5 planks of 200 mm along the height of an 800x400 image.
            Assert.Equal(ScaleSource.ImageFeature, r.Scale.Source);
            Assert.Equal(500, r.Scale.HeightMm);
            Assert.Equal(1000, r.Scale.WidthMm);
            Assert.Equal(GrainAxis.Horizontal, r.Grain);
            Assert.Equal(MappingKind.Planar, r.Mapping);
            Assert.Equal(Finish.Textured, r.Finish);
            Assert.True(r.Usage.Total > 0);
            Assert.True(File.Exists(r.Candidates[0].Image.LocalPath));
            // The vision call carried both images inline.
            var visionBody = gemini.Requests.Last().body;
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(visionBody, "inlineData").Count);
        }

        [Fact]
        public async Task VisionPickReordersCandidates()
        {
            const string pickRoom = @"{""images"":[{""index"":0,""kind"":""detail"",""likely_tileable"":false,""matches_product"":false},
 {""index"":1,""kind"":""swatch"",""likely_tileable"":true,""matches_product"":true}],""best_index"":1,""grain_axis"":""vertical"",""feature"":null}";
            var gemini = new FakeHttp().On(r => true, (r, body) => FakeHttp.Json(FakeHttp.GeminiReply(body.Contains("googleSearch") ? Research : pickRoom)));
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web()));
            var r = await resolver.ResolveAsync("H1145", null, CancellationToken.None);
            Assert.Equal("https://shop.example.com/img/room.jpg", r.Candidates[0].Url);
            Assert.False(r.Candidates[1].MatchesProduct);
            Assert.Equal(GrainAxis.Vertical, r.Grain);
            // No feature: the agent's category prior. 2800x2070 is within 3% of the 400x300 image, so it is kept.
            Assert.Equal(ScaleSource.CategoryPrior, r.Scale.Source);
            Assert.Equal(2070, r.Scale.HeightMm);
        }

        [Fact]
        public async Task RepairsInvalidJsonOnce()
        {
            int research = 0;
            var gemini = new FakeHttp().On(r => true, (r, body) =>
            {
                if (!body.Contains("googleSearch")) return FakeHttp.Json(FakeHttp.GeminiReply(Vision));
                return FakeHttp.Json(FakeHttp.GeminiReply(research++ == 0 ? "{\"product\":{\"name\":\"\"}}" : Research));
            });
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web()));
            var r = await resolver.ResolveAsync("H1145", null, CancellationToken.None);
            Assert.Equal(2, research);
            Assert.Contains("could not be used", gemini.Requests[1].body);
            Assert.Equal("Natural Halifax Oak", r.Resolution.Product.Name);
        }

        [Fact]
        public async Task FallsBackWhenModelRejectsStructuredOutput()
        {
            var gemini = new FakeHttp().On(r => true, (r, body) =>
            {
                if (body.Contains("responseJsonSchema")) return FakeHttp.Json("{\"error\":{\"message\":\"responseJsonSchema is not supported with tools\"}}", HttpStatusCode.BadRequest);
                return FakeHttp.Json(FakeHttp.GeminiReply(body.Contains("googleSearch") ? Research : Vision));
            });
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web()));
            var r = await resolver.ResolveAsync("H1145", null, CancellationToken.None);
            Assert.Equal("Natural Halifax Oak", r.Resolution.Product.Name);
        }

        [Fact]
        public async Task VisionFailureStillReturnsResult()
        {
            var gemini = new FakeHttp().On(r => true, (r, body) =>
                body.Contains("googleSearch") ? FakeHttp.Json(FakeHttp.GeminiReply(Research)) : FakeHttp.Json(FakeHttp.GeminiReply("I can't help")));
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web()));
            var r = await resolver.ResolveAsync("H1145", null, CancellationToken.None);
            Assert.Contains(r.Warnings, w => w.StartsWith("Image check skipped"));
            Assert.Equal(ScaleSource.CategoryPrior, r.Scale.Source);
        }
    }
}
