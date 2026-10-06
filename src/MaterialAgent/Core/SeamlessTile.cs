using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace MaterialAgent.Core
{
    /// <summary>
    /// Makes an image tile without visible seams: blends it with a copy of itself shifted by half its size,
    /// using a mask that is 1 at the borders and 0 in the middle. The shifted copy is continuous across the
    /// wrap, so the borders match; its own seam sits in the middle, where the mask hides it.
    /// Deterministic and free; also used to guarantee seamless output from the AI generator.
    /// </summary>
    public static class SeamlessTile
    {
        /// <param name="imageBytes">PNG/JPEG/BMP/GIF/TIFF/WebP bytes.</param>
        /// <param name="feather">Width of the blend band as a fraction of the image size (each side).</param>
        public static byte[] MakeSeamless(byte[] imageBytes, double feather = 0.25)
        {
            if (feather <= 0 || feather > 0.5) throw new ArgumentOutOfRangeException(nameof(feather));
            using (var src = Image.Load<Rgb24>(imageBytes))
            {
                while (src.Frames.Count > 1) src.Frames.RemoveFrame(src.Frames.Count - 1);
                int w = src.Width, h = src.Height;
                var input = new Rgb24[w * h];
                src.CopyPixelDataTo(input);

                var output = new Rgb24[w * h];
                double fx = Math.Max(1, w * feather), fy = Math.Max(1, h * feather);
                int hw = w / 2, hh = h / 2;
                for (int y = 0; y < h; y++)
                {
                    double dy = Math.Min(y + 0.5, h - y - 0.5);
                    double wy = Smooth(1 - dy / fy);
                    int sy = (y + hh) % h;
                    for (int x = 0; x < w; x++)
                    {
                        double dx = Math.Min(x + 0.5, w - x - 0.5);
                        double wx = Smooth(1 - dx / fx);
                        double t = Math.Max(wx, wy);
                        var a = input[y * w + x];
                        var b = input[sy * w + (x + hw) % w];
                        output[y * w + x] = new Rgb24(Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));
                    }
                }

                using (var result = Image.LoadPixelData<Rgb24>(output, w, h))
                using (var ms = new MemoryStream())
                {
                    result.Save(ms, new JpegEncoder { Quality = 92 });
                    return ms.ToArray();
                }
            }
        }

        static double Smooth(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return t * t * (3 - 2 * t);
        }

        static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

        /// <summary>
        /// Mean absolute RGB difference across the wrap (left vs right column, top vs bottom row), 0..255.
        /// Near zero means the image tiles cleanly. Used by tests and to judge generated images.
        /// </summary>
        public static double WrapSeamError(byte[] imageBytes)
        {
            using (var img = Image.Load<Rgb24>(imageBytes))
            {
                int w = img.Width, h = img.Height;
                var px = new Rgb24[w * h];
                img.CopyPixelDataTo(px);
                double sum = 0; int n = 0;
                for (int y = 0; y < h; y++) { sum += Diff(px[y * w], px[y * w + w - 1]); n++; }
                for (int x = 0; x < w; x++) { sum += Diff(px[x], px[(h - 1) * w + x]); n++; }
                return sum / n;
            }
        }

        static double Diff(Rgb24 a, Rgb24 b) => (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3.0;
    }
}
