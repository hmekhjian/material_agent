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

        public const string EnvApiKey = "GEMINI_API_KEY";
        public const string EnvApiKeyAlt = "GOOGLE_API_KEY";

        public string ApiKey { get; set; }
        public string Model { get; set; } = DefaultModel;
        /// <summary>Gemini thinking level (minimal|low|medium|high), or empty to use the model default.</summary>
        public string ThinkingLevel { get; set; } = "low";
        /// <summary>How many candidate images to download and show the vision model.</summary>
        public int MaxCandidates { get; set; } = 6;

        /// <summary>Environment variable first (never stored), then the saved setting.</summary>
        public static string ResolveApiKey(string saved)
        {
            var env = Environment.GetEnvironmentVariable(EnvApiKey);
            if (string.IsNullOrWhiteSpace(env)) env = Environment.GetEnvironmentVariable(EnvApiKeyAlt);
            return !string.IsNullOrWhiteSpace(env) ? env.Trim() : saved?.Trim();
        }

        public static bool ApiKeyFromEnvironment =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvApiKey)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvApiKeyAlt));
    }
}
