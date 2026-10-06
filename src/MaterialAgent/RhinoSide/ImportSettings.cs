using MaterialAgent.Core;

namespace MaterialAgent.RhinoSide
{
    /// <summary>Everything the user confirmed in the preview, ready to turn into a Rhino material.</summary>
    public sealed class ImportSettings
    {
        public string MaterialName { get; set; }
        public FetchedImage Image { get; set; }
        public Provenance Provenance { get; set; }
        public MappingSettings Mapping { get; set; }
        public Finish Finish { get; set; } = Finish.Matt;
        /// <summary>Derive normal and roughness maps from the albedo.</summary>
        public bool GenerateMaps { get; set; }
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
