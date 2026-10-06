using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MaterialAgent.Core.Agent
{
    public sealed class GeneratedTexture
    {
        public CandidateImage Candidate { get; set; }
        /// <summary>Scale for the generated image: the area it was asked to depict, with lowered confidence.</summary>
        public ScaleDecision Scale { get; set; }
        public GeminiUsage Usage { get; set; } = new GeminiUsage();
    }

    /// <summary>
    /// When no seamless texture exists online, asks Gemini's image model ("Nano Banana") to paint a flat,
    /// tileable texture from whatever reference images were found, then forces seamless edges in code
    /// (<see cref="SeamlessTile"/>) because image models don't reliably wrap.
    /// </summary>
    public sealed class SeamlessTextureGenerator
    {
        /// <summary>Reference images sent to the model; more adds cost and rarely helps.</summary>
        const int MaxReferences = 3;
        const int MaxReferenceBytes = 4 * 1024 * 1024;

        static readonly (string ratio, double value)[] Ratios =
        {
            ("1:1", 1.0), ("4:3", 3.0 / 4), ("3:4", 4.0 / 3), ("3:2", 2.0 / 3), ("2:3", 3.0 / 2), ("16:9", 9.0 / 16), ("9:16", 16.0 / 9),
        };

        readonly GeminiClient _client;
        readonly AgentSettings _settings;

        public SeamlessTextureGenerator(AgentSettings settings, HttpClient http = null)
        {
            _settings = settings ?? new AgentSettings();
            var model = string.IsNullOrWhiteSpace(_settings.ImageModel) ? AgentSettings.DefaultImageModel : _settings.ImageModel;
            _client = new GeminiClient(http ?? GeminiMaterialResolver.SharedHttp, _settings.ApiKey, model);
        }

        /// <param name="references">Images to work from, best first (swatches, details, room shots).</param>
        /// <param name="widthMm">Real-world width the texture should depict.</param>
        /// <param name="heightMm">Real-world height the texture should depict.</param>
        /// <param name="basis">The scale the request is based on (its source/confidence are carried over, lowered).</param>
        public async Task<GeneratedTexture> GenerateAsync(ProductInfo product, string category, Finish? finish,
            IReadOnlyList<CandidateImage> references, double widthMm, double heightMm, ScaleDecision basis, CancellationToken ct)
        {
            var refs = (references ?? Array.Empty<CandidateImage>())
                .Where(r => r?.Image?.Bytes != null && r.Image.Bytes.Length <= MaxReferenceBytes)
                .Take(MaxReferences).ToList();
            if (refs.Count == 0) throw new InvalidOperationException("No reference image to generate from. Find a product or load an image first.");

            var (ratio, ratioValue) = ClosestRatio(heightMm / widthMm);
            // Keep the width the user set; the height follows the ratio the model can actually produce.
            double genHeightMm = Math.Round(widthMm * ratioValue, 1);

            var message = new GeminiMessage { Role = "user" };
            message.Parts.Add(GeminiPart.FromText(Prompt(product, category, finish, widthMm, genHeightMm, refs.Count)));
            for (int i = 0; i < refs.Count; i++)
            {
                message.Parts.Add(GeminiPart.FromText($"Reference {i + 1} ({refs[i].Kind ?? "photo"}):"));
                message.Parts.Add(GeminiPart.FromImage(refs[i].Image.Bytes, ImageFormat.MimeType(refs[i].Image.Kind)));
            }
            var request = new GeminiRequest
            {
                ResponseModalities = new List<string> { "TEXT", "IMAGE" },
                ImageAspectRatio = ratio,
                ImageSize = string.IsNullOrWhiteSpace(_settings.ImageSize) ? AgentSettings.DefaultImageSize : _settings.ImageSize,
            };
            request.Messages.Add(message);

            var response = await _client.GenerateAsync(request, ct).ConfigureAwait(false);
            var image = response.Images.FirstOrDefault();
            if (image == null)
                throw new InvalidOperationException("The image model returned no image" + (string.IsNullOrWhiteSpace(response.Text) ? "." : ": " + response.Text.Trim()));

            var seamless = await Task.Run(() => SeamlessTile.MakeSeamless(image.Data, 0.2), ct).ConfigureAwait(false);
            var source = $"ai-generated:{_client.Model}:{refs[0].Url}";
            var fetched = ImageFetcher.SaveGenerated(seamless, source);
            // Follow the pixels the model actually returned, not the ratio we asked for.
            if (fetched.Aspect > 0) genHeightMm = ScaleLadder.HeightForWidth(widthMm, fetched.Aspect);

            return new GeneratedTexture
            {
                Usage = response.Usage,
                Candidate = new CandidateImage
                {
                    Url = source,
                    Image = fetched,
                    Kind = "generated",
                    LikelyTileable = true,
                    MatchesProduct = true,
                    Note = $"AI-generated from {refs.Count} reference image(s); edges blended in code. Compare with the real product before use.",
                },
                Scale = new ScaleDecision
                {
                    WidthMm = Math.Round(widthMm, 1),
                    HeightMm = genHeightMm,
                    Source = basis?.Source ?? ScaleSource.User,
                    Confidence = Lower(basis?.Confidence ?? ScaleConfidence.Medium),
                    Rationale = $"Generated texture asked to show {widthMm:0.#} × {genHeightMm:0.#} mm; image models only roughly follow scale, so check it (e.g. Measure in viewport)."
                        + (string.IsNullOrWhiteSpace(basis?.Rationale) ? "" : " Based on: " + basis.Rationale),
                },
            };
        }

        public static string Prompt(ProductInfo product, string category, Finish? finish, double widthMm, double heightMm, int referenceCount)
        {
            var what = string.Join(" ", new[] { product?.Manufacturer, product?.Name, string.IsNullOrEmpty(product?.Code) ? null : "(" + product.Code + ")" }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(what)) what = "the material in the reference";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return
$@"Create a seamless, tileable texture map of {what}{(string.IsNullOrWhiteSpace(category) ? "" : ", a " + category)}, for use as a repeating material in architectural 3D rendering.
Match the colour, pattern, grain and surface character of the {(referenceCount > 1 ? "reference photos" : "reference photo")} as closely as possible. The references may be room shots, angled or cropped: extract only the material surface.
Requirements:
- Flat, straight-on orthographic view filling the whole frame. No perspective, no objects, furniture, edges of the product, labels, text, logos or watermarks.
- Even, neutral, shadowless lighting; no highlights, vignetting or reflections baked in.
- The image shows a real-world area of {widthMm.ToString("0", inv)} mm wide by {heightMm.ToString("0", inv)} mm high, so features (planks, tiles, bricks, grain, pattern repeat) appear at their true size and count for that area.
- Left edge continues into the right edge and top into bottom, with no visible seam and no obvious repeated motifs.{(finish.HasValue ? "\n- Surface finish: " + EnumText.ToWire(finish.Value) + "." : "")}
Return only the image.";
        }

        public static (string ratio, double value) ClosestRatio(double heightOverWidth)
        {
            if (!(heightOverWidth > 0)) return Ratios[0];
            return Ratios.OrderBy(r => Math.Abs(Math.Log(r.value / heightOverWidth))).First();
        }

        static ScaleConfidence Lower(ScaleConfidence c) => c == ScaleConfidence.High ? ScaleConfidence.Medium : ScaleConfidence.Low;
    }
}
