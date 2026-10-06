using System.IO;
using System.Linq;
using Eto.Drawing;
using MaterialAgent.Core;
using MaterialAgent.Core.Maps;

namespace MaterialAgent.RhinoSide
{
    public sealed class BakedMaps
    {
        public string NormalPath { get; set; }
        public string RoughnessPath { get; set; }
    }

    /// <summary>Decodes the albedo with Eto, derives normal/roughness maps (Core) and writes them as PNGs. UI thread.</summary>
    public static class MapBaker
    {
        /// <summary>Derived maps don't need full resolution; this keeps baking under a second or two.</summary>
        public const int MaxSide = 1024;

        public static BakedMaps Bake(FetchedImage albedo, Finish finish)
        {
            Bitmap source;
            using (var ms = new MemoryStream(albedo.Bytes))
                source = new Bitmap(ms);

            using (source)
            {
                var (w, h) = SurfaceMaps.FitWithin(source.Width, source.Height, MaxSide);
                var work = w == source.Width && h == source.Height ? source : new Bitmap(source, w, h, ImageInterpolation.High);
                try
                {
                    var luminance = new float[w * h];
                    using (var data = work.Lock())
                    {
                        int i = 0;
                        foreach (var c in data.GetPixels())
                        {
                            if (i >= luminance.Length) break;
                            luminance[i++] = SurfaceMaps.Luminance((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255));
                        }
                    }

                    var maps = SurfaceMaps.Generate(luminance, w, h, EnumText.Roughness(finish),
                        normalStrength: finish == Finish.Textured ? 3.0 : 2.0);

                    var stem = Path.Combine(ImageFetcher.DownloadFolder, Path.GetFileNameWithoutExtension(albedo.LocalPath) + "_" + EnumText.ToWire(finish));
                    Directory.CreateDirectory(ImageFetcher.DownloadFolder);
                    var normalPath = stem + "_normal.png";
                    var roughPath = stem + "_roughness.png";

                    var normalColors = Enumerable.Range(0, w * h).Select(p =>
                        Color.FromArgb(maps.NormalRgb[p * 3], maps.NormalRgb[p * 3 + 1], maps.NormalRgb[p * 3 + 2]));
                    using (var nb = new Bitmap(w, h, PixelFormat.Format32bppRgb, normalColors))
                        nb.Save(normalPath, Eto.Drawing.ImageFormat.Png);

                    var roughColors = Enumerable.Range(0, w * h).Select(p =>
                        Color.FromArgb(maps.Roughness[p], maps.Roughness[p], maps.Roughness[p]));
                    using (var rb = new Bitmap(w, h, PixelFormat.Format32bppRgb, roughColors))
                        rb.Save(roughPath, Eto.Drawing.ImageFormat.Png);

                    return new BakedMaps { NormalPath = normalPath, RoughnessPath = roughPath };
                }
                finally
                {
                    if (!ReferenceEquals(work, source)) work.Dispose();
                }
            }
        }
    }
}
