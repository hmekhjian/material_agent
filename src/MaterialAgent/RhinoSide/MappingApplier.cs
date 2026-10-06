using System;
using System.Collections.Generic;
using MaterialAgent.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Render;

namespace MaterialAgent.RhinoSide
{
    /// <summary>
    /// Builds texture mappings where one UV cycle equals one real-world texture repeat,
    /// so the material shows at true scale regardless of object size.
    /// </summary>
    public static class MappingApplier
    {
        /// <summary>Mapping channel our textures read from.</summary>
        public const int Channel = 1;

        public static int Apply(RhinoDoc doc, IEnumerable<RhinoObject> objects, MappingSettings settings)
        {
            double toModel = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
            double w = MappingMath.MmToModel(settings.WidthMm, toModel);
            double h = MappingMath.MmToModel(settings.HeightMm, toModel);

            int count = 0;
            foreach (var obj in objects)
            {
                if (obj == null) continue;
                var mapping = BuildMapping(obj, w, h, settings);
                if (mapping != null && doc.Objects.ModifyTextureMapping(obj, Channel, mapping))
                    count++;
            }
            return count;
        }

        static TextureMapping BuildMapping(RhinoObject obj, double w, double h, MappingSettings s)
        {
            var bb = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
            if (!bb.IsValid) return null;
            var size = bb.Diagonal;

            Plane plane;
            double extentU, extentV;
            if (s.Kind == MappingKind.Planar)
            {
                // Project along the object's thinnest world axis (a panel lying flat, or standing up).
                if (size.Z <= size.X && size.Z <= size.Y)
                {
                    plane = new Plane(bb.Min, Vector3d.XAxis, Vector3d.YAxis);
                    extentU = size.X; extentV = size.Y;
                }
                else if (size.X <= size.Y)
                {
                    plane = new Plane(bb.Min, Vector3d.YAxis, Vector3d.ZAxis);
                    extentU = size.Y; extentV = size.Z;
                }
                else
                {
                    plane = new Plane(bb.Min, Vector3d.XAxis, Vector3d.ZAxis);
                    extentU = size.X; extentV = size.Z;
                }
            }
            else
            {
                plane = new Plane(bb.Min, Vector3d.XAxis, Vector3d.YAxis);
                extentU = size.X; extentV = size.Y;
            }

            bool rotate = MappingMath.ShouldRotateForGrain(s.Grain, extentU, extentV) ^ s.Rotate90;
            if (rotate) plane.Rotate(Math.PI / 2.0, plane.ZAxis);

            var dx = new Interval(0, w);
            var dy = new Interval(0, h);

            switch (s.Kind)
            {
                case MappingKind.Planar:
                    return TextureMapping.CreatePlaneMapping(plane, dx, dy, new Interval(-1, 1), true);
                default:
                    // Box (and per-face for now): side faces take V from Z, so Z repeats at the texture height.
                    return TextureMapping.CreateBoxMapping(plane, dx, dy, new Interval(0, h), true);
            }
        }
    }
}
