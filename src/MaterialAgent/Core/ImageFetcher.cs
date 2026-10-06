using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MaterialAgent.Core
{
    /// <summary>A texture image that has been downloaded (or read) and written to a local file Rhino can reference.</summary>
    public sealed class FetchedImage
    {
        /// <summary>What the user (or agent) asked for: an http(s) URL or a local path.</summary>
        public string Source { get; set; }
        public bool IsRemote { get; set; }
        /// <summary>Local file the texture points at.</summary>
        public string LocalPath { get; set; }
        public byte[] Bytes { get; set; }
        public ImageKind Kind { get; set; }
        public DateTime FetchedUtc { get; set; }
        /// <summary>Pixel size from the file header; 0 if unknown (e.g. TIFF).</summary>
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        /// <summary>True if the source was WebP and was converted to <see cref="Kind"/>.</summary>
        public bool ConvertedFromWebp { get; set; }

        /// <summary>Height / width, or 0 if unknown.</summary>
        public double Aspect => PixelWidth > 0 && PixelHeight > 0 ? (double)PixelHeight / PixelWidth : 0;
    }

    /// <summary>
    /// Deterministic image download ("agent finds, code downloads"). The agent never handles binary data.
    /// </summary>
    public static class ImageFetcher
    {
        public const long MaxBytes = 60L * 1024 * 1024;

        public const string UserAgent = "Mozilla/5.0 (compatible; MaterialAgent/0.1; Rhino 8 plug-in)";

        static readonly Lazy<HttpClient> SharedClient = new Lazy<HttpClient>(() => CreateClient(null));

        /// <summary>Shared client for page and image downloads. Tests pass their own handler.</summary>
        public static HttpClient CreateClient(HttpMessageHandler handler)
        {
            var c = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return c;
        }

        public static HttpClient Default => SharedClient.Value;

        /// <summary>Overrides <see cref="DownloadFolder"/> (tests).</summary>
        public static string DownloadFolderOverride { get; set; }

        /// <summary>
        /// Folder downloaded textures are written to. This is not a texture library: it only exists because
        /// Rhino textures reference a file on disk. The .3dm is the long-term home of the material.
        /// </summary>
        public static string DownloadFolder
        {
            get
            {
                if (!string.IsNullOrEmpty(DownloadFolderOverride)) return DownloadFolderOverride;
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
                return Path.Combine(root, "MaterialAgent", "textures");
            }
        }

        public static Task<FetchedImage> FetchAsync(string source, CancellationToken ct) => FetchAsync(source, null, ct);

        public static async Task<FetchedImage> FetchAsync(string source, HttpClient client, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("No image URL or file path given.");
            source = source.Trim().Trim('"');

            byte[] bytes;
            bool remote;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                remote = true;
                bytes = await DownloadAsync(client ?? Default, uri, ct).ConfigureAwait(false);
            }
            else
            {
                remote = false;
                var path = uri != null && uri.IsFile ? uri.LocalPath : source;
                if (!File.Exists(path)) throw new FileNotFoundException("Image file not found.", path);
                var info = new FileInfo(path);
                if (info.Length > MaxBytes) throw new InvalidDataException($"Image is larger than {MaxBytes / (1024 * 1024)} MB.");
                bytes = await Task.Run(() => File.ReadAllBytes(path), ct).ConfigureAwait(false);
                source = path;
            }

            var kind = ImageFormat.Sniff(bytes);
            if (kind == ImageKind.Unknown)
                throw new InvalidDataException("That doesn't look like an image (the server may have returned a web page, an error, or an unsupported format such as AVIF).");

            bool converted = false;
            if (kind == ImageKind.Webp)
            {
                var webp = bytes;
                (bytes, kind) = await Task.Run(() => WebpConverter.Convert(webp), ct).ConfigureAwait(false);
                converted = true;
            }
            if (!ImageFormat.IsSupportedTexture(kind))
                throw new NotSupportedException($"{kind} images are not supported. Use a PNG, JPEG or WebP.");

            string localPath;
            if (remote || converted)
            {
                Directory.CreateDirectory(DownloadFolder);
                localPath = Path.Combine(DownloadFolder, FileNameFor(source, kind));
                // Converted local files are rewritten every time: the source file may have been edited.
                if (!File.Exists(localPath) || !remote)
                {
                    var tmp = localPath + ".part";
                    File.WriteAllBytes(tmp, bytes);
                    if (File.Exists(localPath)) File.Delete(tmp); else File.Move(tmp, localPath);
                }
            }
            else
            {
                localPath = source;
            }

            ImageFormat.TryGetSize(bytes, out int pw, out int ph);
            return new FetchedImage
            {
                PixelWidth = pw,
                PixelHeight = ph,
                ConvertedFromWebp = converted,
                Source = source,
                IsRemote = remote,
                LocalPath = localPath,
                Bytes = bytes,
                Kind = kind,
                FetchedUtc = DateTime.UtcNow,
            };
        }

        static async Task<byte[]> DownloadAsync(HttpClient client, Uri uri, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                request.Headers.Accept.ParseAdd("image/png,image/jpeg,image/webp,image/*;q=0.8,*/*;q=0.5");
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"Download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    return await ReadLimitedAsync(response.Content, MaxBytes, ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Reads a response body, refusing anything larger than <paramref name="limit"/> bytes.</summary>
        public static async Task<byte[]> ReadLimitedAsync(HttpContent content, long limit, CancellationToken ct)
        {
            var len = content.Headers.ContentLength;
            if (len.HasValue && len.Value > limit)
                throw new InvalidDataException($"Response is larger than {limit / (1024 * 1024)} MB.");

            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length > limit)
                        throw new InvalidDataException($"Response is larger than {limit / (1024 * 1024)} MB.");
                }
                return ms.ToArray();
            }
        }

        /// <summary>Readable, collision-free name: last URL segment plus a short hash of the full URL.</summary>
        public static string FileNameFor(string url, ImageKind kind)
        {
            string stem = "texture";
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                var last = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(u.AbsolutePath.TrimEnd('/')));
                if (!string.IsNullOrWhiteSpace(last)) stem = last;
            }
            var safe = new StringBuilder();
            foreach (var ch in stem)
                safe.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
            var s = safe.ToString();
            if (s.Length > 48) s = s.Substring(0, 48);

            string hash;
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                hash = BitConverter.ToString(h, 0, 5).Replace("-", "").ToLowerInvariant();
            }
            return $"{s}_{hash}{ImageFormat.Extension(kind)}";
        }
    }
}
