using System;

namespace MaterialAgent.Core.Maps
{
    /// <summary>Derived maps as raw 8-bit pixels, row-major, top row first.</summary>
    public sealed class DerivedMaps
    {
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>RGB triplets, tangent-space normal map (OpenGL convention: green = +V, up).</summary>
        public byte[] NormalRgb { get; set; }
        /// <summary>One byte per pixel, 0 = smooth, 255 = rough.</summary>
        public byte[] Roughness { get; set; }
    }

    /// <summary>
    /// Approximates normal and roughness maps from an albedo image. Product pages almost never offer
    /// real PBR maps; these give believable relief and sheen variation, not measured data.
    /// Sampling wraps at the edges so the maps tile like the texture does.
    /// </summary>
    public static class SurfaceMaps
    {
        /// <summary>Rec. 709 luma, 0..1.</summary>
        public static float Luminance(byte r, byte g, byte b) => (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255f;

        /// <param name="luminance">Width*height values in 0..1, row-major, top row first.</param>
        /// <param name="baseRoughness">Finish preset roughness (see <see cref="EnumText.Roughness"/>).</param>
        /// <param name="normalStrength">Relief strength; ~2 suits wood grain and stone, higher for rough surfaces.</param>
        /// <param name="roughnessVariation">How far roughness may move from the base with image detail.</param>
        public static DerivedMaps Generate(float[] luminance, int width, int height, double baseRoughness,
            double normalStrength = 2.0, double roughnessVariation = 0.15)
        {
            if (luminance == null) throw new ArgumentNullException(nameof(luminance));
            if (width < 2 || height < 2 || luminance.Length != width * height)
                throw new ArgumentException("Luminance size does not match width × height.");

            var heightField = BoxBlur(luminance, width, height);

            // Image statistics for roughness: darker than average (grain, pores, grout) reads rougher.
            double mean = 0, sq = 0;
            foreach (var v in luminance) { mean += v; sq += v * v; }
            mean /= luminance.Length;
            double std = Math.Sqrt(Math.Max(1e-8, sq / luminance.Length - mean * mean));

            var normal = new byte[width * height * 3];
            var rough = new byte[width * height];
            // Sobel on a 0..1 field gives gradients of at most ~4; scale so strength 1 is gentle.
            double k = normalStrength * 0.25 * Math.Max(width, height) / 256.0;

            for (int y = 0; y < height; y++)
            {
                int ym = Wrap(y - 1, height), yp = Wrap(y + 1, height);
                for (int x = 0; x < width; x++)
                {
                    int xm = Wrap(x - 1, width), xp = Wrap(x + 1, width);
                    float tl = heightField[ym * width + xm], t = heightField[ym * width + x], tr = heightField[ym * width + xp];
                    float l = heightField[y * width + xm], r = heightField[y * width + xp];
                    float bl = heightField[yp * width + xm], b = heightField[yp * width + x], br = heightField[yp * width + xp];

                    double dX = (tr + 2 * r + br) - (tl + 2 * l + bl);       // d(height)/d(x), x to the right
                    double dRow = (bl + 2 * b + br) - (tl + 2 * t + tr);     // d(height)/d(row), rows go down
                    double dV = -dRow;                                         // texture V goes up

                    double nx = -dX * k, ny = -dV * k, nz = 1.0;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    int i = (y * width + x) * 3;
                    normal[i] = ToByte((nx / len + 1) * 0.5);
                    normal[i + 1] = ToByte((ny / len + 1) * 0.5);
                    normal[i + 2] = ToByte((nz / len + 1) * 0.5);

                    double z = Math.Max(-2, Math.Min(2, (luminance[y * width + x] - mean) / std));
                    double rv = baseRoughness - roughnessVariation * z * 0.5;
                    rough[y * width + x] = ToByte(Math.Max(0.02, Math.Min(1.0, rv)));
                }
            }

            return new DerivedMaps { Width = width, Height = height, NormalRgb = normal, Roughness = rough };
        }

        /// <summary>Picks a working size no larger than <paramref name="maxSide"/>, keeping proportions.</summary>
        public static (int width, int height) FitWithin(int width, int height, int maxSide)
        {
            if (width <= maxSide && height <= maxSide) return (width, height);
            double s = (double)maxSide / Math.Max(width, height);
            return (Math.Max(2, (int)Math.Round(width * s)), Math.Max(2, (int)Math.Round(height * s)));
        }

        static float[] BoxBlur(float[] src, int w, int h)
        {
            var dst = new float[src.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int row = Wrap(y + dy, h) * w;
                        for (int dx = -1; dx <= 1; dx++) sum += src[row + Wrap(x + dx, w)];
                    }
                    dst[y * w + x] = sum / 9f;
                }
            }
            return dst;
        }

        static int Wrap(int i, int n) => i < 0 ? i + n : i >= n ? i - n : i;
        static byte ToByte(double v01) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v01 * 255, MidpointRounding.AwayFromZero)));
    }
}
