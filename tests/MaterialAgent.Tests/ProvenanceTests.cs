using System;
using System.Linq;
using MaterialAgent.Core;
using Xunit;

namespace MaterialAgent.Tests
{
    public class ProvenanceTests
    {
        static Provenance Sample() => new Provenance
        {
            ProductCode = "H1145 ST10",
            ProductName = "Natural Halifax Oak",
            Manufacturer = "Egger",
            PageUrl = "https://www.egger.com/h1145",
            ImageUrl = "https://example.com/h1145.jpg",
            FetchDateUtc = new DateTime(2026, 10, 6, 12, 30, 0, DateTimeKind.Utc),
            ScaleSource = ScaleSource.PageText,
            ScaleConfidence = ScaleConfidence.High,
            WidthMm = 2800,
            HeightMm = 2070.5,
            Mapping = MappingKind.Planar,
            Grain = GrainAxis.Horizontal,
        };

        [Fact]
        public void NotesRoundTrip()
        {
            var p = Provenance.FromNotes("Some user notes\n" + Sample().ToNotes());
            Assert.NotNull(p);
            Assert.Equal("H1145 ST10", p.ProductCode);
            Assert.Equal("Egger", p.Manufacturer);
            Assert.Equal(new DateTime(2026, 10, 6, 12, 30, 0, DateTimeKind.Utc), p.FetchDateUtc);
            Assert.Equal(ScaleSource.PageText, p.ScaleSource);
            Assert.Equal(ScaleConfidence.High, p.ScaleConfidence);
            Assert.Equal(2070.5, p.HeightMm);
            Assert.Equal(MappingKind.Planar, p.Mapping);
            Assert.Equal(GrainAxis.Horizontal, p.Grain);
        }

        [Fact]
        public void UserStringPairsRoundTrip()
        {
            var dict = Sample().ToPairs().ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.All(dict.Keys, k => Assert.StartsWith(Provenance.Prefix, k));
            var p = Provenance.FromLookup(k => dict.TryGetValue(k, out var v) ? v : null);
            Assert.Equal("https://example.com/h1145.jpg", p.ImageUrl);
            Assert.Equal(2800, p.WidthMm);
        }

        [Fact]
        public void NotesWithoutHeaderAreIgnored()
        {
            Assert.Null(Provenance.FromNotes("matagent.product_code=X"));
            Assert.Null(Provenance.FromNotes(null));
            Assert.Null(Provenance.FromLookup(_ => null));
        }

        [Theory]
        [InlineData("h1145st10", null, true)]
        [InlineData("H1145-ST10", null, true)]
        [InlineData("H1146 ST10", null, false)]
        [InlineData(null, "https://EXAMPLE.com/h1145.jpg", true)]
        [InlineData(null, "https://example.com/other.jpg", false)]
        [InlineData(null, null, false)]
        public void Matches(string code, string url, bool expected)
        {
            Assert.Equal(expected, Sample().Matches(code, url));
        }
    }
}
