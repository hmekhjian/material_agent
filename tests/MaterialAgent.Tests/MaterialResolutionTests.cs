using MaterialAgent.Core;
using Xunit;

namespace MaterialAgent.Tests
{
    public class MaterialResolutionTests
    {
        const string Valid = @"{
          ""product"": { ""name"": ""Natural Halifax Oak"", ""code"": ""H1145 ST10"", ""manufacturer"": ""Egger"", ""page_url"": ""https://www.egger.com/h1145"" },
          ""candidates"": [ { ""url"": ""https://example.com/h1145.jpg"", ""kind"": ""swatch"", ""likely_tileable"": true, ""note"": ""decor download"" } ],
          ""scale"": { ""width_mm"": 2800, ""height_mm"": 2070, ""source"": ""page_text"", ""confidence"": ""high"", ""rationale"": ""board size stated"" },
          ""grain_axis"": ""horizontal"",
          ""mapping"": ""box"",
          ""category"": ""wood decor"",
        }";

        [Fact]
        public void ParsesAndValidates()
        {
            var r = MaterialResolution.FromJson(Valid);
            Assert.Empty(r.Validate());
            Assert.Equal("H1145 ST10", r.Product.Code);
            Assert.Equal(2800, r.Scale.WidthMm);
            Assert.True(r.Candidates[0].LikelyTileable);
        }

        [Fact]
        public void RationaleIsOptional()
        {
            var r = MaterialResolution.FromJson(Valid.Replace(@"""rationale"": ""board size stated""", @"""rationale"": null"));
            Assert.Empty(r.Validate());
        }

        [Fact]
        public void ReportsMissingRequiredFields()
        {
            var r = MaterialResolution.FromJson(@"{ ""product"": { ""name"": """" }, ""candidates"": [], ""scale"": { ""width_mm"": 0, ""source"": ""guess"" }, ""mapping"": ""cylinder"" }");
            var errors = r.Validate();
            Assert.Contains(errors, e => e.Contains("product.name"));
            Assert.Contains(errors, e => e.Contains("product.page_url"));
            Assert.Contains(errors, e => e.Contains("candidates"));
            Assert.Contains(errors, e => e.Contains("width_mm"));
            Assert.Contains(errors, e => e.Contains("scale.source"));
            Assert.Contains(errors, e => e.Contains("confidence"));
            Assert.Contains(errors, e => e.Contains("mapping"));
            Assert.Contains(errors, e => e.Contains("category"));
        }

        [Fact]
        public void RejectsUserAsAgentScaleSource()
        {
            var r = MaterialResolution.FromJson(Valid.Replace(@"""source"": ""page_text""", @"""source"": ""user"""));
            Assert.Contains(r.Validate(), e => e.Contains("scale.source"));
        }

        [Fact]
        public void RejectsNonHttpCandidate()
        {
            var r = MaterialResolution.FromJson(Valid.Replace("https://example.com/h1145.jpg", "file:///c:/x.jpg"));
            Assert.Contains(r.Validate(), e => e.Contains("candidates[0].url"));
        }
    }
}
