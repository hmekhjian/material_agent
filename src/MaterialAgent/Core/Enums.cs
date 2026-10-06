namespace MaterialAgent.Core
{
    /// <summary>How the texture is projected onto the object.</summary>
    public enum MappingKind
    {
        Box,
        Planar,
        /// <summary>Per-face projection. Not implemented separately yet: applied as capped box mapping.</summary>
        PerFace,
    }

    /// <summary>Direction the grain or pattern runs in the texture image.</summary>
    public enum GrainAxis
    {
        None,
        /// <summary>Grain runs along the image's width (texture U).</summary>
        Horizontal,
        /// <summary>Grain runs along the image's height (texture V).</summary>
        Vertical,
    }

    /// <summary>Where the real-world scale came from (the evidence ladder).</summary>
    public enum ScaleSource
    {
        PageText,
        ImageFeature,
        CategoryPrior,
        /// <summary>Typed or corrected by the user in the preview.</summary>
        User,
    }

    public enum ScaleConfidence
    {
        Low,
        Medium,
        High,
    }

    /// <summary>Surface finish. Drives the base roughness of the PBR material.</summary>
    public enum Finish
    {
        Matt,
        Satin,
        Gloss,
        Polished,
        Textured,
    }

    public static class EnumText
    {
        public static string ToWire(Finish f) => f.ToString().ToLowerInvariant();

        public static bool TryParseFinish(string s, out Finish value)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "matt": case "matte": value = Finish.Matt; return true;
                case "satin": case "silk": value = Finish.Satin; return true;
                case "gloss": case "glossy": case "high gloss": value = Finish.Gloss; return true;
                case "polished": value = Finish.Polished; return true;
                case "textured": case "structured": value = Finish.Textured; return true;
                default: value = Finish.Matt; return false;
            }
        }

        /// <summary>Base PBR roughness for a finish (0 = mirror, 1 = fully diffuse).</summary>
        public static double Roughness(Finish f) => f switch
        {
            Finish.Polished => 0.08,
            Finish.Gloss => 0.2,
            Finish.Satin => 0.45,
            Finish.Textured => 0.8,
            _ => 0.7,
        };

        public static string ToWire(MappingKind m) => m switch
        {
            MappingKind.Planar => "planar",
            MappingKind.PerFace => "per_face",
            _ => "box",
        };

        public static string ToWire(GrainAxis g) => g switch
        {
            GrainAxis.Horizontal => "horizontal",
            GrainAxis.Vertical => "vertical",
            _ => "none",
        };

        public static string ToWire(ScaleSource s) => s switch
        {
            ScaleSource.PageText => "page_text",
            ScaleSource.ImageFeature => "image_feature",
            ScaleSource.CategoryPrior => "category_prior",
            _ => "user",
        };

        public static string ToWire(ScaleConfidence c) => c switch
        {
            ScaleConfidence.High => "high",
            ScaleConfidence.Medium => "medium",
            _ => "low",
        };

        public static bool TryParseMapping(string s, out MappingKind value)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "box": value = MappingKind.Box; return true;
                case "planar": value = MappingKind.Planar; return true;
                case "per_face": value = MappingKind.PerFace; return true;
                default: value = MappingKind.Box; return false;
            }
        }

        public static bool TryParseGrain(string s, out GrainAxis value)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "horizontal": value = GrainAxis.Horizontal; return true;
                case "vertical": value = GrainAxis.Vertical; return true;
                case "none": value = GrainAxis.None; return true;
                default: value = GrainAxis.None; return false;
            }
        }

        public static bool TryParseScaleSource(string s, out ScaleSource value)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "page_text": value = ScaleSource.PageText; return true;
                case "image_feature": value = ScaleSource.ImageFeature; return true;
                case "category_prior": value = ScaleSource.CategoryPrior; return true;
                case "user": value = ScaleSource.User; return true;
                default: value = ScaleSource.User; return false;
            }
        }

        public static bool TryParseConfidence(string s, out ScaleConfidence value)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "high": value = ScaleConfidence.High; return true;
                case "medium": value = ScaleConfidence.Medium; return true;
                case "low": value = ScaleConfidence.Low; return true;
                default: value = ScaleConfidence.Low; return false;
            }
        }
    }
}
