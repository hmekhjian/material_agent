using System;
using System.IO;
using System.Linq;
using MaterialAgent.Core;
using MaterialAgent.Core.Patterns;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MaterialAgent.Tests
{
    public class PatternLayoutTests
    {
        public static TheoryData<PatternKind, double, double, double, double> Specs => new TheoryData<PatternKind, double, double, double, double>
        {
            // kind, length, height, depth, joint
            { PatternKind.Stretcher, 215, 65, 102.5, 10 },
            { PatternKind.ThirdBond, 600, 300, 0, 3 },
            { PatternKind.QuarterBond, 1200, 190, 0, 0 },
            { PatternKind.Stack, 600, 600, 0, 2 },
            { PatternKind.Header, 215, 65, 102.5, 10 },
            { PatternKind.English, 215, 65, 102.5, 10 },
            { PatternKind.Flemish, 215, 65, 102.5, 10 },
            { PatternKind.Flemish, 290, 50, 90, 10 },
            { PatternKind.Basketweave, 215, 65, 102.5, 10 },
            { PatternKind.Basketweave, 200, 100, 0, 0 },
            { PatternKind.Herringbone, 215, 65, 102.5, 10 },
            { PatternKind.Herringbone, 600, 100, 0, 2 },
            { PatternKind.Herringbone, 1200, 190, 0, 0 },
        };

        /// <summary>Every point of the repeat lies in exactly one unit cell (face + its joint): no gaps, no overlaps.</summary>
        [Theory]
        [MemberData(nameof(Specs))]
        public void TilesWithoutGapsOrOverlaps(PatternKind kind, double l, double h, double d, double j)
        {
            var p = PatternLayout.Create(new PatternSpec { Kind = kind, UnitLengthMm = l, UnitHeightMm = h, UnitDepthMm = d, JointMm = j });
            double step = Math.Min(p.WidthMm, p.HeightMm) / 97.0; // not a divisor of unit sizes, so samples avoid cell edges
            for (double y = step * 0.37; y < p.HeightMm; y += step)
                for (double x = step * 0.37; x < p.WidthMm; x += step)
                {
                    int hits = p.Units.Count(u => Inside(x, u.X, u.W + j, p.WidthMm) && Inside(y, u.Y, u.H + j, p.HeightMm));
                    Assert.True(hits == 1, $"{kind}: point ({x:0.#}, {y:0.#}) covered {hits} times");
                }
        }

        static bool Inside(double v, double start, double size, double period)
        {
            double t = ((v - start) % period + period) % period;
            return t < size;
        }

        [Fact]
        public void RepeatSizesAreExact()
        {
            var s = PatternLayout.Create(new PatternSpec { Kind = PatternKind.Stretcher, UnitLengthMm = 215, UnitHeightMm = 65, JointMm = 10 });
            Assert.Equal((225, 150), (s.WidthMm, s.HeightMm));
            var f = PatternLayout.Create(new PatternSpec { Kind = PatternKind.Flemish, UnitLengthMm = 215, UnitHeightMm = 65, UnitDepthMm = 102.5, JointMm = 10 });
            Assert.Equal((337.5, 150), (f.WidthMm, f.HeightMm));
            var hb = PatternLayout.Create(new PatternSpec { Kind = PatternKind.Herringbone, UnitLengthMm = 215, UnitHeightMm = 65, JointMm = 10 });
            Assert.Equal((450, 450), (hb.WidthMm, hb.HeightMm));
            Assert.Empty(hb.Notes); // 225 = 3 × 75: no adjustment needed
            Assert.Equal(12, hb.Units.Count); // 2k pairs, k = 3
        }

        [Fact]
        public void ReportsAdjustments()
        {
            var hb = PatternLayout.Create(new PatternSpec { Kind = PatternKind.Herringbone, UnitLengthMm = 1200, UnitHeightMm = 190, JointMm = 0 });
            Assert.Contains(hb.Notes, n => n.Contains("200 mm")); // 1200 / 6
        }
    }

    public class PatternRendererTests
    {
        static byte[] Wood() // stripes with noise, like grain
        {
            using (var img = new Image<Rgb24>(400, 200))
            {
                var rnd = new Random(3);
                for (int y = 0; y < 200; y++)
                    for (int x = 0; x < 400; x++)
                    {
                        int v = 150 + (int)(30 * Math.Sin(y * 0.3)) + rnd.Next(-10, 10);
                        img[x, y] = new Rgb24((byte)v, (byte)(v * 0.75), (byte)(v * 0.45));
                    }
                using (var ms = new MemoryStream()) { img.Save(ms, new PngEncoder()); return ms.ToArray(); }
            }
        }

        static byte[] BrickPhoto() // red and purple patches on a white background
        {
            using (var img = new Image<Rgb24>(200, 100))
            {
                for (int y = 0; y < 100; y++)
                    for (int x = 0; x < 200; x++)
                        img[x, y] = x < 40 ? new Rgb24(255, 255, 255) : x < 120 ? new Rgb24(170, 60, 40) : new Rgb24(110, 50, 70);
                using (var ms = new MemoryStream()) { img.Save(ms, new PngEncoder()); return ms.ToArray(); }
            }
        }

        [Fact]
        public void RendersExactSizeSeamlessTextureFromProduct()
        {
            var layout = PatternLayout.Create(new PatternSpec { Kind = PatternKind.Herringbone, UnitLengthMm = 600, UnitHeightMm = 100, JointMm = 2 });
            var r = PatternRenderer.Render(layout, new PatternStyle { Fill = FillMode.Texture, MaxPixels = 512 },
                new PatternSource { ImageBytes = Wood(), ImageWidthMm = 2000 });

            Assert.Equal(layout.WidthMm * r.RepeatX, r.WidthMm);
            Assert.True(r.WidthMm >= 1200 && r.HeightMm >= 1200);
            Assert.True(ImageFormat.TryGetSize(r.Albedo, out var w, out var h));
            Assert.Equal((r.PixelWidth, r.PixelHeight), (w, h));
            Assert.Equal(ImageKind.Png, ImageFormat.Sniff(r.Normal));
            Assert.Equal(ImageKind.Png, ImageFormat.Sniff(r.Roughness));
            AssertSeamless(r.Albedo);
        }

        [Fact]
        public void PaletteModeUsesTheProductColoursNotTheBackground()
        {
            var colours = Palette.Extract(BrickPhoto(), k: 4);
            Assert.Equal(2, colours.Count);
            Assert.Contains(colours, c => c.R > 150 && c.G < 90);   // red
            Assert.DoesNotContain(colours, c => c.R > 230 && c.G > 230); // no white

            var layout = PatternLayout.Create(new PatternSpec { Kind = PatternKind.Flemish, UnitLengthMm = 215, UnitHeightMm = 65, UnitDepthMm = 102.5, JointMm = 10 });
            var r = PatternRenderer.Render(layout, new PatternStyle { Fill = FillMode.Palette, MaxPixels = 512, JointR = 220, JointG = 220, JointB = 215 },
                new PatternSource { ImageBytes = BrickPhoto() });
            AssertSeamless(r.Albedo);
        }

        [Fact]
        public void JointsAreRecessedInTheNormalMap()
        {
            // A 1-unit stack: flat face in the middle, joint step at the unit's edge.
            var layout = PatternLayout.Create(new PatternSpec { Kind = PatternKind.Stack, UnitLengthMm = 100, UnitHeightMm = 100, JointMm = 10 });
            var r = PatternRenderer.Render(layout, new PatternStyle { Fill = FillMode.Palette, MaxPixels = 220, MinTileMm = 110, JointDepthMm = 5 },
                new PatternSource { Palette = { new PaletteColor(150, 80, 60, 1) } });
            using (var n = Image.Load<Rgb24>(r.Normal))
            {
                var centre = n[n.Width / 2 - 5, n.Height / 2];
                Assert.InRange(centre.B, 250, 255);          // flat face points straight out
                var nearEdge = Enumerable.Range(0, n.Width).Select(x => n[x, n.Height / 2]).Min(p => p.B);
                Assert.True(nearEdge < 240);                  // the bevel/joint step tilts normals
            }
        }

        /// <summary>The wrap-around seam must look like any other pair of neighbouring columns/rows.</summary>
        static void AssertSeamless(byte[] image)
        {
            using (var img = Image.Load<Rgb24>(image))
            {
                double Diff(Rgb24 a, Rgb24 b) => (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3.0;
                double seam = 0, inner = 0;
                for (int y = 0; y < img.Height; y++)
                {
                    seam += Diff(img[0, y], img[img.Width - 1, y]);
                    inner += Diff(img[img.Width / 2, y], img[img.Width / 2 - 1, y]);
                }
                Assert.True(seam <= inner * 2 + img.Height * 3, $"seam {seam / img.Height:0.0} vs inner {inner / img.Height:0.0}");
            }
        }
    }
}
