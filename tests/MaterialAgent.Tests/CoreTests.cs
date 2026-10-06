using System;
using MaterialAgent.Core;
using Xunit;

namespace MaterialAgent.Tests
{
    public class MappingMathTests
    {
        [Fact]
        public void ConvertsMillimetresToModelUnits()
        {
            Assert.Equal(600, MappingMath.MmToModel(600, 1.0));
            Assert.Equal(0.6, MappingMath.MmToModel(600, 0.001), 9);
            Assert.Throws<ArgumentOutOfRangeException>(() => MappingMath.MmToModel(0, 1.0));
        }

        [Theory]
        [InlineData(GrainAxis.None, 100, 2000, false)]
        [InlineData(GrainAxis.Horizontal, 2000, 100, false)] // grain along U, long side already along X
        [InlineData(GrainAxis.Horizontal, 100, 2000, true)]  // long side along Y: turn U onto Y
        [InlineData(GrainAxis.Vertical, 100, 2000, false)]   // grain along V already follows Y
        [InlineData(GrainAxis.Vertical, 2000, 100, true)]
        [InlineData(GrainAxis.Horizontal, 1000, 1010, false)] // near-square: leave alone
        public void GrainRotation(GrainAxis grain, double x, double y, bool rotate)
        {
            Assert.Equal(rotate, MappingMath.ShouldRotateForGrain(grain, x, y));
        }
    }

    public class ImageFormatTests
    {
        [Fact]
        public void SniffsCommonFormats()
        {
            Assert.Equal(ImageKind.Png, ImageFormat.Sniff(Pad(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)));
            Assert.Equal(ImageKind.Jpeg, ImageFormat.Sniff(Pad(0xFF, 0xD8, 0xFF, 0xE0)));
            Assert.Equal(ImageKind.Webp, ImageFormat.Sniff(Pad((byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P')));
            Assert.Equal(ImageKind.Unknown, ImageFormat.Sniff(System.Text.Encoding.ASCII.GetBytes("<!DOCTYPE html><html>")));
            Assert.False(ImageFormat.IsSupportedTexture(ImageKind.Webp)); // converted before use, see WebpTests
        }

        [Fact]
        public void DownloadFileNamesAreStableAndSafe()
        {
            var a = ImageFetcher.FileNameFor("https://example.com/decors/H1145 ST10.jpg?w=2000", ImageKind.Jpeg);
            var b = ImageFetcher.FileNameFor("https://example.com/decors/H1145 ST10.jpg?w=2000", ImageKind.Jpeg);
            var c = ImageFetcher.FileNameFor("https://example.com/decors/H1145 ST10.jpg?w=1000", ImageKind.Jpeg);
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.StartsWith("H1145_ST10_", a);
            Assert.EndsWith(".jpg", a);
        }

        static byte[] Pad(params byte[] head)
        {
            var data = new byte[32];
            Array.Copy(head, data, head.Length);
            return data;
        }
    }
}
