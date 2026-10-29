using SkiaSharp;
using Studio.Core;
using Studio.Core.Model;
using Studio.Formats.Assets;
using Studio.Formats.DeviceFs;
using Studio.Media.Images;

namespace Studio.Tests;

/// <summary>Builders for packs and media used across tests.</summary>
internal static class TestData
{
    /// <summary>Silent mono 44.1 kHz MP3 accepted by v2/v3 devices.</summary>
    public static byte[] DeviceMp3 => EmbeddedData.BlankMp3;

    /// <summary>A device-ready 320x240 RLE4 bitmap filled with one color index.</summary>
    public static byte[] DeviceBmp(byte colorIndex = 1)
    {
        var indices = new byte[320 * 240];
        Array.Fill(indices, colorIndex);
        for (var x = 0; x < 320; x++) indices[x] = 0; // first row differs to exercise runs
        return Rle4BmpEncoder.Encode(320, 240, [0x000000, 0xFF0000, 0x00FF00, 0x0000FF], indices);
    }

    /// <summary>A PNG picture of the given size with a gradient.</summary>
    public static byte[] Png(int width = 640, int height = 360)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 255 / width), (byte)(y * 255 / height), 128));
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A WAVE file (PCM 16-bit mono) of a short tone.</summary>
    public static byte[] Wave(int sampleRate = 32000, int channels = 1, int samples = 3200)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataLength = samples * channels * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataLength);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataLength);
        for (var i = 0; i < samples * channels; i++) w.Write((short)(Math.Sin(i / 10.0) * 8000));
        return ms.ToArray();
    }

    /// <summary>A three-story menu pack with device-ready media on every stage.</summary>
    public static StoryPack DeviceReadyMenu(int stories = 3)
    {
        var pack = PackTemplates.StoryMenu("Test", stories);
        var image = new MediaAsset(DeviceBmp(), MediaTypes.Bmp, "cover.bmp");
        var other = new MediaAsset(DeviceBmp(2), MediaTypes.Bmp, "menu.bmp");
        var audio = new MediaAsset(DeviceMp3, MediaTypes.Mp3, "silence.mp3");
        for (var i = 0; i < pack.StageNodes.Count; i++)
        {
            pack.StageNodes[i].Image = i == 0 ? image : other;
            pack.StageNodes[i].Audio = i % 2 == 0 ? audio : null;
        }
        return pack;
    }

    /// <summary>A synthetic v2 metadata file.</summary>
    public static byte[] V2Metadata() =>
        DeviceMetadata.CreateV2(2, 4, 23023030000123, Enumerable.Range(0, 256).Select(i => (byte)(i * 7 + 1)).ToArray());

    /// <summary>A synthetic v3 metadata file (format 7).</summary>
    public static byte[] V3Metadata() =>
        DeviceMetadata.CreateV7(3, 2, "2300123456789A", Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray(), Enumerable.Range(0, 16).Select(i => (byte)(0xF0 - i)).ToArray());
}
