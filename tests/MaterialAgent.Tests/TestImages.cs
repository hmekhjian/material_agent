using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MaterialAgent.Tests
{
    /// <summary>Builds small valid PNGs in memory (no image library needed).</summary>
    static class TestImages
    {
        public static byte[] Png(int width, int height, byte shade = 128)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var ihdr = new byte[13];
                WriteBE(ihdr, 0, width); WriteBE(ihdr, 4, height);
                ihdr[8] = 8; ihdr[9] = 0; // 8-bit greyscale
                Chunk(ms, "IHDR", ihdr);

                var raw = new byte[(width + 1) * height];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) raw[y * (width + 1) + 1 + x] = shade;
                using (var z = new MemoryStream())
                {
                    z.WriteByte(0x78); z.WriteByte(0x9C);
                    using (var d = new DeflateStream(z, CompressionLevel.Fastest, true)) d.Write(raw, 0, raw.Length);
                    var a = Adler32(raw);
                    z.Write(new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a }, 0, 4);
                    Chunk(ms, "IDAT", z.ToArray());
                }
                Chunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len, 0, 4);
            var t = Encoding.ASCII.GetBytes(type); s.Write(t, 0, 4); s.Write(data, 0, data.Length);
            var crcBuf = new byte[4 + data.Length]; Array.Copy(t, crcBuf, 4); Array.Copy(data, 0, crcBuf, 4, data.Length);
            var c = new byte[4]; WriteBE(c, 0, (int)Crc(crcBuf)); s.Write(c, 0, 4);
        }

        static void WriteBE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        static uint Crc(byte[] data)
        {
            uint c = 0xFFFFFFFF;
            foreach (var b in data) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
            return c ^ 0xFFFFFFFF;
        }

        static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }
    }
}
