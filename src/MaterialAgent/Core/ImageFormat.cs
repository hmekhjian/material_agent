using System;

namespace MaterialAgent.Core
{
    public enum ImageKind
    {
        Unknown,
        Png,
        Jpeg,
        Bmp,
        Gif,
        Tiff,
        Webp,
    }

    /// <summary>Identifies image files by their magic bytes (URLs and content types lie).</summary>
    public static class ImageFormat
    {
        public static ImageKind Sniff(byte[] data)
        {
            if (data == null || data.Length < 12) return ImageKind.Unknown;
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return ImageKind.Png;
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ImageKind.Jpeg;
            if (data[0] == 0x42 && data[1] == 0x4D) return ImageKind.Bmp;
            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38) return ImageKind.Gif;
            if ((data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0x00) ||
                (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2A)) return ImageKind.Tiff;
            if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return ImageKind.Webp;
            return ImageKind.Unknown;
        }

        public static string Extension(ImageKind kind) => kind switch
        {
            ImageKind.Png => ".png",
            ImageKind.Jpeg => ".jpg",
            ImageKind.Bmp => ".bmp",
            ImageKind.Gif => ".gif",
            ImageKind.Tiff => ".tif",
            ImageKind.Webp => ".webp",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        public static string MimeType(ImageKind kind) => kind switch
        {
            ImageKind.Png => "image/png",
            ImageKind.Jpeg => "image/jpeg",
            ImageKind.Bmp => "image/bmp",
            ImageKind.Gif => "image/gif",
            ImageKind.Tiff => "image/tiff",
            ImageKind.Webp => "image/webp",
            _ => "application/octet-stream",
        };

        /// <summary>Reads pixel dimensions from the file header (PNG, JPEG, GIF, BMP). No decoding.</summary>
        public static bool TryGetSize(byte[] d, out int width, out int height)
        {
            width = height = 0;
            switch (Sniff(d))
            {
                case ImageKind.Png:
                    if (d.Length < 24) return false;
                    width = BigEndian32(d, 16);
                    height = BigEndian32(d, 20);
                    break;
                case ImageKind.Gif:
                    width = d[6] | (d[7] << 8);
                    height = d[8] | (d[9] << 8);
                    break;
                case ImageKind.Bmp:
                    if (d.Length < 26) return false;
                    width = BitConverter.ToInt32(d, 18);
                    height = Math.Abs(BitConverter.ToInt32(d, 22));
                    break;
                case ImageKind.Jpeg:
                    int i = 2;
                    while (i + 9 < d.Length)
                    {
                        if (d[i] != 0xFF) { i++; continue; }
                        byte marker = d[i + 1];
                        if (marker == 0xFF) { i++; continue; }
                        if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                        int len = (d[i + 2] << 8) | d[i + 3];
                        bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                        if (sof)
                        {
                            height = (d[i + 5] << 8) | d[i + 6];
                            width = (d[i + 7] << 8) | d[i + 8];
                            break;
                        }
                        i += 2 + len;
                    }
                    break;
                default:
                    return false;
            }
            return width > 0 && height > 0;
        }

        static int BigEndian32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

        /// <summary>Formats Rhino 8 can use as a bitmap texture on every platform.</summary>
        public static bool IsSupportedTexture(ImageKind kind) =>
            kind == ImageKind.Png || kind == ImageKind.Jpeg || kind == ImageKind.Bmp ||
            kind == ImageKind.Tiff || kind == ImageKind.Gif;
    }
}
