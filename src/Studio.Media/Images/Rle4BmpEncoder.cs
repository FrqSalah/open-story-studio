using System.Buffers.Binary;

namespace Studio.Media.Images;

/// <summary>Writes 16-color BI_RLE4 bitmaps, the image format of v2/v3 devices.</summary>
public static class Rle4BmpEncoder
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int PaletteSize = 16 * 4;
    private const int DataOffset = FileHeaderSize + InfoHeaderSize + PaletteSize; // 0x76

    /// <summary>Encodes an indexed image.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="palette">Up to 16 colors as 0xRRGGBB.</param>
    /// <param name="indices">Palette index of each pixel, top-down, row-major.</param>
    public static byte[] Encode(int width, int height, ReadOnlySpan<int> palette, ReadOnlySpan<byte> indices)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (palette.Length is 0 or > 16) throw new ArgumentException("The palette must contain 1 to 16 colors.", nameof(palette));
        if (indices.Length != width * height) throw new ArgumentException("Pixel count mismatch.", nameof(indices));

        using var data = new MemoryStream();
        // Bitmaps are stored bottom-up.
        for (var y = height - 1; y >= 0; y--)
        {
            var row = indices.Slice(y * width, width);
            var x = 0;
            while (x < width)
            {
                var value = row[x];
                if (value > 15) throw new ArgumentException("Palette indices must be lower than 16.", nameof(indices));
                var run = 1;
                while (x + run < width && run < 255 && row[x + run] == value) run++;
                // Encoded mode: <count> <two nibbles alternated>; both nibbles equal → solid run.
                data.WriteByte((byte)run);
                data.WriteByte((byte)((value << 4) | value));
                x += run;
            }
            data.WriteByte(0); // end of line
            data.WriteByte(0);
        }
        data.WriteByte(0); // end of bitmap
        data.WriteByte(1);

        var pixels = data.ToArray();
        var bmp = new byte[DataOffset + pixels.Length];
        var s = bmp.AsSpan();
        s[0] = (byte)'B';
        s[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(s[2..], bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[10..], DataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(s[14..], InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(s[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(s[22..], height);
        BinaryPrimitives.WriteInt16LittleEndian(s[26..], 1); // planes
        BinaryPrimitives.WriteInt16LittleEndian(s[28..], 4); // bits per pixel
        BinaryPrimitives.WriteInt32LittleEndian(s[30..], 2); // BI_RLE4
        BinaryPrimitives.WriteInt32LittleEndian(s[34..], pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[38..], 2835); // 72 dpi
        BinaryPrimitives.WriteInt32LittleEndian(s[42..], 2835);
        BinaryPrimitives.WriteInt32LittleEndian(s[46..], 16); // colors used
        BinaryPrimitives.WriteInt32LittleEndian(s[50..], 0);
        for (var i = 0; i < palette.Length; i++)
        {
            var c = palette[i];
            var p = s[(FileHeaderSize + InfoHeaderSize + i * 4)..];
            p[0] = (byte)c;          // blue
            p[1] = (byte)(c >> 8);   // green
            p[2] = (byte)(c >> 16);  // red
        }
        pixels.CopyTo(s[DataOffset..]);
        return bmp;
    }

    /// <summary>Decodes a BI_RLE4 bitmap back to palette indices (top-down). Used by tests and previews.</summary>
    public static (int Width, int Height, int[] Palette, byte[] Indices) Decode(ReadOnlySpan<byte> bmp)
    {
        var width = BinaryPrimitives.ReadInt32LittleEndian(bmp[18..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(bmp[22..]);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(bmp[10..]);
        var palette = new int[16];
        for (var i = 0; i < 16; i++)
        {
            var p = bmp[(FileHeaderSize + InfoHeaderSize + i * 4)..];
            palette[i] = (p[2] << 16) | (p[1] << 8) | p[0];
        }

        var indices = new byte[width * height];
        int x = 0, y = height - 1, pos = offset;
        while (pos + 1 < bmp.Length)
        {
            int count = bmp[pos++], value = bmp[pos++];
            if (count > 0)
            {
                for (var i = 0; i < count && x < width; i++, x++)
                    indices[y * width + x] = (byte)(i % 2 == 0 ? value >> 4 : value & 0x0F);
                continue;
            }
            switch (value)
            {
                case 0: x = 0; y--; break;
                case 1: return (width, height, palette, indices);
                case 2: x += bmp[pos++]; y -= bmp[pos++]; break;
                default:
                    for (var i = 0; i < value; i++, x++)
                    {
                        var b = bmp[pos + i / 2];
                        if (x < width) indices[y * width + x] = (byte)(i % 2 == 0 ? b >> 4 : b & 0x0F);
                    }
                    var bytes = (value + 1) / 2;
                    pos += bytes + (bytes % 2);
                    break;
            }
            if (y < 0) break;
        }
        return (width, height, palette, indices);
    }
}
