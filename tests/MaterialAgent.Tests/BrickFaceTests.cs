using System;
using System.IO;
using System.Linq;
using MaterialAgent.Core.Patterns;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MaterialAgent.Tests
{
    public class BrickFaceTests
    {
        static readonly Rgb24 Mortar = new Rgb24(200, 196, 188);

        /// <summary>
        /// A stretcher-bond wall photo: 120 × 36 px bricks (≈ 215 × 65 mm at 0.56 px/mm), 6 px joints,
        /// several brick colours with noise, and every third perpend in deep shadow instead of mortar.
        /// </summary>
        static byte[] Wall(out int brickW)
        {
            const int bw = 120, bh = 36, j = 6, w = 900, h = 600;
            brickW = bw;
            var colours = new[] { new Rgb24(150, 60, 40), new Rgb24(120, 45, 38), new Rgb24(95, 55, 60), new Rgb24(170, 90, 55) };
            var rnd = new Random(7);
            using (var img = new Image<Rgb24>(w, h))
            {
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) img[x, y] = Mortar;
                int course = 0;
                for (int y0 = 0; y0 < h; y0 += bh + j, course++)
                {
                    int offset = course % 2 == 0 ? 0 : -(bw + j) / 2;
                    int brick = 0;
                    for (int x0 = offset; x0 < w; x0 += bw + j, brick++)
                    {
                        var c = colours[rnd.Next(colours.Length)];
                        for (int y = y0; y < Math.Min(h, y0 + bh); y++)
                            for (int x = Math.Max(0, x0); x < Math.Min(w, x0 + bw); x++)
                            {
                                int n = rnd.Next(-12, 12);
                                img[x, y] = new Rgb24((byte)(c.R + n), (byte)(c.G + n), (byte)(c.B + n));
                            }
                        if (brick % 3 == 0) // shadowed perpend right of this brick
                            for (int y = y0; y < Math.Min(h, y0 + bh); y++)
                                for (int x = x0 + bw; x < x0 + bw + j; x++)
                                    if (x >= 0 && x < w) img[x, y] = new Rgb24(40, 45, 42);
                    }
                }
                using (var ms = new MemoryStream()) { img.Save(ms, new PngEncoder()); return ms.ToArray(); }
            }
        }

        [Fact]
        public void CutsBricksOutOfAWallPhoto()
        {
            var set = BrickFaceExtractor.Extract(Wall(out int bw));
            Assert.True(set.IsBrickwork);
            Assert.True(set.Faces.Count >= 40, $"only {set.Faces.Count} faces");
            Assert.InRange(set.MortarR, 190, 210);
            Assert.InRange(set.CourseHeightPx, 34, 40);          // brick height between bed joints (36)
            Assert.InRange(set.StretcherWidthPx, bw - 4, bw + 8);

            // No face contains mortar or shadowed joint.
            foreach (var f in set.Faces)
            {
                int light = f.Pixels.Count(p => p.R > 185 && p.G > 180);
                int dark = f.Pixels.Count(p => p.R < 60);
                Assert.True(light == 0 && dark == 0, $"face {f.Width}x{f.Height} has {light} mortar and {dark} shadow pixels");
            }

            // Known brick length measures the photo: 900 px at 120 px per 215 mm ≈ 1612 mm.
            Assert.InRange(BrickFaceExtractor.PhotoWidthMm(set, 215, 10), 1500, 1700);
        }

        [Fact]
        public void SingleBrickOnWhiteIsNotBrickwork()
        {
            using (var img = new Image<Rgb24>(400, 200))
            {
                for (int y = 0; y < 200; y++) for (int x = 0; x < 400; x++)
                    img[x, y] = x > 50 && x < 350 && y > 60 && y < 140 ? new Rgb24(150, 60, 40) : new Rgb24(255, 255, 255);
                using (var ms = new MemoryStream())
                {
                    img.Save(ms, new PngEncoder());
                    Assert.False(BrickFaceExtractor.Extract(ms.ToArray()).IsBrickwork);
                }
            }
        }

        [Fact]
        public void FacesModeBuildsBondWithoutMortarInsideBricks()
        {
            var set = BrickFaceExtractor.Extract(Wall(out _));
            var layout = PatternLayout.Create(new PatternSpec { Kind = PatternKind.Flemish });
            var r = PatternRenderer.Render(layout, new PatternStyle { Fill = FillMode.Faces, MaxPixels = 600, JointR = 0, JointG = 0, JointB = 255 },
                new PatternSource { Faces = set.Faces });
            using (var img = Image.Load<Rgb24>(r.Albedo))
            {
                // Sample the centre of every unit in the first repeat: brick colours only.
                double sx = img.Width / r.WidthMm, sy = img.Height / r.HeightMm;
                foreach (var u in layout.Units)
                {
                    double cx = (u.X + u.W / 2) % layout.WidthMm, cy = (u.Y + u.H / 2) % layout.HeightMm;
                    var p = img[(int)(cx * sx), img.Height - 1 - (int)(cy * sy)];
                    Assert.True(p.R > 70 && p.B < 120, $"unit centre ({cx:0}, {cy:0}) is {p}");
                }
            }
        }
    }
}
