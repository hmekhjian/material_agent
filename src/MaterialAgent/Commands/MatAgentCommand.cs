using MaterialAgent.UI;
using Rhino;
using Rhino.Commands;

namespace MaterialAgent.Commands
{
    public sealed class MatAgentCommand : Command
    {
        public override string EnglishName => "MatAgent";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            Rhino.UI.Panels.OpenPanel(MaterialAgentPanel.PanelId);
            return Result.Success;
        }
    }
}
