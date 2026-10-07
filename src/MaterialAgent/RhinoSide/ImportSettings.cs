using MaterialAgent.Core;
using MaterialAgent.Core.Colors;

namespace MaterialAgent.RhinoSide
{
    /// <summary>Everything the user confirmed in the preview, ready to turn into a Rhino material.</summary>
    public sealed class ImportSettings
    {
        public string MaterialName { get; set; }
        public FetchedImage Image { get; set; }
        /// <summary>Plain-colour material (RAL). When set, no texture, maps or mapping are used.</summary>
        public RalColor SolidColor { get; set; }
        public Provenance Provenance { get; set; }
        public MappingSettings Mapping { get; set; }
        public Finish Finish { get; set; } = Finish.Matt;
        /// <summary>Derive normal and roughness maps from the albedo.</summary>
        public bool GenerateMaps { get; set; }
        /// <summary>Ready-made maps (e.g. from the pattern generator); used instead of deriving them when maps are on.</summary>
        public Core.Agent.SurfaceMapFiles PrebakedMaps { get; set; }
        /// <summary>Convert to Enscape's material type so it shows in the Enscape Material Editor.</summary>
        public bool AsEnscape { get; set; }
    }

    public sealed class MappingSettings
    {
        /// <summary>Real-world width of one texture repeat (along texture U), in millimetres.</summary>
        public double WidthMm { get; set; }
        /// <summary>Real-world height of one texture repeat (along texture V), in millimetres.</summary>
        public double HeightMm { get; set; }
        public MappingKind Kind { get; set; } = MappingKind.Box;
        public GrainAxis Grain { get; set; } = GrainAxis.None;
        /// <summary>Extra 90 degree turn on top of the automatic grain alignment.</summary>
        public bool Rotate90 { get; set; }
    }
}
