using System.Security.Cryptography;

namespace Studio.Core.Model;

/// <summary>Kind of media.</summary>
public enum MediaKind
{
    /// <summary>Unknown or unsupported.</summary>
    Unknown,
    /// <summary>Image.</summary>
    Image,
    /// <summary>Audio.</summary>
    Audio,
}

/// <summary>
/// Immutable media file (image or audio) attached to a stage node.
/// Assets with identical bytes share the same <see cref="Hash"/> and are stored once.
/// </summary>
public sealed class MediaAsset
{
    private string? _hash;
    private string? _dataUrl;

    /// <summary>Creates an asset from raw bytes.</summary>
    /// <param name="data">File content.</param>
    /// <param name="mimeType">MIME type; detected from content when null.</param>
    /// <param name="fileName">Original file name, informative only.</param>
    public MediaAsset(byte[] data, string? mimeType = null, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        MimeType = mimeType ?? MediaTypes.Detect(data) ?? MediaTypes.FromFileName(fileName) ?? "application/octet-stream";
        FileName = fileName;
    }

    /// <summary>File content.</summary>
    public byte[] Data { get; }

    /// <summary>MIME type (e.g. <c>image/png</c>, <c>audio/mpeg</c>).</summary>
    public string MimeType { get; }

    /// <summary>Original file name, if known.</summary>
    public string? FileName { get; }

    /// <summary>Lowercase SHA-1 of the content, used for de-duplication and archive file names.</summary>
#pragma warning disable CA5350 // Content addressing only (STUdio asset naming), not a security feature.
    public string Hash => _hash ??= Convert.ToHexStringLower(SHA1.HashData(Data));
#pragma warning restore CA5350

    /// <summary>Image or audio.</summary>
    public MediaKind Kind => MediaTypes.KindOf(MimeType);

    /// <summary>File extension matching the MIME type, including the dot.</summary>
    public string Extension => MediaTypes.ExtensionOf(MimeType);

    /// <summary>Data URL usable in a browser (img/audio src).</summary>
    public string ToDataUrl() => _dataUrl ??= $"data:{MimeType};base64,{Convert.ToBase64String(Data)}";
}

/// <summary>MIME type helpers.</summary>
public static class MediaTypes
{
    /// <summary>BMP image.</summary>
    public const string Bmp = "image/bmp";
    /// <summary>PNG image.</summary>
    public const string Png = "image/png";
    /// <summary>JPEG image.</summary>
    public const string Jpeg = "image/jpeg";
    /// <summary>MP3 audio.</summary>
    public const string Mp3 = "audio/mpeg";
    /// <summary>Ogg Vorbis audio.</summary>
    public const string Ogg = "audio/ogg";
    /// <summary>WAVE audio.</summary>
    public const string Wav = "audio/x-wav";

    /// <summary>Detects a MIME type from the file signature.</summary>
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return null;
        if (data[0] == 'B' && data[1] == 'M') return Bmp;
        if (data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G') return Png;
        if (data[0] == 0xFF && data[1] == 0xD8) return Jpeg;
        if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S') return Ogg;
        if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F') return Wav;
        if (data[0] == 'I' && data[1] == 'D' && data[2] == '3') return Mp3;
        if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0) return Mp3;
        return null;
    }

    /// <summary>Guesses a MIME type from a file name extension.</summary>
    public static string? FromFileName(string? fileName) =>
        Path.GetExtension(fileName ?? string.Empty).ToUpperInvariant() switch
        {
            ".BMP" => Bmp,
            ".PNG" => Png,
            ".JPG" or ".JPEG" => Jpeg,
            ".MP3" => Mp3,
            ".OGG" or ".OGA" => Ogg,
            ".WAV" => Wav,
            _ => null,
        };

    /// <summary>Returns the kind of a MIME type.</summary>
    public static MediaKind KindOf(string mimeType) => mimeType switch
    {
        Bmp or Png or Jpeg => MediaKind.Image,
        Mp3 or "audio/mp3" or Ogg or Wav or "audio/wav" => MediaKind.Audio,
        _ when mimeType.StartsWith("image/", StringComparison.Ordinal) => MediaKind.Image,
        _ when mimeType.StartsWith("audio/", StringComparison.Ordinal) => MediaKind.Audio,
        _ => MediaKind.Unknown,
    };

    /// <summary>Returns the usual file extension (with dot) of a MIME type.</summary>
    public static string ExtensionOf(string mimeType) => mimeType switch
    {
        Bmp => ".bmp",
        Png => ".png",
        Jpeg => ".jpg",
        Mp3 or "audio/mp3" => ".mp3",
        Ogg => ".ogg",
        Wav or "audio/wav" => ".wav",
        _ => string.Empty,
    };
}
