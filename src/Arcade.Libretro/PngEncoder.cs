using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Arcade.Libretro;

/// <summary>Dependency-free RGBA PNG writer, used for frame dumps and screenshots.</summary>
public static class PngEncoder
{
    static readonly uint[] CrcTable = BuildCrcTable();

    public static void Save(Rgba32Image image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        Write(image, file);
    }

    public static void Write(Rgba32Image image, Stream output)
    {
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), image.Height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // colour type RGBA
        WriteChunk(output, "IHDR", ihdr);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var stride = image.Width * 4;
            for (var y = 0; y < image.Height; y++)
            {
                zlib.WriteByte(0); // filter: none
                zlib.Write(image.Pixels, y * stride, stride);
            }
        }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
    }

    static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        output.Write(header);
        output.Write(data);

        var crc = Crc32(0xFFFFFFFFu, header[4..]);
        crc = Crc32(crc, data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
