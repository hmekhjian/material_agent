using System;
using System.IO;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace MaterialAgent.Core
{
    /// <summary>
    /// Converts WebP (what most product sites serve) into a format Rhino textures, Eto thumbnails and the
    /// vision call all accept. Lossy WebP becomes JPEG; lossless or transparent WebP becomes PNG.
    /// Animated WebP keeps its first frame.
    /// </summary>
    public static class WebpConverter
    {
        public const int JpegQuality = 92;

        public static (byte[] bytes, ImageKind kind) Convert(byte[] webp)
        {
            if (ImageFormat.Sniff(webp) != ImageKind.Webp) throw new ArgumentException("Not a WebP image.");
            bool keepLossless = IsLosslessOrHasAlpha(webp);

            using (var image = Image.Load(webp))
            using (var ms = new MemoryStream())
            {
                // Animated WebP: keep the first frame only.
                while (image.Frames.Count > 1) image.Frames.RemoveFrame(image.Frames.Count - 1);

                if (keepLossless)
                {
                    image.Save(ms, new PngEncoder());
                    return (ms.ToArray(), ImageKind.Png);
                }
                image.Save(ms, new JpegEncoder { Quality = JpegQuality });
                return (ms.ToArray(), ImageKind.Jpeg);
            }
        }

        /// <summary>
        /// Reads the RIFF chunks: "VP8L" is lossless; an "ALPH" chunk means transparency. Both go to PNG.
        /// </summary>
        public static bool IsLosslessOrHasAlpha(byte[] d)
        {
            int i = 12;
            while (i + 8 <= d.Length)
            {
                var fourcc = Encoding.ASCII.GetString(d, i, 4);
                if (fourcc == "VP8L" || fourcc == "ALPH") return true;
                if (fourcc == "VP8 ") return false;
                long size = BitConverter.ToUInt32(d, i + 4);
                i += 8 + (int)Math.Min(int.MaxValue - 16, size + (size & 1));
                if (i < 0) break;
            }
            return false;
        }
    }
}
