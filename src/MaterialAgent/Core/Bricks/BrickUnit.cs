using System.Text.Json.Serialization;

namespace MaterialAgent.Core.Bricks
{
    /// <summary>Size of one brick (or block/paver) in mm, as stated by the manufacturer.</summary>
    public sealed class BrickUnit
    {
        [JsonPropertyName("length_mm")] public double LengthMm { get; set; } = 215;
        [JsonPropertyName("height_mm")] public double HeightMm { get; set; } = 65;
        /// <summary>Bed depth; the visible size of a header (brick laid end-on).</summary>
        [JsonPropertyName("depth_mm")] public double DepthMm { get; set; } = 102.5;

        [JsonIgnore] public bool IsUsable => LengthMm > 0 && HeightMm > 0;

        /// <summary>UK standard brick: 215 × 102.5 × 65 mm.</summary>
        public static BrickUnit UkStandard() => new BrickUnit { LengthMm = 215, HeightMm = 65, DepthMm = 102.5 };
    }
}
