using MaterialAgent.UI;
using Rhino.PlugIns;

namespace MaterialAgent
{
    public sealed class MaterialAgentPlugin : PlugIn
    {
        public MaterialAgentPlugin()
        {
            Instance = this;
        }

        public static MaterialAgentPlugin Instance { get; private set; }

        // Load at start-up so the panel is registered and restores with the user's layout.
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            Rhino.UI.Panels.RegisterPanel(
                this,
                typeof(MaterialAgentPanel),
                "Material Agent",
                typeof(MaterialAgentPlugin).Assembly,
                "MaterialAgent.Resources.MaterialAgent.ico",
                Rhino.UI.PanelType.PerDoc);
            return LoadReturnCode.Success;
        }
    }
}
