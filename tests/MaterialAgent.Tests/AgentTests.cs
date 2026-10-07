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

    [Collection("DownloadFolder")] // shares the static ImageFetcher.DownloadFolderOverride
    public class ResolverTests : IDisposable
    {
        readonly string _folder = Path.Combine(Path.GetTempPath(), "matagent-tests-" + Guid.NewGuid().ToString("N"));

        public ResolverTests()
        {
            ImageFetcher.DownloadFolderOverride = _folder;
            GeminiMaterialResolver.ClearCache();
        }

        public void Dispose()
        {
            ImageFetcher.DownloadFolderOverride = null;
            try { Directory.Delete(_folder, true); } catch { }
        }

        const string Locate = @"{""product"":{""name"":""Natural Halifax Oak"",""code"":""H1145 ST10"",""manufacturer"":""Egger"",""page_url"":""https://shop.example.com/h1145""},
 ""other_pages"":[""https://dealer.example.com/h1145-oak""],""category"":""wood decor laminate""}";

        // Images arrive in harvest order: the decor PNG (link, code in name) first, then the og:image room shot.
        const string Analysis = @"```json
{""product"":{""name"":""Natural Halifax Oak"",""code"":""H1145 ST10"",""manufacturer"":""Egger""},
 ""images"":[{""index"":0,""kind"":""swatch"",""likely_tileable"":true,""matches_product"":true,""note"":""flat decor""},
             {""index"":1,""kind"":""room"",""likely_tileable"":false,""matches_product"":true,""note"":""kitchen""}],
 ""best_index"":0,
 ""scale"":{""width_mm"":1000,""height_mm"":500,""source"":""image_feature"",""confidence"":""medium"",""rationale"":""planks"",
            ""feature"":{""name"":""plank"",""real_mm"":200,""count_across"":2.5,""axis"":""height""}},
 ""grain_axis"":""horizontal"",""mapping"":""planar"",""finish"":""textured"",""category"":""wood decor laminate"",""brick"":null}
```";

        static FakeHttp Web(bool scriptRendered = false)
        {
            var html = scriptRendered
                ? @"<html><head><title>Loading</title></head><body><div id=""app""></div><script>render()</script></body></html>"
                : @"<html><head><title>H1145 ST10 Natural Halifax Oak | Egger</title><meta property=""og:image"" content=""https://shop.example.com/img/room.jpg""></head>
<body><nav>Home</nav><h1>Natural Halifax Oak H1145 ST10</h1><p>Decor image shows approx. 2800 x 2070 mm.</p><p>A warm natural oak decor with lively knots and cracks, available on chipboard and MDF in the ST10 Deepskin Rough texture.</p>
<script>var tracking = 1;</script>
<a href=""https://shop.example.com/img/H1145_decor.png"">decor download</a><img src=""https://shop.example.com/img/tiny-h1145.png""><nav>Home</nav></body></html>";
            return new FakeHttp()
                .OnUrl("https://shop.example.com/h1145", System.Text.Encoding.UTF8.GetBytes(html), "text/html")
                .OnUrl("https://shop.example.com/img/room.jpg", TestImages.Png(400, 300, 90), "image/png")
                .OnUrl("https://shop.example.com/img/H1145_decor.png", WebpTests.Webp(800, 400, lossless: false), "image/webp")
                .OnUrl("https://shop.example.com/img/tiny-h1145.png", TestImages.Png(64, 64), "image/png");
            // dealer.example.com is not served: a failing alternative page must not break the search.
        }

        static FakeHttp Gemini(string locate, string analysis, Action<string> onAnalyse = null) =>
            new FakeHttp().On(r => r.RequestUri.Host == "generativelanguage.googleapis.com", (r, body) =>
            {
                if (body.Contains("googleSearch")) return FakeHttp.Json(FakeHttp.GeminiReply(locate));
                onAnalyse?.Invoke(body);
                return FakeHttp.Json(FakeHttp.GeminiReply(analysis));
            });

        [Fact]
        public async Task EndToEnd()
        {
            string analyseBody = null;
            var gemini = Gemini(Locate, Analysis, b => analyseBody = b);
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web()));

            var r = await resolver.ResolveAsync("Egger H1145 ST10", null, CancellationToken.None);

            // Two model calls: search-only locate, then one analysis with no tools.
            Assert.Equal(2, r.ModelCalls);
            Assert.Equal(2, gemini.Requests.Count);
            Assert.Contains("googleSearch", gemini.Requests[0].body);
            Assert.DoesNotContain("urlContext", gemini.Requests[0].body);
            Assert.DoesNotContain("googleSearch", analyseBody);
            Assert.DoesNotContain("urlContext", analyseBody);

            // The analysis got the page text (scripts stripped, repeated nav removed) and both images inline.
            Assert.Contains("Decor image shows approx. 2800 x 2070 mm.", analyseBody);
            Assert.DoesNotContain("tracking", analyseBody);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(analyseBody, "inlineData").Count);

            // Only the texture is offered; the room shot is kept as a generation reference.
            Assert.Equal("https://shop.example.com/img/H1145_decor.png", r.Candidates.Single().Url);
            Assert.True(r.Candidates[0].Image.ConvertedFromWebp);
            Assert.Equal("room", r.References.Single().Kind);
            Assert.Contains(r.Warnings, w => w.Contains("dealer.example.com"));

            // 2.5 planks of 200 mm along the height of an 800x400 image.
            Assert.Equal(ScaleSource.ImageFeature, r.Scale.Source);
            Assert.Equal(500, r.Scale.HeightMm);
            Assert.Equal(1000, r.Scale.WidthMm);
            Assert.Equal(GrainAxis.Horizontal, r.Grain);
            Assert.Equal(Finish.Textured, r.Finish);
            Assert.Equal("https://shop.example.com/h1145", r.Resolution.Product.PageUrl);
            Assert.Equal(new[] { "search", "pages", "analysis" }, r.Timings.Select(t => t.Key).ToArray());
        }

        [Fact]
        public async Task FindsThePageWithTheFastModelAndAnalysesWithTheMainOne()
        {
            var gemini = Gemini(Locate, Analysis);
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web()));
            await resolver.ResolveAsync("Egger H1145 ST10", null, CancellationToken.None);
            Assert.Contains("/models/gemini-flash-lite-latest:generateContent", gemini.Requests[0].url);
            Assert.Contains("/models/gemini-flash-latest:generateContent", gemini.Requests[1].url);
            Assert.Contains("\"thinkingLevel\":\"MINIMAL\"", gemini.Requests[1].body);
        }

        [Fact]
        public async Task FallsBackToTheMainModelWhenTheFastOneIsUnavailable()
        {
            var gemini = new FakeHttp().On(r => true, (r, body) =>
            {
                if (r.RequestUri.ToString().Contains("flash-lite"))
                    return FakeHttp.Json("{\"error\":{\"message\":\"models/gemini-flash-lite-latest is not found\"}}", HttpStatusCode.NotFound);
                return FakeHttp.Json(FakeHttp.GeminiReply(body.Contains("googleSearch") ? Locate : Analysis));
            });
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web()));
            var r = await resolver.ResolveAsync("H1145", null, CancellationToken.None);
            Assert.Single(r.Candidates);
            Assert.Contains(r.Warnings, w => w.Contains("flash-lite"));
        }

        [Fact]
        public async Task TraceRecordsEveryStepAndTheLogKeepsIt()
        {
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(Gemini(Locate, Analysis)), new HttpClient(Web()));
            await resolver.ResolveAsync("Egger H1145 ST10", null, CancellationToken.None);
            var t = resolver.LastTrace.ToString();
            Assert.Contains("Search \"Egger H1145 ST10\"", t);
            Assert.Contains("gemini-flash-lite-latest: HTTP 200", t);       // locate call, with timing and tokens
            Assert.Contains("gemini-flash-latest: HTTP 200", t);            // analysis call
            Assert.Contains("tokens prompt", t);
            Assert.Contains("Page https://shop.example.com/h1145:", t);
            Assert.Contains("failed", t);                                   // the unreachable dealer page
            Assert.Contains("too small: skipped", t);                        // the 64 px thumbnail
            Assert.Contains("Done in", t);

            var log = Path.Combine(_folder, "search.log");
            SearchLog.PathOverride = log;
            try
            {
                SearchLog.Append(resolver.LastTrace);
                Assert.Contains("Done in", SearchLog.Read());
                SearchLog.Clear();
                Assert.Equal("", SearchLog.Read());
            }
            finally { SearchLog.PathOverride = null; }
        }

        [Fact]
        public async Task TraceExplainsFailures()
        {
            var gemini = new FakeHttp().On(r => true, (r, b) => FakeHttp.Json("{\"error\":{\"message\":\"Quota exceeded\"}}", (HttpStatusCode)429));
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test", LocateModel = "gemini-flash-latest" }, new HttpClient(gemini), new HttpClient(Web()));
            await Assert.ThrowsAsync<GeminiApiException>(() => resolver.ResolveAsync("H1145", null, CancellationToken.None));
            var t = resolver.LastTrace.ToString();
            Assert.Contains("HTTP 429", t);
            Assert.Contains("retrying in 2s", t);
            Assert.Contains("FAILED after", t);
            Assert.Contains("Quota exceeded", t);
        }

        [Fact]
        public async Task RepeatSearchComesFromCache()
        {
            var gemini = Gemini(Locate, Analysis);
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web()));
            await resolver.ResolveAsync("Egger H1145 ST10", null, CancellationToken.None);
            var again = await resolver.ResolveAsync("egger h1145-st10", null, CancellationToken.None);
            Assert.True(again.FromCache);
            Assert.Equal(2, gemini.Requests.Count); // no new calls
        }

        [Fact]
        public async Task ScriptRenderedPagesFallBackToUrlContext()
        {
            string analyseBody = null;
            var gemini = Gemini(Locate, Analysis.Replace(@"""images"":[{""index"":0", @"""images"":[],""unused"":[{""index"":0"), b => analyseBody = b);
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web(scriptRendered: true)));
            var r = await resolver.ResolveAsync("H1145", null, CancellationToken.None);
            Assert.Contains("urlContext", analyseBody);
            Assert.Contains("could not be read as plain HTML", analyseBody);
            Assert.Empty(r.Candidates);
            Assert.Contains(r.Warnings, w => w.Contains("No usable product image"));
        }

        [Fact]
        public async Task ReturnsBrickSize()
        {
            var brick = Analysis.Replace(@"""brick"":null", @"""brick"":{""length_mm"":215,""height_mm"":65,""depth_mm"":102.5}");
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(Gemini(Locate, brick)), new HttpClient(Web()));
            var r = await resolver.ResolveAsync("Ibstock Anglian Red Multi Rustic", null, CancellationToken.None);
            Assert.Equal(215, r.Brick.LengthMm);
            Assert.Equal(65, r.Brick.HeightMm);
        }

        [Fact]
        public async Task RepairsInvalidAnalysisOnce()
        {
            int analyses = 0;
            var gemini = new FakeHttp().On(r => true, (r, body) =>
                body.Contains("googleSearch")
                    ? FakeHttp.Json(FakeHttp.GeminiReply(Locate))
                    : FakeHttp.Json(FakeHttp.GeminiReply(analyses++ == 0 ? "{\"product\":{\"name\":\"x\"},\"mapping\":\"cylinder\"}" : Analysis)));
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web()));
            var r = await resolver.ResolveAsync("H1145", null, CancellationToken.None);
            Assert.Equal(2, analyses);
            Assert.Contains("could not be used", gemini.Requests.Last().body);
            Assert.Single(r.Candidates);
        }

        [Fact]
        public async Task FallsBackWhenModelRejectsStructuredOutput()
        {
            var gemini = new FakeHttp().On(r => true, (r, body) =>
            {
                if (body.Contains("responseJsonSchema")) return FakeHttp.Json("{\"error\":{\"message\":\"responseJsonSchema is not supported with tools\"}}", HttpStatusCode.BadRequest);
                return FakeHttp.Json(FakeHttp.GeminiReply(body.Contains("googleSearch") ? Locate : Analysis));
            });
            var resolver = new GeminiMaterialResolver(new AgentSettings { ApiKey = "test" }, new HttpClient(gemini), new HttpClient(Web()));
            var r = await resolver.ResolveAsync("H1145", null, CancellationToken.None);
            Assert.Equal("Natural Halifax Oak", r.Resolution.Product.Name);
        }

        [Fact]
        public void WithoutVisionOnlyAgentRoomLabelsAreFiltered()
        {
            var r = new ResolveResult();
            r.Candidates.Add(new CandidateImage { Url = "a", Kind = "room" });
            r.Candidates.Add(new CandidateImage { Url = "b", Kind = null, FromPage = true });
            GeminiMaterialResolver.SplitTexturesFromReferences(r, checkedByVision: false);
            Assert.Equal("b", r.Candidates.Single().Url);
            Assert.Equal("a", r.References.Single().Url);
        }
    }

    public class PageReaderTests
    {
        [Fact]
        public void ExtractsReadableText()
        {
            var html = @"<html><head><title>Oak &amp; Co</title><meta name=""description"" content=""Lovely oak decor"">
<script type=""application/ld+json"">{""@type"":""Product"",""size"":""2800 x 2070 mm""}</script>
<style>.x{color:red}</style></head><body><nav>Menu</nav><div>Dimensions: 215 x 65 mm</div><script>evil()</script><nav>Menu</nav><!-- hidden --></body></html>";
            var t = PageReader.ExtractText(html);
            Assert.Contains("Title: Oak & Co", t);
            Assert.Contains("Meta: Lovely oak decor", t);
            Assert.Contains("2800 x 2070 mm", t);
            Assert.Contains("Dimensions: 215 x 65 mm", t);
            Assert.DoesNotContain("evil", t);
            Assert.DoesNotContain("color:red", t);
            Assert.DoesNotContain("hidden", t);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(t, "Menu"));
            Assert.True(PageReader.ContentLength(t) > 20);
        }

        [Fact]
        public void TruncatesLongPages()
        {
            var html = "<body>" + string.Concat(Enumerable.Range(0, 5000).Select(i => $"<p>line number {i}</p>")) + "</body>";
            Assert.True(PageReader.ExtractText(html, 2000).Length <= 2001);
        }
    }
}
