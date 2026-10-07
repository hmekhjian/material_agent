using System;

namespace MaterialAgent.Core.Agent
{
    public sealed class AgentSettings
    {
        /// <summary>
        /// Google's alias for the current Gemini Flash model. Cheap, multimodal, and has built-in
        /// Google Search grounding and URL fetching. Use "gemini-flash-lite-latest" to save more.
        /// </summary>
        public const string DefaultModel = "gemini-flash-latest";

        /// <summary>"Nano Banana 2". Used only for the optional seamless-texture generation.</summary>
        public const string DefaultImageModel = "gemini-3.1-flash-image";
        public const string DefaultImageSize = "1K";

        /// <summary>
        /// Model for the page-finding (search-only) step: a simple task, so the faster, cheaper Flash-Lite.
        /// Falls back to <see cref="Model"/> if it fails.
        /// </summary>
        public const string DefaultLocateModel = "gemini-flash-lite-latest";

        public const string EnvApiKey = "GEMINI_API_KEY";
        public const string EnvApiKeyAlt = "GOOGLE_API_KEY";

        public string ApiKey { get; set; }
        public string Model { get; set; } = DefaultModel;
        public string LocateModel { get; set; } = DefaultLocateModel;
        /// <summary>Gemini thinking level (minimal|low|medium|high), or empty to use the model default.</summary>
        public string ThinkingLevel { get; set; } = "low";
        /// <summary>How many candidate images to download and show the vision model.</summary>
        public int MaxCandidates { get; set; } = 6;
        public string ImageModel { get; set; } = DefaultImageModel;
        /// <summary>"1K" or "2K" (higher costs more).</summary>
        public string ImageSize { get; set; } = DefaultImageSize;
        /// <summary>Generate a seamless texture automatically when the search finds no tileable image. Costs per image.</summary>
        public bool AutoGenerateSeamless { get; set; }

        /// <summary>The key typed into the panel's Settings tab wins; the environment variable is only a fallback.</summary>
        public static string ResolveApiKey(string saved)
        {
            if (!string.IsNullOrWhiteSpace(saved)) return saved.Trim();
            var env = EnvironmentKey;
            return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
        }

        public static string EnvironmentKey
        {
            get
            {
                var env = Environment.GetEnvironmentVariable(EnvApiKey);
                return string.IsNullOrWhiteSpace(env) ? Environment.GetEnvironmentVariable(EnvApiKeyAlt) : env;
            }
        }

        /// <summary>Shows only the start and end of a key, for status text.</summary>
        public static string Mask(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            key = key.Trim();
            return key.Length <= 8 ? new string('•', key.Length) : key.Substring(0, 4) + "…" + key.Substring(key.Length - 4);
        }
    }
}
