using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Studio.Core.Model;
using Studio.Core.Validation;
using Studio.Device.Fs;
using Studio.Device.Raw;
using Studio.Device.Storage;
using Studio.Media;

namespace Studio.Device;

/// <summary>Options of the <see cref="DeviceManager"/>.</summary>
public sealed class DeviceManagerOptions
{
    /// <summary>Allow writing to v1 and v3 devices (not validated on real hardware).</summary>
    public bool AllowExperimentalWrites { get; set; }

    /// <summary>Folder where index backups are kept (one sub folder per device).</summary>
    public string BackupFolder { get; set; } = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenStoryStudio", "backups");
}

/// <summary>
/// Entry point for device operations: detection, then validation → conversion → backup → transfer.
/// </summary>
[UnsupportedOSPlatform("browser")]
public sealed partial class DeviceManager(DeviceAssetPreparer preparer, DeviceManagerOptions options, ILogger<DeviceManager>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<DeviceManager>.Instance;

    /// <summary>Options in use.</summary>
    public DeviceManagerOptions Options => options;

    /// <summary>Opens a device from a mounted folder (v2/v3), or returns null if it is not a story teller.</summary>
    public async Task<IStoryDevice?> OpenAsync(IDeviceStorage storage, CancellationToken cancellationToken = default) =>
        await FsStoryDevice.TryOpenAsync(storage, options.AllowExperimentalWrites, cancellationToken).ConfigureAwait(false);

    /// <summary>Looks for a connected device: mounted drives first (v2/v3), then libusb (v1).</summary>
    public async Task<IStoryDevice?> DetectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var root in MountedRoots())
        {
            try
            {
                if (await OpenAsync(new LocalDeviceStorage(root), cancellationToken).ConfigureAwait(false) is { } device)
                {
                    LogFound(_logger, root);
                    return device;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogCannotInspect(_logger, e, root);
            }
        }

        if (!OperatingSystem.IsAndroid() && LibUsbTransport.TryOpenV1() is { } usb)
        {
            LogFound(_logger, "USB");
            return new RawStoryDevice(new RawSdCard(usb), "USB", options.AllowExperimentalWrites);
        }
        return null;
    }

    /// <summary>
    /// Validates, converts and installs a library pack. The device index is backed up first.
    /// </summary>
    /// <exception cref="DeviceException">Validation, conversion or transfer failed.</exception>
    public async Task InstallAsync(IStoryDevice device, StoryPack pack, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(pack);

        var errors = PackValidator.Validate(pack).Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new DeviceException("Le pack contient des erreurs : " + string.Join(" ; ", errors.Select(e => e.Message)));
        }

        StoryPack ready;
        try
        {
            var conversion = new Relay<double>(f => progress?.Report(new(TransferPhase.Converting, f * 0.3)));
            ready = await preparer.PrepareAsync(pack, device.Generation, conversion, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaConversionException e)
        {
            throw new DeviceException(e.Message, e);
        }

        await BackupAsync(device, cancellationToken).ConfigureAwait(false);
        var transfer = new Relay<TransferProgress>(p => progress?.Report(p with { Fraction = 0.3 + p.Fraction * 0.7 }));
        await device.InstallAsync(ready, transfer, cancellationToken).ConfigureAwait(false);
        LogInstalled(_logger, pack.Id);
    }

    /// <summary>Saves the device index files to the backup folder and returns the backup path.</summary>
    public async Task<string> BackupAsync(IStoryDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var info = await device.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await device.SnapshotIndexAsync(cancellationToken).ConfigureAwait(false);
        var folder = Path.Join(options.BackupFolder, Sanitize(info.SerialNumber ?? "inconnu"), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        foreach (var (name, data) in snapshot)
        {
            await File.WriteAllBytesAsync(Path.Join(folder, Sanitize(name)), data, cancellationToken).ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(Path.Join(folder, "device.json"), JsonSerializer.Serialize(info), cancellationToken).ConfigureAwait(false);
        return folder;
    }

    /// <summary>Lists the backups of a device, most recent first.</summary>
    public IReadOnlyList<string> ListBackups(string? serialNumber)
    {
        var folder = Path.Join(options.BackupFolder, Sanitize(serialNumber ?? "inconnu"));
        return Directory.Exists(folder) ? Directory.GetDirectories(folder).OrderDescending(StringComparer.Ordinal).ToList() : [];
    }

    /// <summary>Restores the index files from a backup folder.</summary>
    public async Task RestoreAsync(IStoryDevice device, string backupFolder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(backupFolder).Where(f => !f.EndsWith(".json", StringComparison.Ordinal)))
        {
            snapshot[Unsanitize(Path.GetFileName(file))] = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }
        await device.RestoreIndexAsync(snapshot, cancellationToken).ConfigureAwait(false);
        LogRestored(_logger, backupFolder);
    }

    /// <summary>Synchronous progress forwarder (keeps reports ordered, unlike <see cref="Progress{T}"/>).</summary>
    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Story teller found on {Location}")]
    private static partial void LogFound(ILogger logger, string location);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cannot inspect {Root}")]
    private static partial void LogCannotInspect(ILogger logger, Exception exception, string root);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pack {PackId} installed")]
    private static partial void LogInstalled(ILogger logger, Guid packId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device index restored from {Folder}")]
    private static partial void LogRestored(ILogger logger, string folder);

    private static IEnumerable<string> MountedRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = drive.IsReady && drive.DriveType is DriveType.Removable or DriveType.Fixed or DriveType.Unknown;
            }
            catch (IOException)
            {
                continue;
            }
            if (ready) yield return drive.RootDirectory.FullName;
        }
    }

    // Index file names start with a dot: keep them visible in backups.
    private static string Sanitize(string name) => name.StartsWith('.') ? "dot" + name[1..] : string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static string Unsanitize(string name) => name.StartsWith("dot", StringComparison.Ordinal) && !name.Contains('-', StringComparison.Ordinal) ? "." + name[3..] : name;
}
