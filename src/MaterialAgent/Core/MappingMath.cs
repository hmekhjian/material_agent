using System;

namespace MaterialAgent.Core
{
    /// <summary>Pure maths for real-world texture mapping. No Rhino types so it can be unit tested.</summary>
    public static class MappingMath
    {
        /// <summary>
        /// Converts a repeat size in millimetres into model units.
        /// <paramref name="mmToModelScale"/> is RhinoMath.UnitScale(Millimeters, doc.ModelUnitSystem).
        /// </summary>
        public static double MmToModel(double mm, double mmToModelScale)
        {
            if (!(mm > 0)) throw new ArgumentOutOfRangeException(nameof(mm), "Repeat size must be positive.");
            if (!(mmToModelScale > 0)) throw new ArgumentOutOfRangeException(nameof(mmToModelScale));
            return mm * mmToModelScale;
        }

        /// <summary>
        /// Decides whether to turn the mapping 90 degrees about world Z so the texture's grain
        /// follows the object's longer horizontal side.
        /// Texture U runs along the mapping plane's X axis and V along its Y axis (top faces).
        /// </summary>
        /// <param name="grain">Direction the grain runs in the image.</param>
        /// <param name="extentX">Object size along world X.</param>
        /// <param name="extentY">Object size along world Y.</param>
        public static bool ShouldRotateForGrain(GrainAxis grain, double extentX, double extentY)
        {
            if (grain == GrainAxis.None) return false;
            // Treat near-square footprints as "no preference" to avoid flip-flopping on tiny differences.
            const double tolerance = 1.02;
            bool longAlongX = extentX > extentY * tolerance;
            bool longAlongY = extentY > extentX * tolerance;
            if (!longAlongX && !longAlongY) return false;

            // Horizontal grain follows U (plane X). Vertical grain follows V (plane Y).
            return grain == GrainAxis.Horizontal ? longAlongY : longAlongX;
        }
    }
}
