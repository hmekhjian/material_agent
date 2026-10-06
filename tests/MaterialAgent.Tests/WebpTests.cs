using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MaterialAgent.Core;
using MaterialAgent.Core.Agent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MaterialAgent.Tests
{
    public class WebpTests : IDisposable
    {
        readonly string _folder = Path.Combine(Path.GetTempPath(), "matagent-webp-" + Guid.NewGuid().ToString("N"));

        public WebpTests() { ImageFetcher.DownloadFolderOverride = _folder; }
        public void Dispose()
        {
            ImageFetcher.DownloadFolderOverride = null;
            try { Directory.Delete(_folder, true); } catch { }
        }

        internal static byte[] Webp(int w, int h, bool lossless, bool alpha = false)
        {
            using (var img = new Image<Rgba32>(w, h))
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        img[x, y] = new Rgba32((byte)(x * 255 / w), (byte)(y * 255 / h), 90, alpha && x < w / 2 ? (byte)0 : (byte)255);
                using (var ms = new MemoryStream())
                {
                    img.Save(ms, new WebpEncoder { FileFormat = lossless ? WebpFileFormatType.Lossless : WebpFileFormatType.Lossy, Quality = 80 });
                    return ms.ToArray();
                }
            }
        }

        [Fact]
        public void LossyBecomesJpeg()
        {
            var webp = Webp(320, 240, lossless: false);
            Assert.Equal(ImageKind.Webp, ImageFormat.Sniff(webp));
            Assert.False(WebpConverter.IsLosslessOrHasAlpha(webp));
            var (bytes, kind) = WebpConverter.Convert(webp);
            Assert.Equal(ImageKind.Jpeg, kind);
            Assert.True(ImageFormat.TryGetSize(bytes, out var w, out var h));
            Assert.Equal((320, 240), (w, h));
        }

        [Fact]
        public void LosslessBecomesPng()
        {
            var webp = Webp(300, 200, lossless: true);
            Assert.True(WebpConverter.IsLosslessOrHasAlpha(webp));
            var (bytes, kind) = WebpConverter.Convert(webp);
            Assert.Equal(ImageKind.Png, kind);
            Assert.True(ImageFormat.TryGetSize(bytes, out var w, out var h));
            Assert.Equal((300, 200), (w, h));
        }

        [Fact]
        public async Task FetcherConvertsDownloadedWebp()
        {
            var http = new FakeHttp().OnUrl("https://cdn.example.com/oak.webp", Webp(400, 300, false), "image/webp");
            var img = await ImageFetcher.FetchAsync("https://cdn.example.com/oak.webp", new HttpClient(http), CancellationToken.None);
            Assert.True(img.ConvertedFromWebp);
            Assert.Equal(ImageKind.Jpeg, img.Kind);
            Assert.EndsWith(".jpg", img.LocalPath);
            Assert.True(File.Exists(img.LocalPath));
            Assert.Equal(0.75, img.Aspect, 3);
            Assert.Equal("https://cdn.example.com/oak.webp", img.Source); // provenance keeps the real URL
        }

        [Fact]
        public async Task FetcherConvertsLocalWebpIntoWorkingFolder()
        {
            Directory.CreateDirectory(_folder);
            var src = Path.Combine(_folder, "local tile.webp");
            File.WriteAllBytes(src, Webp(200, 200, true));
            var img = await ImageFetcher.FetchAsync(src, CancellationToken.None);
            Assert.Equal(ImageKind.Png, img.Kind);
            Assert.NotEqual(src, img.LocalPath);
            Assert.EndsWith(".png", img.LocalPath);
        }

        [Fact]
        public async Task KeyCheckReportsModelOrError()
        {
            var ok = new FakeHttp().On(r => r.Method == HttpMethod.Get, (r, b) => FakeHttp.Json("{\"name\":\"models/gemini-flash-latest\",\"displayName\":\"Gemini Flash Latest\"}"));
            Assert.Equal("Gemini Flash Latest", await new GeminiClient(new HttpClient(ok), "k", null).CheckAsync(CancellationToken.None));

            var bad = new FakeHttp().On(r => true, (r, b) => FakeHttp.Json("{\"error\":{\"message\":\"API key not valid. Please pass a valid API key.\"}}", HttpStatusCode.BadRequest));
            var ex = await Assert.ThrowsAsync<GeminiApiException>(() => new GeminiClient(new HttpClient(bad), "k", "x").CheckAsync(CancellationToken.None));
            Assert.Contains("check the key", ex.Message);
        }

        [Fact]
        public void SavedKeyBeatsEnvironment()
        {
            var old = Environment.GetEnvironmentVariable(AgentSettings.EnvApiKey);
            try
            {
                Environment.SetEnvironmentVariable(AgentSettings.EnvApiKey, "env-key");
                Assert.Equal("ui-key", AgentSettings.ResolveApiKey(" ui-key "));
                Assert.Equal("env-key", AgentSettings.ResolveApiKey(""));
                Assert.Equal("AIza…1234", AgentSettings.Mask("AIzaSyXXXXXXXX1234"));
            }
            finally { Environment.SetEnvironmentVariable(AgentSettings.EnvApiKey, old); }
        }
    }
}
