using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaterialAgent.Core
{
    /// <summary>
    /// What the agent returns for one product (see CLAUDE.md, "Agent output schema").
    /// Enum-like fields stay strings on the wire; <see cref="Validate"/> checks them.
    /// </summary>
    public sealed class MaterialResolution
    {
        [JsonPropertyName("product")] public ProductInfo Product { get; set; }
        [JsonPropertyName("candidates")] public List<ImageCandidate> Candidates { get; set; }
        [JsonPropertyName("scale")] public ScaleInfo Scale { get; set; }
        [JsonPropertyName("grain_axis")] public string GrainAxis { get; set; }
        [JsonPropertyName("mapping")] public string Mapping { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; }

        static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };

        public static MaterialResolution FromJson(string json) =>
            JsonSerializer.Deserialize<MaterialResolution>(json, Options);

        public string ToJson() => JsonSerializer.Serialize(this, Options);

        /// <summary>Returns a list of problems; empty means the resolution is usable.</summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();

            if (Product == null) errors.Add("product is missing");
            else
            {
                if (string.IsNullOrWhiteSpace(Product.Name)) errors.Add("product.name is required");
                if (!IsHttpUrl(Product.PageUrl)) errors.Add("product.page_url must be an http(s) URL");
            }

            if (Candidates == null || Candidates.Count == 0) errors.Add("candidates must contain at least one image");
            else
            {
                for (int i = 0; i < Candidates.Count; i++)
                {
                    var c = Candidates[i];
                    if (c == null || !IsHttpUrl(c.Url)) errors.Add($"candidates[{i}].url must be an http(s) URL");
                    else if (!string.IsNullOrEmpty(c.Kind) && c.Kind != "swatch" && c.Kind != "room" && c.Kind != "detail")
                        errors.Add($"candidates[{i}].kind '{c.Kind}' is not swatch|room|detail");
                }
            }

            if (Scale == null) errors.Add("scale is missing");
            else
            {
                if (!(Scale.WidthMm > 0)) errors.Add("scale.width_mm must be > 0");
                if (!(Scale.HeightMm > 0)) errors.Add("scale.height_mm must be > 0");
                if (!EnumText.TryParseScaleSource(Scale.Source, out var src) || src == ScaleSource.User)
                    errors.Add("scale.source must be page_text|image_feature|category_prior");
                if (!EnumText.TryParseConfidence(Scale.Confidence, out _))
                    errors.Add("scale.confidence must be high|medium|low");
            }

            if (!EnumText.TryParseMapping(Mapping, out _)) errors.Add("mapping must be planar|box|per_face");
            if (GrainAxis != null && !EnumText.TryParseGrain(GrainAxis, out _)) errors.Add("grain_axis must be horizontal|vertical|none");
            if (string.IsNullOrWhiteSpace(Category)) errors.Add("category is required");

            return errors;
        }

        public static bool IsHttpUrl(string s) =>
            Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
    }

    public sealed class ProductInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("code")] public string Code { get; set; }
        [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; }
        [JsonPropertyName("page_url")] public string PageUrl { get; set; }
    }

    public sealed class ImageCandidate
    {
        [JsonPropertyName("url")] public string Url { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("likely_tileable")] public bool LikelyTileable { get; set; }
        [JsonPropertyName("note")] public string Note { get; set; }
    }

    public sealed class ScaleInfo
    {
        [JsonPropertyName("width_mm")] public double WidthMm { get; set; }
        [JsonPropertyName("height_mm")] public double HeightMm { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; }
        [JsonPropertyName("confidence")] public string Confidence { get; set; }
        [JsonPropertyName("rationale")] public string Rationale { get; set; }
    }
}
