using System;

namespace MaterialAgent.Core
{
    /// <summary>The real-world repeat size we will use, and how sure we are.</summary>
    public sealed class ScaleDecision
    {
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public ScaleSource Source { get; set; }
        public ScaleConfidence Confidence { get; set; }
        /// <summary>What the agent said, plus anything code changed.</summary>
        public string Rationale { get; set; }
    }

    /// <summary>
    /// Applies the evidence ladder (page text > measured image feature > category prior) in code,
    /// and keeps the repeat size consistent with the image's pixel proportions so textures never stretch.
    /// </summary>
    public static class ScaleLadder
    {
        /// <summary>Allowed mismatch between the size ratio and the pixel ratio before height is corrected.</summary>
        public const double AspectTolerance = 0.03;

        /// <param name="agent">The research step's scale (may be null).</param>
        /// <param name="visionFeature">A feature count from the vision step for the chosen image (may be null).</param>
        /// <param name="imageAspect">Chosen image height / width in pixels, or 0 if unknown.</param>
        public static ScaleDecision Decide(ScaleInfo agent, ScaleFeature visionFeature, double imageAspect)
        {
            EnumText.TryParseScaleSource(agent?.Source, out var agentSource);
            EnumText.TryParseConfidence(agent?.Confidence, out var agentConfidence);
            bool hasAgentSize = agent != null && agent.WidthMm > 0 && agent.HeightMm > 0;
            var rationale = agent?.Rationale?.Trim() ?? "";

            // 1. Explicit dimensions on the page.
            if (hasAgentSize && agentSource == ScaleSource.PageText)
                return Fit(agent.WidthMm, agent.HeightMm, ScaleSource.PageText, agentConfidence, rationale, imageAspect, false);

            // 2. A feature of known size, counted in the image; the multiplication happens here.
            var feature = visionFeature?.IsUsable == true ? visionFeature : agent?.Feature?.IsUsable == true ? agent.Feature : null;
            if (feature != null)
            {
                double span = feature.RealMm * feature.CountAcross;
                var note = $"{Fmt(feature.CountAcross)} × {feature.Name ?? "feature"} of {Fmt(feature.RealMm)} mm across the image {(feature.AlongHeight ? "height" : "width")} = {Fmt(span)} mm.";
                double w, h;
                bool anchorHeight = feature.AlongHeight;
                if (anchorHeight) { h = span; w = imageAspect > 0 ? span / imageAspect : (hasAgentSize ? agent.WidthMm : span); }
                else { w = span; h = imageAspect > 0 ? span * imageAspect : (hasAgentSize ? agent.HeightMm : span); }
                return Fit(w, h, ScaleSource.ImageFeature, ScaleConfidence.Medium, Join(note, rationale), imageAspect, anchorHeight);
            }

            // 3. Category prior.
            if (hasAgentSize)
            {
                var source = agentSource == ScaleSource.User ? ScaleSource.CategoryPrior : agentSource;
                var confidence = source == ScaleSource.ImageFeature ? ScaleConfidence.Medium : ScaleConfidence.Low;
                if (agentConfidence < confidence) confidence = agentConfidence;
                return Fit(agent.WidthMm, agent.HeightMm, source, confidence, rationale, imageAspect, false);
            }

            // Nothing usable: a neutral guess the user must correct.
            double fw = 1000, fh = imageAspect > 0 ? 1000 * imageAspect : 1000;
            return new ScaleDecision { WidthMm = fw, HeightMm = fh, Source = ScaleSource.CategoryPrior, Confidence = ScaleConfidence.Low, Rationale = Join("No size evidence found; placeholder 1 m repeat.", rationale) };
        }

        static ScaleDecision Fit(double w, double h, ScaleSource source, ScaleConfidence confidence, string rationale, double aspect, bool anchorHeight)
        {
            if (aspect > 0 && w > 0 && h > 0)
            {
                double ratio = h / w;
                if (Math.Abs(ratio - aspect) / aspect > AspectTolerance)
                {
                    // The stated size and the picture disagree. The image may be a crop of what was described,
                    // so keep one side and derive the other; drop confidence a notch.
                    if (anchorHeight) w = h / aspect; else h = w * aspect;
                    rationale = Join(rationale, $"{(anchorHeight ? "Width" : "Height")} adjusted to the image proportions.");
                    if (source == ScaleSource.PageText && confidence == ScaleConfidence.High) confidence = ScaleConfidence.Medium;
                }
            }
            return new ScaleDecision { WidthMm = Round(w), HeightMm = Round(h), Source = source, Confidence = confidence, Rationale = rationale };
        }

        /// <summary>Height for a width at the image's proportions (for the "lock aspect" control).</summary>
        public static double HeightForWidth(double widthMm, double imageAspect) => imageAspect > 0 ? Round(widthMm * imageAspect) : widthMm;
        public static double WidthForHeight(double heightMm, double imageAspect) => imageAspect > 0 ? Round(heightMm / imageAspect) : heightMm;

        static double Round(double v) => Math.Round(v, 1);
        static string Fmt(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        static string Join(string a, string b) => string.IsNullOrWhiteSpace(a) ? b ?? "" : string.IsNullOrWhiteSpace(b) ? a : a.TrimEnd() + " " + b.Trim();
    }
}
