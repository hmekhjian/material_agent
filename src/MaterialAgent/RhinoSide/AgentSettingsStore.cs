using MaterialAgent.Core.Agent;

namespace MaterialAgent.RhinoSide
{
    /// <summary>Agent settings persisted in Rhino's plug-in settings. The API key env var always wins.</summary>
    public static class AgentSettingsStore
    {
        const string KeyApiKey = "GeminiApiKey";
        const string KeyModel = "GeminiModel";

        static Rhino.PersistentSettings Settings => MaterialAgentPlugin.Instance?.Settings;

        public static string SavedApiKey => Settings?.GetString(KeyApiKey, "") ?? "";
        public static string SavedModel => Settings?.GetString(KeyModel, AgentSettings.DefaultModel) ?? AgentSettings.DefaultModel;

        public static AgentSettings Load() => new AgentSettings
        {
            ApiKey = AgentSettings.ResolveApiKey(SavedApiKey),
            Model = string.IsNullOrWhiteSpace(SavedModel) ? AgentSettings.DefaultModel : SavedModel.Trim(),
        };

        public static void Save(string apiKey, string model)
        {
            var s = Settings;
            if (s == null) return;
            if (apiKey != null) s.SetString(KeyApiKey, apiKey.Trim());
            s.SetString(KeyModel, string.IsNullOrWhiteSpace(model) ? AgentSettings.DefaultModel : model.Trim());
            MaterialAgentPlugin.Instance.SaveSettings();
        }
    }
}
