using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MaterialAgent.Core
{
    /// <summary>Image preparation for model calls.</summary>
    public static class ImagePrep
    {
        /// <summary>
        /// A JPEG no larger than <paramref name="maxSide"/> pixels, for the vision check. Falls back to the
        /// original bytes if the image can't be decoded.
        /// </summary>
        public static byte[] ForVision(byte[] imageBytes, int maxSide = 768)
        {
            try
            {
                using (var img = Image.Load<Rgb24>(imageBytes))
                using (var ms = new MemoryStream())
                {
                    while (img.Frames.Count > 1) img.Frames.RemoveFrame(img.Frames.Count - 1);
                    if (img.Width > maxSide || img.Height > maxSide)
                        img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(maxSide, maxSide) }));
                    img.Save(ms, new JpegEncoder { Quality = 85 });
                    return ms.ToArray();
                }
            }
            catch
            {
                return imageBytes;
            }
        }
    }
}
