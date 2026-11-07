using System.Buffers.Binary;
using System.IO.Compression;

namespace ScreenRecorder.Core.Project;

/// <summary>Minimal PNG reader/writer for 32-bit BGRA images (cursor shapes and screenshots).</summary>
public static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes straight (non-premultiplied) BGRA pixels as an RGBA PNG.</summary>
    public static void WriteBgra(Stream output, int width, int height, ReadOnlySpan<byte> bgra, int stride)
    {
        output.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(output, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 4];
            for (int y = 0; y < height; y++)
            {
                row[0] = 0;
                var src = bgra.Slice(y * stride, width * 4);
                for (int x = 0; x < width; x++)
                {
                    row[1 + x * 4] = src[x * 4 + 2];
                    row[2 + x * 4] = src[x * 4 + 1];
                    row[3 + x * 4] = src[x * 4];
                    row[4 + x * 4] = src[x * 4 + 3];
                }
                z.Write(row);
            }
        }
        WriteChunk(output, "IDAT", raw.ToArray());
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    public static void WriteBgra(string path, int width, int height, ReadOnlySpan<byte> bgra, int stride)
    {
        using var fs = File.Create(path);
        WriteBgra(fs, width, height, bgra, stride);
    }

    /// <summary>Reads a PNG written by <see cref="WriteBgra(Stream,int,int,ReadOnlySpan{byte},int)"/> (RGBA, 8-bit, filter 0).</summary>
    public static (int Width, int Height, byte[] Bgra) ReadBgra(string path)
    {
        var data = File.ReadAllBytes(path);
        if (!data.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("Not a PNG file.");
        int pos = 8, width = 0, height = 0;
        using var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = data.AsSpan(pos + 8, len);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                if (body[8] != 8 || body[9] != 6) throw new InvalidDataException("Only 8-bit RGBA PNGs are supported.");
            }
            else if (type == "IDAT") idat.Write(body);
            else if (type == "IEND") break;
            pos += 12 + len;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var bgra = new byte[width * height * 4];
        var row = new byte[1 + width * 4];
        var prev = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            z.ReadExactly(row);
            Unfilter(row[0], row.AsSpan(1), prev, 4);
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                bgra[o] = row[3 + x * 4];
                bgra[o + 1] = row[2 + x * 4];
                bgra[o + 2] = row[1 + x * 4];
                bgra[o + 3] = row[4 + x * 4];
            }
            row.AsSpan(1).CopyTo(prev);
        }
        return (width, height, bgra);
    }

    private static void Unfilter(byte filter, Span<byte> cur, ReadOnlySpan<byte> prev, int bpp)
    {
        for (int i = 0; i < cur.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            cur[i] = filter switch
            {
                0 => cur[i],
                1 => (byte)(cur[i] + a),
                2 => (byte)(cur[i] + b),
                3 => (byte)(cur[i] + ((a + b) >> 1)),
                4 => (byte)(cur[i] + Paeth(a, b, c)),
                _ => throw new InvalidDataException("Bad PNG filter."),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        System.Text.Encoding.ASCII.GetBytes(type, header[4..]);
        s.Write(header);
        s.Write(body);
        uint crc = Crc(Crc(0xFFFFFFFF, header[4..]), body) ^ 0xFFFFFFFF;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
