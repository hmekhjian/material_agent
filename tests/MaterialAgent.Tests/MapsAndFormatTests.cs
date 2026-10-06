using System.Linq;
using MaterialAgent.Core;
using MaterialAgent.Core.Maps;
using Xunit;

namespace MaterialAgent.Tests
{
    public class SurfaceMapsTests
    {
        [Fact]
        public void FlatImageGivesFlatNormalsAndBaseRoughness()
        {
            var lum = Enumerable.Repeat(0.5f, 16 * 8).ToArray();
            var maps = SurfaceMaps.Generate(lum, 16, 8, 0.7);
            for (int i = 0; i < 16 * 8; i++)
            {
                Assert.Equal(128, maps.NormalRgb[i * 3]);
                Assert.Equal(128, maps.NormalRgb[i * 3 + 1]);
                Assert.Equal(255, maps.NormalRgb[i * 3 + 2]);
                Assert.Equal(179, maps.Roughness[i]); // 0.7 * 255
            }
        }

        [Fact]
        public void RampTiltsNormalsAndDarkAreasAreRougher()
        {
            // Brightness rising to the right with a seam at the wrap: interior normals tilt towards -X.
            int w = 32, h = 4;
            var lum = new float[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) lum[y * w + x] = x / (float)(w - 1);
            var maps = SurfaceMaps.Generate(lum, w, h, 0.5);
            int mid = (1 * w + 16) * 3;
            Assert.True(maps.NormalRgb[mid] < 128);
            Assert.Equal(128, maps.NormalRgb[mid + 1]);
            Assert.True(maps.Roughness[1 * w + 2] > maps.Roughness[1 * w + 29]);
        }

        [Fact]
        public void FitWithinKeepsProportions()
        {
            Assert.Equal((1024, 512), SurfaceMaps.FitWithin(4000, 2000, 1024));
            Assert.Equal((300, 200), SurfaceMaps.FitWithin(300, 200, 1024));
        }
    }

    public class ImageSizeTests
    {
        [Fact]
        public void ReadsPngSize()
        {
            Assert.True(ImageFormat.TryGetSize(TestImages.Png(123, 45), out var w, out var h));
            Assert.Equal((123, 45), (w, h));
        }

        [Fact]
        public void ReadsJpegSize()
        {
            // SOI, APP0 (len 16), SOF0 with height 300 width 500.
            var jpeg = new byte[] {
                0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x2C, 0x01, 0xF4, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 };
            Assert.True(ImageFormat.TryGetSize(jpeg, out var w, out var h));
            Assert.Equal((500, 300), (w, h));
        }
    }

    public class QueryMatchTests
    {
        [Theory]
        [InlineData("Egger H1145 ST10", true)]
        [InlineData("egger h1145-st10 oak", true)]
        [InlineData("Egger H1146 ST10", false)]
        [InlineData("Natural Halifax Oak", true)]
        public void MatchesFreeTextQuery(string query, bool expected)
        {
            var p = new Provenance { ProductCode = "H1145 ST10", ProductName = "Natural Halifax Oak" };
            Assert.Equal(expected, p.MatchesQuery(query));
        }
    }
}
