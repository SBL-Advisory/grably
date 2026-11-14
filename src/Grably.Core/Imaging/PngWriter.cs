using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Grably.Core.Imaging;

/// <summary>
/// Minimal PNG encoder for <see cref="PixelBuffer"/> (8-bit RGBA, no filtering), so tools and tests can save images
/// without WPF or WIC. The app itself uses the WPF encoders.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Save(PixelBuffer image, string path)
    {
        using var file = File.Create(path);
        Write(image, file);
    }

    public static void Write(PixelBuffer image, Stream output)
    {
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: RGBA
        header[10] = 0; // compression
        header[11] = 0; // filter method
        header[12] = 0; // no interlace
        WriteChunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[1 + (image.Width * 4)]; // filter byte 0 then RGBA
                for (var y = 0; y < image.Height; y++)
                {
                    var src = y * image.Stride;
                    for (var x = 0; x < image.Width; x++)
                    {
                        var s = src + (x * 4);
                        var d = 1 + (x * 4);
                        row[d] = image.Pixels[s + 2];
                        row[d + 1] = image.Pixels[s + 1];
                        row[d + 2] = image.Pixels[s];
                        row[d + 3] = image.Pixels[s + 3];
                    }

                    zlib.Write(row);
                }
            }

            WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }

        WriteChunk(output, "IEND", []);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
