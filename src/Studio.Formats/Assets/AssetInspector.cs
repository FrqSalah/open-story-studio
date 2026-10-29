using System.Buffers.Binary;

namespace Studio.Formats.Assets;

/// <summary>Basic properties of an MP3 stream, read from its first frame header.</summary>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="Channels">1 (mono) or 2.</param>
/// <param name="BitrateKbps">Bitrate of the first frame.</param>
public readonly record struct Mp3Info(int SampleRate, int Channels, int BitrateKbps);

/// <summary>Basic properties of a BMP image.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels (absolute).</param>
/// <param name="BitsPerPixel">Color depth.</param>
/// <param name="Compression">BI_* compression (2 = RLE4).</param>
public readonly record struct BmpInfo(int Width, int Height, int BitsPerPixel, int Compression)
{
    /// <summary>True for a 4-bit RLE compressed bitmap.</summary>
    public bool IsRle4 => BitsPerPixel == 4 && Compression == 2;
}

/// <summary>Lightweight, dependency-free inspection of media files.</summary>
public static class AssetInspector
{
    /// <summary>Screen width of the device.</summary>
    public const int ScreenWidth = 320;

    /// <summary>Screen height of the device.</summary>
    public const int ScreenHeight = 240;

    /// <summary>Sample rate expected for MP3 files on v2/v3 devices.</summary>
    public const int Mp3SampleRate = 44100;

    private static readonly int[][] SampleRates =
    [
        [11025, 12000, 8000], // MPEG 2.5
        [], // reserved
        [22050, 24000, 16000], // MPEG 2
        [44100, 48000, 32000], // MPEG 1
    ];

    private static readonly int[] BitratesV1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] BitratesV2L3 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

    /// <summary>True when the data starts with an ID3v2 tag.</summary>
    public static bool HasId3v2(ReadOnlySpan<byte> mp3) => mp3.Length >= 10 && mp3[0] == 'I' && mp3[1] == 'D' && mp3[2] == '3';

    /// <summary>True when the data ends with an ID3v1 tag.</summary>
    public static bool HasId3v1(ReadOnlySpan<byte> mp3) =>
        mp3.Length >= 128 && mp3[^128] == 'T' && mp3[^127] == 'A' && mp3[^126] == 'G';

    /// <summary>Removes ID3v1 and ID3v2 tags (the firmware does not support them).</summary>
    public static byte[] StripId3(ReadOnlySpan<byte> mp3)
    {
        var start = 0;
        while (HasId3v2(mp3[start..]))
        {
            var s = mp3[(start + 6)..];
            var size = ((s[0] & 0x7f) << 21) | ((s[1] & 0x7f) << 14) | ((s[2] & 0x7f) << 7) | (s[3] & 0x7f);
            var footer = (mp3[start + 5] & 0x10) != 0 ? 10 : 0;
            start += 10 + size + footer;
            if (start >= mp3.Length) return [];
        }
        var end = mp3.Length;
        if (HasId3v1(mp3[start..end])) end -= 128;
        return mp3[start..end].ToArray();
    }

    /// <summary>Reads the first MPEG audio frame header (skipping ID3v2 tags).</summary>
    public static Mp3Info? ReadMp3Info(ReadOnlySpan<byte> mp3)
    {
        var data = HasId3v2(mp3) ? StripId3(mp3) : mp3.ToArray();
        for (var i = 0; i + 4 <= data.Length; i++)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xE0) != 0xE0) continue;
            var version = (data[i + 1] >> 3) & 0x03;
            var layer = (data[i + 1] >> 1) & 0x03;
            var bitrateIndex = (data[i + 2] >> 4) & 0x0F;
            var rateIndex = (data[i + 2] >> 2) & 0x03;
            var channelMode = (data[i + 3] >> 6) & 0x03;
            if (version == 1 || layer == 0 || rateIndex == 3 || bitrateIndex == 15) continue;

            var bitrates = version == 3 ? BitratesV1L3 : BitratesV2L3;
            return new Mp3Info(SampleRates[version][rateIndex], channelMode == 3 ? 1 : 2, bitrates[bitrateIndex]);
        }
        return null;
    }

    /// <summary>Reads a BMP header.</summary>
    public static BmpInfo? ReadBmpInfo(ReadOnlySpan<byte> bmp)
    {
        if (bmp.Length < 34 || bmp[0] != 'B' || bmp[1] != 'M') return null;
        return new BmpInfo(
            BinaryPrimitives.ReadInt32LittleEndian(bmp[18..]),
            Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bmp[22..])),
            BinaryPrimitives.ReadInt16LittleEndian(bmp[28..]),
            BinaryPrimitives.ReadInt32LittleEndian(bmp[30..]));
    }

    /// <summary>Checks an image is ready for v2/v3 devices (320x240 BMP, 4-bit RLE).</summary>
    public static string? CheckDeviceImage(ReadOnlySpan<byte> image)
    {
        var info = ReadBmpInfo(image);
        if (info is null) return "l'image doit être un BMP";
        if (info.Value.Width != ScreenWidth || info.Value.Height != ScreenHeight) return $"l'image doit faire {ScreenWidth}x{ScreenHeight} pixels (actuellement {info.Value.Width}x{info.Value.Height})";
        if (!info.Value.IsRle4) return "l'image doit être en 16 couleurs compressée RLE4";
        return null;
    }

    /// <summary>Checks an audio file is ready for v2/v3 devices (MP3 mono 44.1 kHz without ID3).</summary>
    public static string? CheckDeviceAudio(ReadOnlySpan<byte> audio)
    {
        if (HasId3v2(audio) || HasId3v1(audio)) return "le MP3 ne doit pas contenir de tags ID3";
        var info = ReadMp3Info(audio);
        if (info is null) return "le son doit être un MP3";
        if (info.Value.Channels != 1) return "le MP3 doit être en mono";
        if (info.Value.SampleRate != Mp3SampleRate) return $"le MP3 doit être échantillonné à {Mp3SampleRate} Hz (actuellement {info.Value.SampleRate} Hz)";
        return null;
    }
}
