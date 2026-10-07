using System;
using System.IO;
using System.Runtime.InteropServices;
using MaterialAgent.RhinoSide;
using MaterialAgent.UI;
using Rhino;
using Rhino.PlugIns;

namespace MaterialAgent
{
    public sealed class MaterialAgentPlugin : PlugIn
    {
        public MaterialAgentPlugin()
        {
            Instance = this;
            Log("Plug-in object created.");
        }

        public static MaterialAgentPlugin Instance { get; private set; }

        // Load at start-up so the panel is registered and restores with the user's layout.
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        /// <summary>Start-up diagnostics: %LOCALAPPDATA%\MaterialAgent\load.log.</summary>
        public static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaterialAgent", "load.log");

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            Log($"Loading Material Agent {typeof(MaterialAgentPlugin).Assembly.GetName().Version} from {typeof(MaterialAgentPlugin).Assembly.Location}");
            Log($"Rhino {RhinoApp.Version}, runtime {RuntimeInformation.FrameworkDescription}, OS {RuntimeInformation.OSDescription}");

            // Each step is guarded so one failure doesn't hide the cause or take the rest down.
            try
            {
                RegisterPanel();
            }
            catch (Exception ex)
            {
                Log("Panel registration failed: " + ex);
                errorMessage = "Material Agent could not register its panel:\n\n" + ex.GetType().Name + ": " + ex.Message +
                               "\n\nFull details: " + LogPath;
                return LoadReturnCode.ErrorShowDialog;
            }

            try
            {
                LayerAutoMapper.Enable();
            }
            catch (Exception ex)
            {
                // Not essential: layer auto-mapping is a convenience.
                Log("Layer auto-mapping disabled: " + ex);
                RhinoApp.WriteLine("Material Agent: layer auto-mapping disabled (" + ex.Message + "). See " + LogPath);
            }

            Log("Loaded OK.");
            return LoadReturnCode.Success;
        }

        void RegisterPanel()
        {
            Rhino.UI.Panels.RegisterPanel(
                this,
                typeof(MaterialAgentPanel),
                "Material Agent",
                typeof(MaterialAgentPlugin).Assembly,
                "MaterialAgent.Resources.MaterialAgent.ico",
                Rhino.UI.PanelType.PerDoc);
        }

        internal static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never break loading.
            }
        }
    }
}
