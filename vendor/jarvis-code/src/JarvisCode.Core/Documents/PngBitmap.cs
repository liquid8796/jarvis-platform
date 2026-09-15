using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace JarvisCode.Core.Documents;

/// <summary>Lossless RGB PNG encoding for the opaque, white-backed PDF raster.</summary>
internal static class PngBitmap
{
    internal static byte[] EncodeBgra(byte[] pixels, int width, int height, int stride)
    {
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2; // RGB; every PDF bitmap starts opaque white.
        WriteChunk(png, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[checked(width * 3 + 1)]; // filter byte 0
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var source = y * stride + x * 4;
                    var target = x * 3 + 1;
                    row[target] = pixels[source + 2];
                    row[target + 1] = pixels[source + 1];
                    row[target + 2] = pixels[source];
                }
                zlib.Write(row);
            }
        }
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream target, string name, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        target.Write(number);
        var type = Encoding.ASCII.GetBytes(name);
        target.Write(type);
        target.Write(data);
        uint crc = 0xffffffff;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        target.Write(number);
    }
}
