using System.Linq;
using MaterialAgent.Core;
using MaterialAgent.RhinoSide;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MaterialAgent.Commands
{
    /// <summary>
    /// Re-scale by measurement: pick two points on a feature of the applied texture (a plank edge to edge,
    /// a brick course, a tile with grout) and type its real length. The repeat size is scaled to match.
    /// </summary>
    public sealed class MatAgentRescaleCommand : Command
    {
        public override string EnglishName => "MatAgentRescale";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var go = new GetObject();
            go.SetCommandPrompt("Select objects to re-scale");
            go.GeometryFilter = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.SubD;
            go.SubObjectSelect = false;
            go.GroupSelect = true;
            go.GetMultiple(1, 0);
            if (go.CommandResult() != Result.Success) return go.CommandResult();

            var objects = go.Objects().Select(o => o.Object()).Where(o => o != null).ToList();
            var rm = objects.Select(o => o.RenderMaterial).FirstOrDefault(m => m != null);
            var provenance = ProvenanceStore.Read(doc, rm);
            var mapping = ProvenanceStore.MappingFrom(provenance) ?? MaterialAgentEvents.LastMapping;
            if (mapping == null)
            {
                RhinoApp.WriteLine("These objects have no Material Agent scale. Import a material from the Material Agent panel first.");
                return Result.Failure;
            }

            var gp = new GetPoint();
            gp.SetCommandPrompt("Start of a feature with known size (e.g. one plank edge)");
            gp.Get();
            if (gp.CommandResult() != Result.Success) return gp.CommandResult();
            var p1 = gp.Point();

            gp.SetCommandPrompt("End of the feature");
            gp.SetBasePoint(p1, true);
            gp.DrawLineFromPoint(p1, true);
            gp.Get();
            if (gp.CommandResult() != Result.Success) return gp.CommandResult();
            var p2 = gp.Point();

            double picked = p1.DistanceTo(p2);
            if (picked <= RhinoMath.ZeroTolerance) { RhinoApp.WriteLine("The two points are the same."); return Result.Failure; }

            double modelToMm = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);
            double realMm = picked * modelToMm;
            var rc = RhinoGet.GetNumber("Real length of that feature in mm", false, ref realMm, 0.01, 1e7);
            if (rc != Result.Success) return rc;

            double factor = realMm / (picked * modelToMm);
            var updated = new MappingSettings
            {
                WidthMm = mapping.WidthMm * factor,
                HeightMm = mapping.HeightMm * factor,
                Kind = mapping.Kind,
                Grain = mapping.Grain,
                Rotate90 = mapping.Rotate90,
            };

            uint undo = doc.BeginUndoRecord("Material Agent re-scale");
            try
            {
                MappingApplier.Apply(doc, objects, updated);
                if (provenance != null && rm != null)
                {
                    provenance.WidthMm = updated.WidthMm;
                    provenance.HeightMm = updated.HeightMm;
                    provenance.ScaleSource = ScaleSource.User;
                    provenance.ScaleConfidence = ScaleConfidence.High;
                    ProvenanceStore.Write(doc, rm, provenance);
                }
            }
            finally
            {
                doc.EndUndoRecord(undo);
            }
            doc.Views.Redraw();

            MaterialAgentEvents.LastMapping = updated;
            if (provenance != null) MaterialAgentEvents.RaiseScaleChanged(provenance);
            RhinoApp.WriteLine($"Repeat size is now {updated.WidthMm:0.#} × {updated.HeightMm:0.#} mm (×{factor:0.###}).");
            return Result.Success;
        }
    }
}
