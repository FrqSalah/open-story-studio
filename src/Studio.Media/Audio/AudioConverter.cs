using System.ComponentModel;
using System.Diagnostics;
using Studio.Formats.Assets;
using Studio.Formats.Raw;

namespace Studio.Media.Audio;

/// <summary>Converts audio files to the formats expected by the devices.</summary>
public interface IAudioTranscoder
{
    /// <summary>True when arbitrary input formats can be converted (otherwise only compliant files are accepted).</summary>
    bool CanTranscode { get; }

    /// <summary>Converts to MP3 mono 44.1 kHz without ID3 tags (v2/v3 devices).</summary>
    Task<byte[]> ToDeviceMp3Async(byte[] input, CancellationToken cancellationToken = default);

    /// <summary>Converts to WAVE PCM 16-bit mono 32 kHz (v1 devices).</summary>
    Task<byte[]> ToV1WaveAsync(byte[] input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Transcoder that only accepts files that are already compliant (after removing ID3 tags).
/// Used where ffmpeg is not available (Android, browser).
/// </summary>
public sealed class PassthroughAudioTranscoder : IAudioTranscoder
{
    /// <inheritdoc />
    public bool CanTranscode => false;

    /// <inheritdoc />
    public Task<byte[]> ToDeviceMp3Async(byte[] input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var stripped = AssetInspector.StripId3(input);
        if (AssetInspector.CheckDeviceAudio(stripped) is { } problem)
        {
            throw new MediaConversionException(
                $"Conversion audio impossible sur cet appareil : {problem}. Convertissez le fichier sur PC (avec ffmpeg) ou fournissez un MP3 mono 44,1 kHz.");
        }
        return Task.FromResult(stripped);
    }

    /// <inheritdoc />
    public Task<byte[]> ToV1WaveAsync(byte[] input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (RawPackFormat.CheckWave(input) is { } problem)
        {
            throw new MediaConversionException($"Conversion audio impossible sur cet appareil : {problem}.");
        }
        return Task.FromResult(input);
    }
}

/// <summary>Transcoder backed by the ffmpeg command line tool.</summary>
public sealed class FfmpegAudioTranscoder : IAudioTranscoder
{
    private readonly string _ffmpeg;

    /// <summary>Creates a transcoder using the given ffmpeg executable.</summary>
    public FfmpegAudioTranscoder(string ffmpegPath) => _ffmpeg = ffmpegPath;

    /// <inheritdoc />
    public bool CanTranscode => true;

    /// <summary>
    /// Looks for ffmpeg in <c>STUDIO_FFMPEG</c>, next to the application, then in the PATH.
    /// Returns a passthrough transcoder when it cannot be found.
    /// </summary>
    public static IAudioTranscoder CreateDefault()
    {
        var path = Find();
        return path is null ? new PassthroughAudioTranscoder() : new FfmpegAudioTranscoder(path);
    }

    /// <summary>Path of the ffmpeg executable, or null.</summary>
    public static string? Find()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        if (Environment.GetEnvironmentVariable("STUDIO_FFMPEG") is { Length: > 0 } env && File.Exists(env)) return env;

        var local = Path.Join(AppContext.BaseDirectory, exe);
        if (File.Exists(local)) return local;

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return directories.Select(dir => Path.Join(dir.Trim('"'), exe)).FirstOrDefault(File.Exists);
    }

    /// <inheritdoc />
    public async Task<byte[]> ToDeviceMp3Async(byte[] input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var stripped = AssetInspector.StripId3(input);
        if (AssetInspector.CheckDeviceAudio(stripped) is null) return stripped;

        var output = await RunAsync(input,
            ["-ac", "1", "-ar", AssetInspector.Mp3SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
             "-c:a", "libmp3lame", "-b:a", "96k", "-write_xing", "0", "-id3v2_version", "0", "-f", "mp3"],
            cancellationToken).ConfigureAwait(false);
        return AssetInspector.StripId3(output);
    }

    /// <inheritdoc />
    public async Task<byte[]> ToV1WaveAsync(byte[] input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (RawPackFormat.CheckWave(input) is null) return input;
        return await RunAsync(input,
            ["-ac", "1", "-ar", RawPackFormat.WaveSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
             "-c:a", "pcm_s16le", "-bitexact", "-f", "wav"],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> RunAsync(byte[] input, string[] outputArgs, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string[] inputArgs = ["-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-vn", "-map_metadata", "-1"];
        foreach (var a in inputArgs.Concat(outputArgs)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("pipe:1");

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new MediaConversionException("Impossible de lancer ffmpeg.");
        }
        catch (Win32Exception e)
        {
            throw new MediaConversionException("Impossible de lancer ffmpeg.", e);
        }

        using (process)
        {
            using var output = new MemoryStream();
            var copyOut = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            var readErr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await copyOut.ConfigureAwait(false);
            var errors = await readErr.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || output.Length == 0)
            {
                throw new MediaConversionException($"ffmpeg a échoué : {errors.Trim()}");
            }
            return output.ToArray();
        }
    }
}
