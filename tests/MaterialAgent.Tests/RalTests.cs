using System.Linq;
using MaterialAgent.Core;
using MaterialAgent.Core.Colors;
using Xunit;

namespace MaterialAgent.Tests
{
    public class RalTests
    {
        [Fact]
        public void CatalogueIsComplete()
        {
            Assert.Equal(215, RalCatalog.Classic.Count);
            Assert.Equal(1825, RalCatalog.Design.Count);
            Assert.Equal(RalCatalog.Classic.Count, RalCatalog.Classic.Select(c => c.Code).Distinct().Count());
            Assert.Equal(RalCatalog.Design.Count, RalCatalog.Design.Select(c => c.Code).Distinct().Count());
            Assert.All(RalCatalog.Classic, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
        }

        [Theory]
        [InlineData("RAL 9010", "RAL 9010", "Pure white", "#F1ECE1")]
        [InlineData("ral9010", "RAL 9010", "Pure white", "#F1ECE1")]
        [InlineData("RAL-7016 anthracite", "RAL 7016", "Anthracite grey", "#383E42")]
        [InlineData("  RAL classic 9005 ", "RAL 9005", "Jet black", null)]
        [InlineData("RAL 6037", "RAL 6037", "Pure green", "#008C27")] // deep green, not the neon some tables list
        public void MatchesClassicCodes(string query, string code, string name, string hex)
        {
            Assert.True(RalCatalog.TryMatch(query, out var m));
            Assert.Equal(code, m.Color.Code);
            Assert.Equal(name, m.Color.Name);
            if (hex != null) Assert.Equal(hex, m.Color.Hex);
            Assert.Equal(RalSystem.Classic, m.Color.System);
        }

        [Theory]
        [InlineData("RAL 000 15 00", "RAL 000 15 00", "Ink Black")]
        [InlineData("RAL 0001500", "RAL 000 15 00", "Ink Black")]
        [InlineData("H000L15C00", "RAL 000 15 00", "Ink Black")]
        [InlineData("RAL design 360 93 05", "RAL 360 93 05", "Arrowhead White")]
        public void MatchesDesignCodes(string query, string code, string name)
        {
            Assert.True(RalCatalog.TryMatch(query, out var m));
            Assert.Equal(code, m.Color.Code);
            Assert.Equal(name, m.Color.Name);
            Assert.Equal(RalSystem.Design, m.Color.System);
        }

        [Fact]
        public void MatchesByNameWithAlternatives()
        {
            Assert.True(RalCatalog.TryMatch("RAL pure white", out var exact));
            Assert.Equal("RAL 9010", exact.Color.Code);

            Assert.True(RalCatalog.TryMatch("RAL grey", out var many));
            Assert.Equal(RalSystem.Classic, many.Color.System); // Classic before Design
            Assert.True(many.Alternatives.Count > 3);
        }

        [Theory]
        [InlineData("Egger H1145 ST10")]
        [InlineData("9010")]          // bare numbers stay product searches
        [InlineData("RAL 9999")]       // not a RAL Classic colour
        [InlineData("RAL")]
        [InlineData("Ralston oak")]
        [InlineData("")]
        public void LeavesOtherQueriesToTheAgent(string query)
        {
            Assert.False(RalCatalog.TryMatch(query, out _));
        }

        [Fact]
        public void FlagsSpecialFinishes()
        {
            RalCatalog.TryMatch("RAL 9006", out var alu);
            Assert.Equal(RalSpecial.Metallic, alu.Color.Special);
            RalCatalog.TryMatch("RAL 1035", out var pearl);
            Assert.Equal(RalSpecial.Metallic, pearl.Color.Special);
            RalCatalog.TryMatch("RAL 2005", out var lum);
            Assert.Equal(RalSpecial.Luminous, lum.Color.Special);
            RalCatalog.TryMatch("RAL 9010", out var plain);
            Assert.Equal(RalSpecial.None, plain.Color.Special);
        }

        [Fact]
        public void ColourProvenanceRoundTripsAndIsFoundAgain()
        {
            var p = new Provenance { ProductCode = "RAL 9010", ProductName = "Pure white", Manufacturer = "RAL", ColorHex = "#F1ECE1", Category = "RAL Classic colour" };
            var back = Provenance.FromNotes(p.ToNotes());
            Assert.True(back.IsSolidColor);
            Assert.Equal("#F1ECE1", back.ColorHex);
            Assert.True(back.Matches("RAL 9010", null));
            Assert.True(back.MatchesQuery("ral 9010"));
            Assert.False(new Provenance { ProductCode = "X" }.IsSolidColor);
        }
    }
}
