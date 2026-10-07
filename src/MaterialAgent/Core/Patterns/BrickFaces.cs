using System;
using System.Collections.Generic;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MaterialAgent.Core.Patterns
{
    /// <summary>One brick's face cut out of a photo, without mortar.</summary>
    public sealed class BrickFace
    {
        public Rgb24[] Pixels { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public sealed class BrickFaceSet
    {
        public List<BrickFace> Faces { get; } = new List<BrickFace>();
        public byte MortarR { get; set; }
        public byte MortarG { get; set; }
        public byte MortarB { get; set; }
        /// <summary>Median brick height between bed joints in the analysed image, px.</summary>
        public double CourseHeightPx { get; set; }
        /// <summary>Median stretcher face width in the analysed image, px (0 if none found).</summary>
        public double StretcherWidthPx { get; set; }
        /// <summary>Width of the analysed image in px (the photo may have been downscaled for analysis).</summary>
        public int AnalysedWidthPx { get; set; }
        public bool IsBrickwork => Faces.Count >= 4;
    }

    /// <summary>
    /// Finds individual bricks in a photo of brickwork: identifies the mortar colour (the colour that forms long
    /// horizontal lines, i.e. bed joints), splits the image into courses at the bed joints, splits each course at the
    /// perpends, and cuts out each brick face slightly inset. The faces carry the real colour and texture of the
    /// product with no mortar in them, so bonds can be rebuilt from them.
    /// </summary>
    public static class BrickFaceExtractor
    {
        const int MaxAnalysePx = 1200;
        const int MaxFaces = 160;

        public static BrickFaceSet Extract(byte[] imageBytes)
        {
            var set = new BrickFaceSet();
            Rgb24[] px; int w, h;
            using (var img = Image.Load<Rgb24>(imageBytes))
            {
                while (img.Frames.Count > 1) img.Frames.RemoveFrame(img.Frames.Count - 1);
                if (img.Width > MaxAnalysePx || img.Height > MaxAnalysePx)
                    img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(MaxAnalysePx, MaxAnalysePx) }));
                w = img.Width; h = img.Height;
                px = new Rgb24[w * h];
                img.CopyPixelDataTo(px);
            }
            set.AnalysedWidthPx = w;
            if (w < 40 || h < 40) return set;

            var mortar = FindMortar(px, w, h);
            if (mortar == null) return set;
            var isMortar = mortar.Value.mask;
            var mc = mortar.Value.color;
            set.MortarR = mc.R; set.MortarG = mc.G; set.MortarB = mc.B;

            // Bed joints: rows mostly mortar (smoothed over 3 rows so a ragged joint still counts).
            var rowCov = new double[h];
            for (int y = 0; y < h; y++)
            {
                int n = 0;
                for (int x = 0; x < w; x++) if (isMortar[y * w + x]) n++;
                rowCov[y] = (double)n / w;
            }
            var bedRows = Smooth(rowCov).Select(c => c >= 0.45).ToArray();
            var courses = Gaps(bedRows, minLength: 4);
            if (courses.Count < 2) return set;
            set.CourseHeightPx = Median(courses.Select(c => (double)(c.end - c.start)));

            var lum = new float[px.Length];
            for (int i = 0; i < px.Length; i++) lum[i] = 0.2126f * px[i].R + 0.7152f * px[i].G + 0.0722f * px[i].B;

            var stretcherWidths = new List<double>();
            var jointLike = new bool[px.Length];
            foreach (var (y0, y1) in courses)
            {
                int bandH = y1 - y0;
                if (bandH < set.CourseHeightPx * 0.6) continue; // a sliver at the image edge
                int iy0 = y0 + bandH / 8, iy1 = y1 - bandH / 8;  // ignore the joint edges when finding perpends

                // Perpends show either as mortar or as deep shadow (a recessed joint): both count as joint.
                var faceLum = new List<float>();
                for (int y = iy0; y < iy1; y++)
                    for (int x = 0; x < w; x += 3)
                        if (!isMortar[y * w + x]) faceLum.Add(lum[y * w + x]);
                float median = faceLum.Count > 0 ? faceLum.OrderBy(v => v).ElementAt(faceLum.Count / 2) : 128f;
                for (int y = y0; y < y1; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        jointLike[i] = isMortar[i] || lum[i] < median * 0.62f;
                    }

                var colCov = new double[w];
                for (int x = 0; x < w; x++)
                {
                    int n = 0;
                    for (int y = iy0; y < iy1; y++) if (jointLike[y * w + x]) n++;
                    colCov[x] = (double)n / Math.Max(1, iy1 - iy0);
                }
                // Perpends: columns mostly mortar/shadow, or a narrow valley in the column brightness profile
                // (a recessed joint between two dark bricks is only a little darker than the bricks).
                var profile = new double[w];
                for (int x = 0; x < w; x++)
                {
                    double sum = 0;
                    for (int y = iy0; y < iy1; y++) sum += lum[y * w + x];
                    profile[x] = sum / Math.Max(1, iy1 - iy0);
                }
                var valley = Valleys(profile, Math.Max(4, bandH / 2));
                var smoothed = Smooth(colCov);
                var perpCols = Enumerable.Range(0, w).Select(x => smoothed[x] >= 0.5 || valley[x]).ToArray();
                var bricks = Gaps(perpCols, minLength: Math.Max(3, (int)(bandH * 0.6)));
                foreach (var (x0, x1) in bricks)
                {
                    if (x0 <= 1 || x1 >= w - 1) continue; // cut off by the photo edge
                    int bw = x1 - x0;
                    if (bw > bandH * 2) stretcherWidths.Add(bw);
                    var face = Cut(px, w, x0, y0, x1, y1, jointLike);
                    if (face != null && set.Faces.Count < MaxFaces) set.Faces.Add(face);
                }
            }
            set.StretcherWidthPx = stretcherWidths.Count > 0 ? Median(stretcherWidths) : 0;
            return set;
        }

        /// <summary>Real width the photo covers, from a known brick length, or 0 if it can't be measured.</summary>
        public static double PhotoWidthMm(BrickFaceSet set, double brickLengthMm, double jointMm)
        {
            if (set == null || set.StretcherWidthPx <= 0 || brickLengthMm <= 0) return 0;
            return set.AnalysedWidthPx * (brickLengthMm / set.StretcherWidthPx);
        }

        static BrickFace Cut(Rgb24[] px, int w, int x0, int y0, int x1, int y1, bool[] isMortar)
        {
            // Inset so joint edges and arrises (often shadowed or chipped) stay out.
            int ix = Math.Max(1, (x1 - x0) / 14), iy = Math.Max(1, (y1 - y0) / 7);
            x0 += ix; x1 -= ix; y0 += iy; y1 -= iy;
            int fw = x1 - x0, fh = y1 - y0;
            if (fw < 6 || fh < 4) return null;
            var face = new Rgb24[fw * fh];
            int mortar = 0;
            for (int y = 0; y < fh; y++)
                for (int x = 0; x < fw; x++)
                {
                    int i = (y + y0) * w + (x + x0);
                    face[y * fw + x] = px[i];
                    if (isMortar[i]) mortar++;
                }
            // Reject cuts that still contain a joint: too much joint overall, or a joint-like vertical strip
            // (a perpend that was too faint to split the course).
            if (mortar > face.Length * 0.12) return null;
            var profile = new double[fw];
            for (int x = 0; x < fw; x++)
            {
                int n = 0; double sum = 0;
                for (int y = 0; y < fh; y++)
                {
                    var p = face[y * fw + x];
                    sum += 0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B;
                    if (isMortar[(y + y0) * w + (x + x0)]) n++;
                }
                if (n > fh * 0.4) return null;
                profile[x] = sum / fh;
            }
            var valley = Valleys(profile, Math.Max(4, fh / 2));
            for (int x = 3; x < fw - 3; x++) if (valley[x]) return null;
            return new BrickFace { Pixels = face, Width = fw, Height = fh };
        }

        /// <summary>
        /// The mortar colour is the cluster that forms long horizontal lines. Returns its mask and mean colour,
        /// or null when the photo doesn't look like brickwork (e.g. a single brick on a white background).
        /// </summary>
        static (bool[] mask, Rgb24 color)? FindMortar(Rgb24[] px, int w, int h)
        {
            const int k = 4;
            var centres = KMeans(px, k);
            var assign = new int[px.Length];
            for (int i = 0; i < px.Length; i++) assign[i] = Nearest(px[i], centres);

            int best = -1; double bestScore = 0;
            for (int c = 0; c < k; c++)
            {
                double share = assign.Count(a => a == c) / (double)px.Length;
                if (share < 0.04 || share > 0.45) continue; // mortar is a minority, but not a speck
                int lineRows = 0;
                for (int y = 0; y < h; y++)
                {
                    int n = 0;
                    for (int x = 0; x < w; x++) if (assign[y * w + x] == c) n++;
                    if (n > w * 0.5) lineRows++;
                }
                double score = lineRows / (double)h;
                if (score > bestScore) { bestScore = score; best = c; }
            }
            if (best < 0 || bestScore < 0.04) return null;

            // Mortar often splits into a lit and a shadowed cluster: merge clusters close to the chosen one.
            var mask = new bool[px.Length];
            long r = 0, g = 0, b = 0; int count = 0;
            for (int i = 0; i < px.Length; i++)
            {
                var cc = centres[assign[i]];
                if (assign[i] == best || Dist(cc, centres[best]) < 35)
                {
                    mask[i] = true;
                    r += px[i].R; g += px[i].G; b += px[i].B; count++;
                }
            }
            return (mask, new Rgb24((byte)(r / count), (byte)(g / count), (byte)(b / count)));
        }

        static (double r, double g, double b)[] KMeans(Rgb24[] px, int k)
        {
            int step = Math.Max(1, px.Length / 20000);
            var sample = new List<Rgb24>();
            for (int i = 0; i < px.Length; i += step) sample.Add(px[i]);
            var sorted = sample.OrderBy(p => p.R * 0.2126 + p.G * 0.7152 + p.B * 0.0722).ToList();
            var centres = Enumerable.Range(0, k).Select(i => { var p = sorted[(int)((i + 0.5) * sorted.Count / k)]; return ((double)p.R, (double)p.G, (double)p.B); }).ToArray();
            for (int iter = 0; iter < 10; iter++)
            {
                var sum = new (double r, double g, double b, int n)[k];
                foreach (var p in sample)
                {
                    int c = Nearest(p, centres);
                    sum[c] = (sum[c].r + p.R, sum[c].g + p.G, sum[c].b + p.B, sum[c].n + 1);
                }
                for (int c = 0; c < k; c++)
                    if (sum[c].n > 0) centres[c] = (sum[c].r / sum[c].n, sum[c].g / sum[c].n, sum[c].b / sum[c].n);
            }
            return centres;
        }

        static int Nearest(Rgb24 p, (double r, double g, double b)[] centres)
        {
            int best = 0; double bestD = double.MaxValue;
            for (int c = 0; c < centres.Length; c++)
            {
                double dr = p.R - centres[c].r, dg = p.G - centres[c].g, db = p.B - centres[c].b, d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        static double Dist((double r, double g, double b) a, (double r, double g, double b) b) =>
            Math.Sqrt((a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b));

        /// <summary>
        /// Columns that are clearly darker, or clearly lighter, than the profile on both sides within
        /// <paramref name="reach"/> px: narrow joints. A colour change between two bricks is a step, not a valley.
        /// </summary>
        static bool[] Valleys(double[] p, int reach)
        {
            int n = p.Length;
            var o = new bool[n];
            for (int x = 0; x < n; x++)
            {
                double left = 0, right = 0; int nl = 0, nr = 0;
                for (int k = 3; k <= reach; k++)
                {
                    if (x - k >= 0) { left += p[x - k]; nl++; }
                    if (x + k < n) { right += p[x + k]; nr++; }
                }
                if (nl == 0 || nr == 0) continue;
                left /= nl; right /= nr;
                double lo = Math.Min(left, right), hi = Math.Max(left, right);
                o[x] = p[x] < lo * 0.82 || p[x] > hi * 1.18 + 6;
            }
            return o;
        }

        static double[] Smooth(double[] v)
        {
            var o = new double[v.Length];
            for (int i = 0; i < v.Length; i++)
                o[i] = (v[Math.Max(0, i - 1)] + v[i] + v[Math.Min(v.Length - 1, i + 1)]) / 3;
            return o;
        }

        /// <summary>Runs of false values (the spaces between joints) at least <paramref name="minLength"/> long.</summary>
        static List<(int start, int end)> Gaps(bool[] joint, int minLength)
        {
            var runs = new List<(int, int)>();
            int start = -1;
            for (int i = 0; i <= joint.Length; i++)
            {
                bool isGap = i < joint.Length && !joint[i];
                if (isGap && start < 0) start = i;
                if (!isGap && start >= 0) { if (i - start >= minLength) runs.Add((start, i)); start = -1; }
            }
            return runs;
        }

        static double Median(IEnumerable<double> values)
        {
            var v = values.OrderBy(x => x).ToList();
            return v.Count == 0 ? 0 : v[v.Count / 2];
        }
    }
}
