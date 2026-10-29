using Studio.Core;
using Studio.Core.Model;
using Studio.Formats.DeviceFs;
using Studio.Media.Audio;
using Studio.Media.Images;

namespace Studio.Media;

/// <summary>Error raised when a media file cannot be converted.</summary>
public sealed class MediaConversionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MediaConversionException() { }

    /// <summary>Creates the exception.</summary>
    public MediaConversionException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public MediaConversionException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Produces a device-ready copy of a pack: every image and sound is converted to the format of the
/// target device generation. The library keeps the original files.
/// </summary>
public sealed class DeviceAssetPreparer(IAudioTranscoder audio)
{
    /// <summary>Converts all assets of a pack for the given generation.</summary>
    public async Task<StoryPack> PrepareAsync(StoryPack pack, DeviceGeneration generation, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var assets = pack.Assets.ToList();
        var converted = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);

        for (var i = 0; i < assets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = assets[i];
            converted[asset.Hash] = await ConvertAsync(asset, generation, cancellationToken).ConfigureAwait(false);
            progress?.Report((i + 1) / (double)assets.Count);
        }

        return PackCloner.Clone(pack, a => converted[a.Hash]);
    }

    private async Task<MediaAsset> ConvertAsync(MediaAsset asset, DeviceGeneration generation, CancellationToken cancellationToken)
    {
        var name = asset.FileName;
        try
        {
            return (asset.Kind, generation) switch
            {
                (MediaKind.Image, DeviceGeneration.V1) => new MediaAsset(ImageConverter.ToBmp24(asset.Data), MediaTypes.Bmp, name),
                (MediaKind.Image, _) => new MediaAsset(ImageConverter.ToDeviceBmp(asset.Data), MediaTypes.Bmp, name),
                (MediaKind.Audio, DeviceGeneration.V1) => new MediaAsset(await audio.ToV1WaveAsync(asset.Data, cancellationToken).ConfigureAwait(false), MediaTypes.Wav, name),
                (MediaKind.Audio, _) => new MediaAsset(await audio.ToDeviceMp3Async(asset.Data, cancellationToken).ConfigureAwait(false), MediaTypes.Mp3, name),
                _ => throw new MediaConversionException($"Type de média non supporté : {asset.MimeType}."),
            };
        }
        catch (MediaConversionException e) when (name is not null)
        {
            throw new MediaConversionException($"{name} : {e.Message}", e);
        }
    }
}
