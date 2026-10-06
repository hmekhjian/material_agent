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
    }

    /// <summary>
    /// Deterministic image download ("agent finds, code downloads"). The agent never handles binary data.
    /// </summary>
    public static class ImageFetcher
    {
        public const long MaxBytes = 60L * 1024 * 1024;

        static readonly Lazy<HttpClient> Client = new Lazy<HttpClient>(() =>
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MaterialAgent/0.1 (Rhino 8 plug-in)");
            c.DefaultRequestHeaders.Accept.ParseAdd("image/png,image/jpeg,image/*;q=0.8,*/*;q=0.5");
            return c;
        });

        /// <summary>
        /// Folder downloaded textures are written to. This is not a texture library: it only exists because
        /// Rhino textures reference a file on disk. The .3dm is the long-term home of the material.
        /// </summary>
        public static string DownloadFolder
        {
            get
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
                return Path.Combine(root, "MaterialAgent", "textures");
            }
        }

        public static async Task<FetchedImage> FetchAsync(string source, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("No image URL or file path given.");
            source = source.Trim().Trim('"');

            byte[] bytes;
            bool remote;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                remote = true;
                bytes = await DownloadAsync(uri, ct).ConfigureAwait(false);
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
                throw new InvalidDataException("That doesn't look like an image (the server may have returned a web page or an error).");
            if (!ImageFormat.IsSupportedTexture(kind))
                throw new NotSupportedException($"{kind} images are not supported yet. Use a PNG or JPEG.");

            string localPath;
            if (remote)
            {
                Directory.CreateDirectory(DownloadFolder);
                localPath = Path.Combine(DownloadFolder, FileNameFor(source, kind));
                if (!File.Exists(localPath))
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

            return new FetchedImage
            {
                Source = source,
                IsRemote = remote,
                LocalPath = localPath,
                Bytes = bytes,
                Kind = kind,
                FetchedUtc = DateTime.UtcNow,
            };
        }

        static async Task<byte[]> DownloadAsync(Uri uri, CancellationToken ct)
        {
            using (var response = await Client.Value.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                var len = response.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > MaxBytes)
                    throw new InvalidDataException($"Image is larger than {MaxBytes / (1024 * 1024)} MB.");

                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var ms = new MemoryStream())
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                        if (ms.Length > MaxBytes)
                            throw new InvalidDataException($"Image is larger than {MaxBytes / (1024 * 1024)} MB.");
                    }
                    return ms.ToArray();
                }
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
