using System;
using System.Collections.Generic;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MaterialAgent.Core.Patterns
{
    public struct PaletteColor
    {
        public byte R, G, B;
        /// <summary>Share of the image's (non-background) pixels, 0..1.</summary>
        public double Weight;
        public PaletteColor(byte r, byte g, byte b, double weight) { R = r; G = g; B = b; Weight = weight; }
        public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    }

    /// <summary>
    /// Main colours of a product photo (k-means), e.g. the reds, browns and purples of a multi brick.
    /// White studio backgrounds are ignored. The user can switch colours off (e.g. mortar in a wall photo).
    /// </summary>
    public static class Palette
    {
        public static List<PaletteColor> Extract(byte[] imageBytes, int k = 6, int seed = 1)
        {
            var pixels = new List<(double r, double g, double b)>();
            using (var img = Image.Load<Rgb24>(imageBytes))
            {
                img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(128, 128) }));
                var px = new Rgb24[img.Width * img.Height];
                img.CopyPixelDataTo(px);
                foreach (var p in px)
                {
                    int max = Math.Max(p.R, Math.Max(p.G, p.B)), min = Math.Min(p.R, Math.Min(p.G, p.B));
                    if (min > 235 && max - min < 12) continue; // white background
                    pixels.Add((p.R, p.G, p.B));
                }
            }
            if (pixels.Count == 0) return new List<PaletteColor>();
            k = Math.Max(1, Math.Min(k, pixels.Count));

            // Deterministic start: centres spread across the luminance range.
            var sorted = pixels.OrderBy(p => 0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b).ToList();
            var centres = Enumerable.Range(0, k).Select(i => sorted[(int)((i + 0.5) * sorted.Count / k)]).ToArray();
            var assign = new int[pixels.Count];
            for (int iter = 0; iter < 12; iter++)
            {
                for (int i = 0; i < pixels.Count; i++)
                {
                    int best = 0; double bestD = double.MaxValue;
                    for (int c = 0; c < k; c++)
                    {
                        double dr = pixels[i].r - centres[c].r, dg = pixels[i].g - centres[c].g, db = pixels[i].b - centres[c].b;
                        double d = dr * dr + dg * dg + db * db;
                        if (d < bestD) { bestD = d; best = c; }
                    }
                    assign[i] = best;
                }
                for (int c = 0; c < k; c++)
                {
                    double r = 0, g = 0, b = 0; int n = 0;
                    for (int i = 0; i < pixels.Count; i++)
                        if (assign[i] == c) { r += pixels[i].r; g += pixels[i].g; b += pixels[i].b; n++; }
                    if (n > 0) centres[c] = (r / n, g / n, b / n);
                }
            }

            var counts = new int[k];
            foreach (var a in assign) counts[a]++;
            return Enumerable.Range(0, k)
                .Select(c => new PaletteColor(ToByte(centres[c].r), ToByte(centres[c].g), ToByte(centres[c].b), (double)counts[c] / pixels.Count))
                .Where(c => c.Weight >= 0.03)
                .OrderByDescending(c => c.Weight)
                .ToList();
        }

        static byte ToByte(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v)));
    }
}
