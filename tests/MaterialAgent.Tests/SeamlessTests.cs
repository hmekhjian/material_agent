using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MaterialAgent.Core;
using MaterialAgent.Core.Agent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MaterialAgent.Tests
{
    [Collection("DownloadFolder")] // shares the static ImageFetcher.DownloadFolderOverride
    public class SeamlessTests : IDisposable
    {
        readonly string _folder = Path.Combine(Path.GetTempPath(), "matagent-seamless-" + Guid.NewGuid().ToString("N"));

        public SeamlessTests() { ImageFetcher.DownloadFolderOverride = _folder; }
        public void Dispose()
        {
            ImageFetcher.DownloadFolderOverride = null;
            try { Directory.Delete(_folder, true); } catch { }
        }

        /// <summary>A horizontal + vertical gradient: maximally non-tileable (black on one side, bright on the other).</summary>
        static byte[] Gradient(int w, int h)
        {
            using (var img = new Image<Rgb24>(w, h))
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        img[x, y] = new Rgb24((byte)(x * 255 / (w - 1)), (byte)(y * 255 / (h - 1)), 120);
                using (var ms = new MemoryStream()) { img.Save(ms, new PngEncoder()); return ms.ToArray(); }
            }
        }

        [Fact]
        public void BlendRemovesTheWrapSeam()
        {
            var src = Gradient(256, 128);
            double before = SeamlessTile.WrapSeamError(src);
            var tiled = SeamlessTile.MakeSeamless(src);
            double after = SeamlessTile.WrapSeamError(tiled);
            Assert.True(before > 50, $"before {before}");
            Assert.True(after < 6, $"after {after}");
            Assert.True(ImageFormat.TryGetSize(tiled, out var w, out var h));
            Assert.Equal((256, 128), (w, h)); // size kept, so the real-world scale is unchanged
        }

        [Fact]
        public void BlendKeepsTheCentre()
        {
            var src = Gradient(200, 200);
            var tiled = SeamlessTile.MakeSeamless(src);
            using (var a = Image.Load<Rgb24>(src))
            using (var b = Image.Load<Rgb24>(tiled))
            {
                var pa = a[100, 100]; var pb = b[100, 100];
                Assert.InRange(Math.Abs(pa.R - pb.R), 0, 4);
                Assert.InRange(Math.Abs(pa.G - pb.G), 0, 4);
            }
        }

        [Theory]
        [InlineData(1.0, "1:1")]
        [InlineData(0.74, "4:3")]
        [InlineData(1.45, "2:3")]
        [InlineData(0.55, "16:9")]
        public void PicksClosestSupportedRatio(double hOverW, string expected)
        {
            Assert.Equal(expected, SeamlessTextureGenerator.ClosestRatio(hOverW).ratio);
        }

        [Fact]
        public void RequestsImageOutput()
        {
            var req = new GeminiRequest { ResponseModalities = new() { "TEXT", "IMAGE" }, ImageAspectRatio = "4:3", ImageSize = "1K" };
            var body = GeminiClient.BuildBody(req);
            Assert.Equal("IMAGE", (string)body["generationConfig"]["responseModalities"][1]);
            Assert.Equal("4:3", (string)body["generationConfig"]["imageConfig"]["aspectRatio"]);
            Assert.Equal("1K", (string)body["generationConfig"]["imageConfig"]["imageSize"]);
            Assert.Null(body["generationConfig"]["maxOutputTokens"]);
            Assert.Null(body["tools"]);
        }

        [Fact]
        public async Task GeneratesSeamlessTextureFromReferences()
        {
            var generatedPng = Gradient(1184, 864); // what a "4:3" 1K image might really be
            string sentBody = null;
            var http = new FakeHttp().On(r => r.RequestUri.ToString().Contains("gemini-3.1-flash-image:generateContent"), (r, body) =>
            {
                sentBody = body;
                var json = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Here you go\"},{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\""
                    + Convert.ToBase64String(generatedPng) + "\"}}]}}],\"usageMetadata\":{\"promptTokenCount\":800,\"candidatesTokenCount\":1290}}";
                return FakeHttp.Json(json);
            });

            var reference = new CandidateImage
            {
                Url = "https://shop.example.com/room.jpg",
                Kind = "room",
                Image = new FetchedImage { Bytes = TestImages.Png(400, 300), Kind = ImageKind.Png },
            };
            var basis = new ScaleDecision { Source = ScaleSource.PageText, Confidence = ScaleConfidence.High, Rationale = "board 2800 x 2070" };
            var gen = new SeamlessTextureGenerator(new AgentSettings { ApiKey = "k" }, new HttpClient(http));
            var result = await gen.GenerateAsync(new ProductInfo { Name = "Natural Halifax Oak", Code = "H1145 ST10", Manufacturer = "Egger" },
                "wood decor laminate", Finish.Textured, new[] { reference }, 1600, 1200, basis, CancellationToken.None);

            // Request: the reference image went inline, with the area and product in the prompt.
            Assert.Contains("inlineData", sentBody);
            Assert.Contains("1600 mm wide by 1200 mm high", sentBody);
            Assert.Contains("Egger Natural Halifax Oak (H1145 ST10)", sentBody);
            Assert.Contains("\"aspectRatio\":\"4:3\"", sentBody);

            // Result: seamless, saved, flagged as generated, scale from the real pixels, confidence lowered.
            var c = result.Candidate;
            Assert.Equal("generated", c.Kind);
            Assert.True(File.Exists(c.Image.LocalPath));
            Assert.StartsWith("ai-generated:gemini-3.1-flash-image:https://shop.example.com/room.jpg", c.Url);
            Assert.True(SeamlessTile.WrapSeamError(c.Image.Bytes) < 8);
            Assert.Equal(1600, result.Scale.WidthMm);
            Assert.Equal(Math.Round(1600 * 864 / 1184.0, 1), result.Scale.HeightMm);
            Assert.Equal(ScaleConfidence.Medium, result.Scale.Confidence);
            Assert.Equal(ScaleSource.PageText, result.Scale.Source);
            Assert.Equal(2090, result.Usage.Total);
        }

        [Fact]
        public async Task ReportsWhenNoImageComesBack()
        {
            var http = new FakeHttp().On(r => true, (r, b) => FakeHttp.Json(FakeHttp.GeminiReply("I can't make that image.")));
            var reference = new CandidateImage { Url = "u", Image = new FetchedImage { Bytes = TestImages.Png(300, 300), Kind = ImageKind.Png } };
            var gen = new SeamlessTextureGenerator(new AgentSettings { ApiKey = "k" }, new HttpClient(http));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                gen.GenerateAsync(new ProductInfo(), null, null, new[] { reference }, 1000, 1000, null, CancellationToken.None));
            Assert.Contains("can't make that image", ex.Message);
        }
    }
}
