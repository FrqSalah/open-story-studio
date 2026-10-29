using Studio.Core.Model;

namespace Studio.UI.Services;

/// <summary>Device information shown by the UI.</summary>
public sealed record DeviceSummary(
    string Generation,
    string Firmware,
    string? SerialNumber,
    long? TotalBytes,
    long? FreeBytes,
    string Location,
    bool IsWriteSupported);

/// <summary>A pack installed on the device, as shown by the UI.</summary>
public sealed record DevicePack(Guid Id, short Version, long SizeBytes, bool NightMode, bool IsValid);

/// <summary>
/// Device access offered by the host. The browser edition has none (<see cref="IsSupported"/> is false);
/// the desktop/Android app implements it with <c>Studio.Device</c>.
/// </summary>
public interface IDeviceService
{
    /// <summary>False when the host cannot talk to devices (browser).</summary>
    bool IsSupported { get; }

    /// <summary>Whether a device is currently open.</summary>
    bool IsConnected { get; }

    /// <summary>Text explaining how to connect on this platform.</summary>
    string ConnectHint { get; }

    /// <summary>Detects and opens a device. Returns null when none is found.</summary>
    Task<DeviceSummary?> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Lets the user pick the device folder (Android SAF, desktop folder picker), when the platform supports it.</summary>
    Task<DeviceSummary?> PickAndConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists installed packs.</summary>
    Task<IReadOnlyList<DevicePack>> ListPacksAsync(CancellationToken cancellationToken = default);

    /// <summary>Converts and installs a pack.</summary>
    Task InstallAsync(StoryPack pack, IProgress<(string Phase, double Fraction)>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a pack (index backed up first).</summary>
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Reorders packs (index backed up first).</summary>
    Task ReorderAsync(IReadOnlyList<Guid> order, CancellationToken cancellationToken = default);

    /// <summary>Reads a pack back from the device.</summary>
    Task<StoryPack> ExportAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Backs up the device index and returns where.</summary>
    Task<string> BackupAsync(CancellationToken cancellationToken = default);

    /// <summary>Allows writing on v1/v3 devices.</summary>
    bool AllowExperimentalWrites { get; set; }
}

/// <summary>Device service of hosts without device access.</summary>
public sealed class UnsupportedDeviceService : IDeviceService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public bool IsConnected => false;

    /// <inheritdoc />
    public string ConnectHint => "Le transfert vers l'appareil nécessite l'application Windows ou Android : le navigateur ne peut pas écrire sur une conteuse.";

    /// <inheritdoc />
    public bool AllowExperimentalWrites { get; set; }

    /// <inheritdoc />
    public Task<DeviceSummary?> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult<DeviceSummary?>(null);

    /// <inheritdoc />
    public Task<DeviceSummary?> PickAndConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult<DeviceSummary?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<DevicePack>> ListPacksAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DevicePack>>([]);

    /// <inheritdoc />
    public Task InstallAsync(StoryPack pack, IProgress<(string Phase, double Fraction)>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException(ConnectHint);

    /// <inheritdoc />
    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException(ConnectHint);

    /// <inheritdoc />
    public Task ReorderAsync(IReadOnlyList<Guid> order, CancellationToken cancellationToken = default) => throw new NotSupportedException(ConnectHint);

    /// <inheritdoc />
    public Task<StoryPack> ExportAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException(ConnectHint);

    /// <inheritdoc />
    public Task<string> BackupAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(ConnectHint);
}

/// <summary>Saving files for the user (browser download, or app documents folder / share sheet).</summary>
public interface IFileSaver
{
    /// <summary>Saves a file and returns a description of where it went.</summary>
    Task<string> SaveAsync(string fileName, string mimeType, byte[] data, CancellationToken cancellationToken = default);
}
