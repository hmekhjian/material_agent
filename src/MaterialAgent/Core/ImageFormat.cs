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

        /// <summary>Formats Rhino 8 can use as a bitmap texture on every platform.</summary>
        public static bool IsSupportedTexture(ImageKind kind) =>
            kind == ImageKind.Png || kind == ImageKind.Jpeg || kind == ImageKind.Bmp ||
            kind == ImageKind.Tiff || kind == ImageKind.Gif;
    }
}
