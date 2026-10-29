using Studio.Core.Model;
using Studio.Formats.Assets;
using Studio.Formats.DeviceFs;
using Studio.Media;
using Studio.Media.Audio;
using Studio.Media.Images;

namespace Studio.Tests.Media;

public class ImageTests
{
    [Fact]
    public void Rle4_round_trips_indices_and_palette()
    {
        var indices = new byte[320 * 240];
        for (var i = 0; i < indices.Length; i++) indices[i] = (byte)(i / 7 % 16);
        var palette = Enumerable.Range(0, 16).Select(i => i * 0x101010).ToArray();
        var bmp = Rle4BmpEncoder.Encode(320, 240, palette, indices);

        Assert.Null(AssetInspector.CheckDeviceImage(bmp));
        var (w, h, p, decoded) = Rle4BmpEncoder.Decode(bmp);
        Assert.Equal((320, 240), (w, h));
        Assert.Equal(palette, p);
        Assert.Equal(indices, decoded);
    }

    [Fact]
    public void Header_matches_the_device_layout()
    {
        var bmp = TestData.DeviceBmp();
        var info = AssetInspector.ReadBmpInfo(bmp)!.Value;
        Assert.True(info.IsRle4);
        Assert.Equal(0x76, BitConverter.ToInt32(bmp, 10)); // pixel data offset used by the firmware
    }

    [Fact]
    public void Converts_any_picture_to_a_device_bitmap()
    {
        var bmp = ImageConverter.ToDeviceBmp(TestData.Png(800, 600));
        Assert.Null(AssetInspector.CheckDeviceImage(bmp));
        var (_, _, palette, indices) = Rle4BmpEncoder.Decode(bmp);
        Assert.True(indices.Distinct().Count() > 4, "the gradient should use several palette entries");
        Assert.All(indices, i => Assert.InRange(i, 0, 15));
        Assert.Equal(16, palette.Length);
    }

    [Theory]
    [InlineData(FitMode.Pad)]
    [InlineData(FitMode.Crop)]
    public void Fits_wide_pictures(FitMode mode)
    {
        var bmp = ImageConverter.ToDeviceBmp(TestData.Png(1000, 200), mode);
        Assert.Null(AssetInspector.CheckDeviceImage(bmp));
    }

    [Fact]
    public void Compliant_bitmaps_are_kept_as_is()
    {
        var bmp = TestData.DeviceBmp();
        Assert.Same(bmp, ImageConverter.ToDeviceBmp(bmp));
    }

    [Fact]
    public void Converts_to_24_bit_bitmap_for_v1()
    {
        var bmp = ImageConverter.ToBmp24(TestData.Png());
        var info = AssetInspector.ReadBmpInfo(bmp)!.Value;
        Assert.Equal((320, 240, 24, 0), (info.Width, info.Height, info.BitsPerPixel, info.Compression));
    }

    [Fact]
    public void Rejects_garbage() =>
        Assert.Throws<MediaConversionException>(() => ImageConverter.ToDeviceBmp([1, 2, 3, 4, 5]));
}

public class AudioTests
{
    [Fact]
    public void Blank_mp3_is_mono_44100()
    {
        var info = AssetInspector.ReadMp3Info(TestData.DeviceMp3)!.Value;
        Assert.Equal(1, info.Channels);
        Assert.Equal(44100, info.SampleRate);
        Assert.Null(AssetInspector.CheckDeviceAudio(TestData.DeviceMp3));
    }

    [Fact]
    public void Strips_id3v2_and_id3v1_tags()
    {
        // ID3v2 header with a 5-byte body (synchsafe size), then the audio, then a 128-byte ID3v1 tag.
        byte[] tagged = [.. "ID3"u8, 3, 0, 0, 0, 0, 0, 5, 1, 2, 3, 4, 5, .. TestData.DeviceMp3, .. "TAG"u8, .. new byte[125]];
        Assert.True(AssetInspector.HasId3v2(tagged));
        Assert.Equal(TestData.DeviceMp3, AssetInspector.StripId3(tagged));
    }

    [Fact]
    public async Task Passthrough_accepts_compliant_mp3_after_removing_tags()
    {
        byte[] tagged = [.. "ID3"u8, 3, 0, 0, 0, 0, 0, 0, .. TestData.DeviceMp3];
        var result = await new PassthroughAudioTranscoder().ToDeviceMp3Async(tagged, TestContext.Current.CancellationToken);
        Assert.Equal(TestData.DeviceMp3, result);
    }

    [Fact]
    public async Task Passthrough_rejects_formats_it_cannot_convert()
    {
        var e = await Assert.ThrowsAsync<MediaConversionException>(() =>
            new PassthroughAudioTranscoder().ToDeviceMp3Async(TestData.Wave(), TestContext.Current.CancellationToken));
        Assert.Contains("MP3", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ffmpeg_transcodes_wave_when_available()
    {
        if (FfmpegAudioTranscoder.Find() is not { } ffmpeg)
        {
            Assert.Skip("ffmpeg n'est pas installé.");
            return;
        }
        var transcoder = new FfmpegAudioTranscoder(ffmpeg);
        var mp3 = await transcoder.ToDeviceMp3Async(TestData.Wave(48000, 2, 48000), TestContext.Current.CancellationToken);
        Assert.Null(AssetInspector.CheckDeviceAudio(mp3));
        var wav = await transcoder.ToV1WaveAsync(mp3, TestContext.Current.CancellationToken);
        Assert.Null(Studio.Formats.Raw.RawPackFormat.CheckWave(wav));
    }
}

public class DeviceAssetPreparerTests
{
    [Fact]
    public async Task Converts_every_asset_for_v2_and_keeps_the_original()
    {
        var pack = TestData.DeviceReadyMenu(2);
        var png = new MediaAsset(TestData.Png(), fileName: "photo.png");
        pack.StageNodes[0].Image = png;

        var ready = await new DeviceAssetPreparer(new PassthroughAudioTranscoder())
            .PrepareAsync(pack, DeviceGeneration.V2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MediaTypes.Png, pack.StageNodes[0].Image!.MimeType);
        Assert.Equal(MediaTypes.Bmp, ready.StageNodes[0].Image!.MimeType);
        Assert.Null(AssetInspector.CheckDeviceImage(ready.StageNodes[0].Image!.Data));
        Assert.Equal(pack.Id, ready.Id);
        Assert.Equal(pack.StageNodes.Select(s => s.Id), ready.StageNodes.Select(s => s.Id));
    }

    [Fact]
    public async Task Reports_the_file_name_on_conversion_errors()
    {
        var pack = TestData.DeviceReadyMenu(1);
        pack.StageNodes[1].Audio = new MediaAsset(TestData.Wave(), MediaTypes.Wav, "voix.wav");
        var e = await Assert.ThrowsAsync<MediaConversionException>(() =>
            new DeviceAssetPreparer(new PassthroughAudioTranscoder()).PrepareAsync(pack, DeviceGeneration.V2, cancellationToken: TestContext.Current.CancellationToken));
        Assert.StartsWith("voix.wav", e.Message, StringComparison.Ordinal);
    }
}
