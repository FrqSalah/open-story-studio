using Studio.Core.Model;
using Studio.Formats.DeviceFs;

namespace Studio.Device;

/// <summary>Error raised by device operations, with a message meant for the user.</summary>
public sealed class DeviceException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DeviceException() { }

    /// <summary>Creates the exception.</summary>
    public DeviceException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public DeviceException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>General information about a connected device.</summary>
/// <param name="Generation">Hardware generation.</param>
/// <param name="Firmware">Firmware version.</param>
/// <param name="SerialNumber">Serial number, if known.</param>
/// <param name="TotalBytes">Storage capacity, if known.</param>
/// <param name="FreeBytes">Free space, if known.</param>
/// <param name="Location">Where the device is (drive, mount point, USB).</param>
/// <param name="IsWriteSupported">False when writing is disabled for safety (untested firmware).</param>
public sealed record DeviceInfo(
    DeviceGeneration Generation,
    string Firmware,
    string? SerialNumber,
    long? TotalBytes,
    long? FreeBytes,
    string Location,
    bool IsWriteSupported);

/// <summary>A pack installed on the device.</summary>
/// <param name="Id">Pack id.</param>
/// <param name="Version">Pack version.</param>
/// <param name="SizeBytes">Size on the device.</param>
/// <param name="NightMode">Night mode available.</param>
/// <param name="IsValid">False when the pack folder is missing or unreadable.</param>
public sealed record InstalledPack(Guid Id, short Version, long SizeBytes, bool NightMode, bool IsValid = true);

/// <summary>Step of a transfer.</summary>
public enum TransferPhase
{
    /// <summary>Converting images and sounds.</summary>
    Converting,
    /// <summary>Saving the device index.</summary>
    BackingUp,
    /// <summary>Copying files.</summary>
    Writing,
    /// <summary>Reading back and comparing.</summary>
    Verifying,
    /// <summary>Updating the device index.</summary>
    Committing,
    /// <summary>Done.</summary>
    Done,
}

/// <summary>Progress of a transfer.</summary>
/// <param name="Phase">Current step.</param>
/// <param name="Fraction">0..1 within the whole transfer.</param>
public readonly record struct TransferProgress(TransferPhase Phase, double Fraction);

/// <summary>
/// A connected story teller device. Packs passed to <see cref="InstallAsync"/> must be device-ready
/// (see <see cref="Media.DeviceAssetPreparer"/>); <see cref="DeviceManager"/> does the conversion.
/// </summary>
public interface IStoryDevice
{
    /// <summary>Hardware generation.</summary>
    DeviceGeneration Generation { get; }

    /// <summary>Reads device information.</summary>
    Task<DeviceInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists installed packs in device order.</summary>
    Task<IReadOnlyList<InstalledPack>> ListPacksAsync(CancellationToken cancellationToken = default);

    /// <summary>Installs (or replaces) a device-ready pack. The device stays consistent if the transfer fails.</summary>
    Task InstallAsync(StoryPack pack, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a pack.</summary>
    Task RemoveAsync(Guid packId, CancellationToken cancellationToken = default);

    /// <summary>Reorders packs. <paramref name="order"/> must contain exactly the installed pack ids.</summary>
    Task ReorderAsync(IReadOnlyList<Guid> order, CancellationToken cancellationToken = default);

    /// <summary>Reads a pack back from the device (not supported on every generation).</summary>
    Task<StoryPack> ExportAsync(Guid packId, CancellationToken cancellationToken = default);

    /// <summary>Raw content of the device index files, saved before every write.</summary>
    Task<IReadOnlyDictionary<string, byte[]>> SnapshotIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores index files saved with <see cref="SnapshotIndexAsync"/>.</summary>
    Task RestoreIndexAsync(IReadOnlyDictionary<string, byte[]> snapshot, CancellationToken cancellationToken = default);
}
