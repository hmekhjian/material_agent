using System;
using MaterialAgent.Core;

namespace MaterialAgent.RhinoSide
{
    /// <summary>Lets commands tell the open panel about changes (e.g. a re-scale done in the viewport).</summary>
    public static class MaterialAgentEvents
    {
        public static event EventHandler<Provenance> ScaleChanged;

        public static void RaiseScaleChanged(Provenance p) => ScaleChanged?.Invoke(null, p);

        /// <summary>The mapping last used in the panel; fallback for objects without provenance.</summary>
        public static MappingSettings LastMapping { get; set; }
    }
}
