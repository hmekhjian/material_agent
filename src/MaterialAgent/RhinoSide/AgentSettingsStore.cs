using MaterialAgent.Core.Agent;

namespace MaterialAgent.RhinoSide
{
    /// <summary>
    /// Agent settings persisted in Rhino's plug-in settings (entered in the panel's Settings tab).
    /// The environment variable is only used when no key has been saved.
    /// </summary>
    public static class AgentSettingsStore
    {
        const string KeyApiKey = "GeminiApiKey";
        const string KeyModel = "GeminiModel";
        const string KeyEnscape = "CreateEnscapeMaterial";
        const string KeyImageModel = "GeminiImageModel";
        const string KeyImageSize = "GeminiImageSize";
        const string KeyAutoGenerate = "AutoGenerateSeamless";
        const string KeyEnscapeTypeId = "EnscapeMaterialTypeId";

        static Rhino.PersistentSettings Settings => MaterialAgentPlugin.Instance?.Settings;

        public static string SavedApiKey => Settings?.GetString(KeyApiKey, "") ?? "";
        public static string SavedModel => Settings?.GetString(KeyModel, AgentSettings.DefaultModel) ?? AgentSettings.DefaultModel;

        public static string ImageModel
        {
            get => Settings?.GetString(KeyImageModel, AgentSettings.DefaultImageModel) ?? AgentSettings.DefaultImageModel;
            set { var s = Settings; if (s == null) return; s.SetString(KeyImageModel, string.IsNullOrWhiteSpace(value) ? AgentSettings.DefaultImageModel : value.Trim()); MaterialAgentPlugin.Instance.SaveSettings(); }
        }

        public static string ImageSize
        {
            get => Settings?.GetString(KeyImageSize, AgentSettings.DefaultImageSize) ?? AgentSettings.DefaultImageSize;
            set { var s = Settings; if (s == null) return; s.SetString(KeyImageSize, string.IsNullOrWhiteSpace(value) ? AgentSettings.DefaultImageSize : value.Trim()); MaterialAgentPlugin.Instance.SaveSettings(); }
        }

        public static bool AutoGenerateSeamless
        {
            get => Settings?.GetBool(KeyAutoGenerate, false) ?? false;
            set { var s = Settings; if (s == null) return; s.SetBool(KeyAutoGenerate, value); MaterialAgentPlugin.Instance.SaveSettings(); }
        }

        public static bool CreateEnscape
        {
            get => Settings?.GetBool(KeyEnscape, false) ?? false;
            set { var s = Settings; if (s == null) return; s.SetBool(KeyEnscape, value); MaterialAgentPlugin.Instance.SaveSettings(); }
        }

        /// <summary>Manual Enscape material type ID, for when detection fails. Empty = detect.</summary>
        public static string EnscapeTypeId
        {
            get => Settings?.GetString(KeyEnscapeTypeId, "") ?? "";
            set
            {
                var s = Settings; if (s == null) return;
                s.SetString(KeyEnscapeTypeId, value?.Trim() ?? "");
                MaterialAgentPlugin.Instance.SaveSettings();
                ApplyEnscapeOverride();
            }
        }

        public static void ApplyEnscapeOverride()
        {
            EnscapeSupport.OverrideTypeId = System.Guid.TryParse(EnscapeTypeId, out var g) ? g : System.Guid.Empty;
        }

        public static AgentSettings Load() => new AgentSettings
        {
            ApiKey = AgentSettings.ResolveApiKey(SavedApiKey),
            Model = string.IsNullOrWhiteSpace(SavedModel) ? AgentSettings.DefaultModel : SavedModel.Trim(),
            ImageModel = string.IsNullOrWhiteSpace(ImageModel) ? AgentSettings.DefaultImageModel : ImageModel.Trim(),
            ImageSize = ImageSize,
            AutoGenerateSeamless = AutoGenerateSeamless,
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
