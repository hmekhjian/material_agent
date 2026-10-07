using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace MaterialAgent.Core.Patterns
{
    public enum FillMode
    {
        /// <summary>Each unit shows a different crop of the product texture at true scale (planks, tiles, stone).</summary>
        Texture,
        /// <summary>Each unit gets a colour from the product photo's palette plus its surface detail (bricks).</summary>
        Palette,
    }

    public sealed class PatternStyle
    {
        public FillMode Fill { get; set; } = FillMode.Texture;
        public byte JointR { get; set; } = 190;
        public byte JointG { get; set; } = 184;
        public byte JointB { get; set; } = 172;
        /// <summary>How far joints are recessed, mm (0 = flush). Drives the normal map.</summary>
        public double JointDepthMm { get; set; } = 4;
        /// <summary>Unit-to-unit colour/brightness variation, 0..1.</summary>
        public double Variation { get; set; } = 0.35;
        public int Seed { get; set; } = 1;
        /// <summary>Base face roughness (from the finish).</summary>
        public double Roughness { get; set; } = 0.75;
        /// <summary>The source texture's grain runs vertically; turn it so grain follows each unit's length.</summary>
        public bool SourceGrainVertical { get; set; }
        /// <summary>Longest side of the output in pixels.</summary>
        public int MaxPixels { get; set; } = 2048;
        /// <summary>Repeat the pattern until the tile is at least this big, so unit variation doesn't visibly repeat.</summary>
        public double MinTileMm { get; set; } = 1200;
    }

    public sealed class PatternSource
    {
        /// <summary>Product image (any format ImageSharp reads). Optional in palette mode.</summary>
        public byte[] ImageBytes { get; set; }
        /// <summary>Real width the image covers, mm; 0 if unknown.</summary>
        public double ImageWidthMm { get; set; }
        /// <summary>Colours for palette mode; extracted from the image when empty.</summary>
        public List<PaletteColor> Palette { get; set; } = new List<PaletteColor>();
    }

    public sealed class PatternResult
    {
        public byte[] Albedo { get; set; }
        public byte[] Normal { get; set; }
        public byte[] Roughness { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        /// <summary>Real size of the whole texture tile, mm (exact: built from the unit sizes).</summary>
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public int RepeatX { get; set; }
        public int RepeatY { get; set; }
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// Renders a pattern into a seamless albedo + normal + roughness set whose real size is known exactly.
    /// Units are filled from a real product (texture crops at true scale, or its colour palette), joints get
    /// their own colour and are recessed in the height map.
    /// </summary>
    public static class PatternRenderer
    {
        public static PatternResult Render(PatternLayout layout, PatternStyle style, PatternSource source)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            style = style ?? new PatternStyle();
            source = source ?? new PatternSource();

            int rx = Math.Max(1, (int)Math.Ceiling(style.MinTileMm / layout.WidthMm));
            int ry = Math.Max(1, (int)Math.Ceiling(style.MinTileMm / layout.HeightMm));
            double tileW = rx * layout.WidthMm, tileH = ry * layout.HeightMm;
            double ppm = Math.Min(2.0, style.MaxPixels / Math.Max(tileW, tileH)); // pixels per mm
            int pw = Math.Max(8, (int)Math.Round(tileW * ppm)), ph = Math.Max(8, (int)Math.Round(tileH * ppm));
            double sx = pw / tileW, sy = ph / tileH;

            var result = new PatternResult { WidthMm = tileW, HeightMm = tileH, RepeatX = rx, RepeatY = ry, PixelWidth = pw, PixelHeight = ph };
            result.Notes.AddRange(layout.Notes);

            // Source pixels (decoded once).
            Rgb24[] src = null; int sw = 0, sh = 0; float[] srcDetail = null;
            if (source.ImageBytes != null)
            {
                using (var img = Image.Load<Rgb24>(source.ImageBytes))
                {
                    while (img.Frames.Count > 1) img.Frames.RemoveFrame(img.Frames.Count - 1);
                    sw = img.Width; sh = img.Height;
                    src = new Rgb24[sw * sh];
                    img.CopyPixelDataTo(src);
                }
                (src, sw, sh) = CropToProduct(src, sw, sh);
                if (style.SourceGrainVertical) (src, sw, sh) = Rotate90(src, sw, sh);
                srcDetail = HighPass(src, sw, sh);
            }
            if (style.Fill == FillMode.Texture && src == null) throw new InvalidOperationException("Texture fill needs a product image.");

            var palette = source.Palette?.Count > 0 ? source.Palette
                : source.ImageBytes != null ? Palette.Extract(source.ImageBytes) : new List<PaletteColor>();
            if (style.Fill == FillMode.Palette && palette.Count == 0) throw new InvalidOperationException("Palette fill needs colours or a product image.");

            // Real size of one source pixel. Unknown: assume the image spans about 6 unit lengths.
            double unitLen = layout.Units.Count > 0 ? layout.Units.Max(u => Math.Max(u.W, u.H)) : 200;
            double srcMm = src == null ? 1 : (source.ImageWidthMm > 0 ? source.ImageWidthMm / sw : 6 * unitLen / sw);
            if (src != null && source.ImageWidthMm <= 0) result.Notes.Add("Product image size unknown; assumed it spans about 6 unit lengths.");

            var albedo = new Rgb24[pw * ph];
            var height = new float[pw * ph];
            var rough = new byte[pw * ph];
            var rng = new Random(style.Seed);

            // Joints everywhere first.
            double jointDepth = Math.Max(0, style.JointDepthMm);
            byte jointRough = ToByte(0.92);
            for (int i = 0; i < albedo.Length; i++)
            {
                double n = 1 + (Hash(i, style.Seed) - 0.5) * 0.08;
                albedo[i] = new Rgb24(ToByte(style.JointR * n / 255), ToByte(style.JointG * n / 255), ToByte(style.JointB * n / 255));
                height[i] = (float)-jointDepth;
                rough[i] = jointRough;
            }

            double bevelMm = Math.Min(2.0, layout.JointMm * 0.25 + 0.5);
            for (int cy = 0; cy < ry; cy++)
                for (int cx = 0; cx < rx; cx++)
                    foreach (var u in layout.Units)
                        DrawUnit(u, cx * layout.WidthMm, cy * layout.HeightMm);

            void DrawUnit(UnitRect u, double ox, double oy)
            {
                // Per-unit randomness: texture offset, palette colour, brightness, roughness. The crop is placed so
                // it fits inside the source when possible: product photos rarely tile, and wrapping would show a seam.
                double lenAlong = u.Vertical ? u.H : u.W, lenAcross = u.Vertical ? u.W : u.H;
                double offU = src == null ? 0 : rng.NextDouble() * Math.Max(0, sw * srcMm - lenAlong);
                double offV = src == null ? 0 : rng.NextDouble() * Math.Max(0, sh * srcMm - lenAcross);
                double bright = 1 + (rng.NextDouble() - 0.5) * style.Variation * 0.35;
                var baseColor = PickPalette(palette, rng);
                double tint = (rng.NextDouble() - 0.5) * style.Variation * 0.12;
                double faceRough = Math.Max(0.05, Math.Min(1, style.Roughness + (rng.NextDouble() - 0.5) * 0.1));

                int x0 = (int)Math.Round((ox + u.X) * sx), x1 = (int)Math.Round((ox + u.X + u.W) * sx);
                int y0 = (int)Math.Round((oy + u.Y) * sy), y1 = (int)Math.Round((oy + u.Y + u.H) * sy);
                for (int py = y0; py < y1; py++)
                {
                    int wy = Mod(py, ph);
                    int row = ph - 1 - wy; // image rows run down, pattern Y runs up
                    double vy = (py - y0 + 0.5) / sy;
                    for (int px = x0; px < x1; px++)
                    {
                        int wx = Mod(px, pw);
                        double vx = (px - x0 + 0.5) / sx;
                        // Unit-local coordinates: along the unit's length, and across it.
                        double along = u.Vertical ? vy : vx, across = u.Vertical ? vx : vy;
                        double edge = Math.Min(Math.Min(vx, u.W - vx), Math.Min(vy, u.H - vy));
                        double bevel = Smooth(edge / bevelMm);

                        double r, g, b;
                        if (style.Fill == FillMode.Texture)
                        {
                            var s = Sample(src, sw, sh, (offU + along) / srcMm, (offV + across) / srcMm);
                            r = s.R; g = s.G; b = s.B;
                        }
                        else
                        {
                            r = baseColor.R * (1 + tint); g = baseColor.G; b = baseColor.B * (1 - tint);
                            if (srcDetail != null)
                            {
                                double d = SampleF(srcDetail, sw, sh, (offU + along) / srcMm, (offV + across) / srcMm);
                                r += d; g += d; b += d;
                            }
                            double speck = (Hash(wx * 7919 + wy * 104729, style.Seed) - 0.5) * 10 * (0.5 + style.Variation);
                            r += speck; g += speck; b += speck;
                        }
                        double k = bright * (0.82 + 0.18 * bevel); // edges slightly darker
                        int i = row * pw + wx;
                        albedo[i] = new Rgb24(ToByte(r * k / 255), ToByte(g * k / 255), ToByte(b * k / 255));
                        height[i] = (float)(-jointDepth * (1 - bevel));
                        rough[i] = ToByte(faceRough);
                    }
                }
            }

            result.Albedo = Encode(albedo, pw, ph, jpeg: true);
            result.Normal = Encode(NormalFromHeight(height, pw, ph, ppm), pw, ph, jpeg: false);
            using (var r = Image.LoadPixelData<L8>(rough.Select(v => new L8(v)).ToArray(), pw, ph))
            using (var ms = new MemoryStream())
            {
                r.Save(ms, new PngEncoder());
                result.Roughness = ms.ToArray();
            }
            return result;
        }

        static PaletteColor PickPalette(List<PaletteColor> palette, Random rng)
        {
            if (palette.Count == 0) return new PaletteColor(128, 128, 128, 1);
            double total = palette.Sum(p => p.Weight), t = rng.NextDouble() * total;
            foreach (var p in palette) { if ((t -= p.Weight) <= 0) return p; }
            return palette[palette.Count - 1];
        }

        // Mirrored addressing: if a crop is larger than the source it reflects instead of wrapping (no hard seam).
        static Rgb24 Sample(Rgb24[] src, int w, int h, double x, double y) => src[Mirror((int)Math.Floor(y), h) * w + Mirror((int)Math.Floor(x), w)];
        static double SampleF(float[] src, int w, int h, double x, double y) => src[Mirror((int)Math.Floor(y), h) * w + Mirror((int)Math.Floor(x), w)];

        static int Mirror(int v, int n)
        {
            int p = Mod(v, 2 * n);
            return p < n ? p : 2 * n - 1 - p;
        }

        /// <summary>
        /// Luminance minus a blurred copy: surface detail without the colour, for palette mode. Studio-white
        /// background is replaced by the average first and the result is capped, so a product photo's outline
        /// doesn't turn into streaks.
        /// </summary>
        static float[] HighPass(Rgb24[] src, int w, int h)
        {
            var lum = new float[w * h];
            var background = new bool[w * h];
            double sum = 0; int count = 0;
            for (int i = 0; i < lum.Length; i++)
            {
                var p = src[i];
                int max = Math.Max(p.R, Math.Max(p.G, p.B)), min = Math.Min(p.R, Math.Min(p.G, p.B));
                background[i] = min > 235 && max - min < 12;
                lum[i] = 0.2126f * p.R + 0.7152f * p.G + 0.0722f * p.B;
                if (!background[i]) { sum += lum[i]; count++; }
            }
            float mean = count > 0 ? (float)(sum / count) : 128f;
            for (int i = 0; i < lum.Length; i++) if (background[i]) lum[i] = mean;
            int r = Math.Max(2, Math.Min(w, h) / 64);
            var blur = BoxBlur(BoxBlur(lum, w, h, r, true), w, h, r, false);
            var d = new float[lum.Length];
            for (int i = 0; i < d.Length; i++) d[i] = Math.Max(-22f, Math.Min(22f, lum[i] - blur[i]));
            return d;
        }

        static float[] BoxBlur(float[] s, int w, int h, int r, bool horizontal)
        {
            var o = new float[s.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0;
                    for (int k = -r; k <= r; k++)
                        sum += horizontal ? s[y * w + Mod(x + k, w)] : s[Mod(y + k, h) * w + x];
                    o[y * w + x] = sum / (2 * r + 1);
                }
            return o;
        }

        /// <summary>Crops away a studio-white background around the product, so crops never land on it.</summary>
        public static (Rgb24[], int, int) CropToProduct(Rgb24[] s, int w, int h)
        {
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var p = s[y * w + x];
                    int max = Math.Max(p.R, Math.Max(p.G, p.B)), min = Math.Min(p.R, Math.Min(p.G, p.B));
                    if (min > 235 && max - min < 12) continue;
                    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
            if (x1 < 0) return (s, w, h);
            // Stay a little inside the product's outline (soft, shadowed edges).
            int mx = (x1 - x0) / 20, my = (y1 - y0) / 20;
            x0 += mx; x1 -= mx; y0 += my; y1 -= my;
            int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
            if (cw < 8 || ch < 8 || (cw == w && ch == h)) return (s, w, h);
            var o = new Rgb24[cw * ch];
            for (int y = 0; y < ch; y++) Array.Copy(s, (y + y0) * w + x0, o, y * cw, cw);
            return (o, cw, ch);
        }

        static (Rgb24[], int, int) Rotate90(Rgb24[] s, int w, int h)
        {
            var o = new Rgb24[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    o[x * h + (h - 1 - y)] = s[y * w + x];
            return (o, h, w);
        }

        /// <summary>Tangent-space normal map (OpenGL convention) from a height field in mm; wraps at the edges.</summary>
        public static Rgb24[] NormalFromHeight(float[] hgt, int w, int h, double pixelsPerMm)
        {
            var n = new Rgb24[w * h];
            double k = pixelsPerMm / 8.0; // Sobel sums 8 pixel-steps of gradient; convert to mm/mm
            for (int y = 0; y < h; y++)
            {
                int ym = Mod(y - 1, h), yp = Mod(y + 1, h);
                for (int x = 0; x < w; x++)
                {
                    int xm = Mod(x - 1, w), xp = Mod(x + 1, w);
                    double dx = (hgt[ym * w + xp] + 2 * hgt[y * w + xp] + hgt[yp * w + xp]) - (hgt[ym * w + xm] + 2 * hgt[y * w + xm] + hgt[yp * w + xm]);
                    double dRow = (hgt[yp * w + xm] + 2 * hgt[yp * w + x] + hgt[yp * w + xp]) - (hgt[ym * w + xm] + 2 * hgt[ym * w + x] + hgt[ym * w + xp]);
                    double gx = dx * k, gv = -dRow * k; // image rows go down; texture V goes up
                    double len = Math.Sqrt(gx * gx + gv * gv + 1);
                    n[y * w + x] = new Rgb24(ToByte((-gx / len + 1) / 2), ToByte((-gv / len + 1) / 2), ToByte((1 / len + 1) / 2));
                }
            }
            return n;
        }

        static byte[] Encode(Rgb24[] px, int w, int h, bool jpeg)
        {
            using (var img = Image.LoadPixelData(px, w, h))
            using (var ms = new MemoryStream())
            {
                if (jpeg) img.Save(ms, new JpegEncoder { Quality = 92 });
                else img.Save(ms, new PngEncoder());
                return ms.ToArray();
            }
        }

        static double Smooth(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }
        static int Mod(int v, int m) => ((v % m) + m) % m;
        static byte ToByte(double v01) => (byte)Math.Max(0, Math.Min(255, Math.Round(v01 * 255)));

        /// <summary>Cheap deterministic 0..1 noise.</summary>
        static double Hash(int i, int seed)
        {
            unchecked
            {
                uint x = (uint)i * 747796405u + (uint)seed * 2891336453u;
                x = ((x >> ((int)(x >> 28) + 4)) ^ x) * 277803737u;
                x = (x >> 22) ^ x;
                return (x & 0xFFFFFF) / (double)0xFFFFFF;
            }
        }
    }
}
