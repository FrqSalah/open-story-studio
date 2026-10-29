using Studio.Core.Model;
using Studio.Device;
using Studio.Device.Raw;
using Studio.Device.Storage;
using Studio.UI.Services;

namespace Studio.App.Services;

/// <summary>Lets the user pick the device root folder (Android SAF, Windows folder picker).</summary>
public interface IDevicePicker
{
    /// <summary>Shows the picker. Returns null when cancelled.</summary>
    Task<IDeviceStorage?> PickAsync(CancellationToken cancellationToken = default);

    /// <summary>Reopens the last picked folder, if its permission is still granted.</summary>
    IDeviceStorage? Restore();
}

/// <summary>Opens firmware v1 devices over USB on this platform.</summary>
public interface IUsbDeviceProvider
{
    /// <summary>Opens a connected v1 device, or returns null.</summary>
    Task<IUsbTransport?> OpenV1Async(CancellationToken cancellationToken = default);
}

/// <summary><see cref="IDeviceService"/> backed by <see cref="DeviceManager"/>.</summary>
public sealed class AppDeviceService(DeviceManager manager, IDevicePicker picker, IUsbDeviceProvider usb) : IDeviceService
{
    private IStoryDevice? _device;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public bool IsConnected => _device is not null;

    /// <inheritdoc />
    public string ConnectHint => OperatingSystem.IsAndroid()
        ? "Branchez la conteuse avec un câble USB OTG, allumez-la, puis choisissez son stockage (« Choisir le dossier… »)."
        : "Branchez et allumez la conteuse : elle apparaît comme un lecteur (v2/v3) ou par USB (v1, pilote libusb requis).";

    /// <inheritdoc />
    public bool AllowExperimentalWrites
    {
        get => manager.Options.AllowExperimentalWrites;
        set
        {
            manager.Options.AllowExperimentalWrites = value;
            Preferences.Default.Set(nameof(AllowExperimentalWrites), value);
        }
    }

    /// <inheritdoc />
    public async Task<DeviceSummary?> ConnectAsync(CancellationToken cancellationToken = default)
    {
        _device = null;
        if (picker.Restore() is { } storage)
        {
            _device = await TryOpenAsync(storage, cancellationToken);
        }
        _device ??= OperatingSystem.IsAndroid() ? null : await manager.DetectAsync(cancellationToken);
        if (_device is null && await usb.OpenV1Async(cancellationToken) is { } transport)
        {
            _device = new RawStoryDevice(new RawSdCard(transport), "USB", AllowExperimentalWrites);
        }
        return _device is null ? null : await SummaryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DeviceSummary?> PickAndConnectAsync(CancellationToken cancellationToken = default)
    {
        var storage = await picker.PickAsync(cancellationToken);
        if (storage is null) return null;
        _device = await TryOpenAsync(storage, cancellationToken);
        return _device is null ? null : await SummaryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DevicePack>> ListPacksAsync(CancellationToken cancellationToken = default) =>
        (await Device.ListPacksAsync(cancellationToken))
            .Select(p => new DevicePack(p.Id, p.Version, p.SizeBytes, p.NightMode, p.IsValid))
            .ToList();

    /// <inheritdoc />
    public Task InstallAsync(StoryPack pack, IProgress<(string Phase, double Fraction)>? progress = null, CancellationToken cancellationToken = default)
    {
        var mapped = new Progress<TransferProgress>(p => progress?.Report((Phase(p.Phase), p.Fraction)));
        return Task.Run(() => manager.InstallAsync(Device, pack, mapped, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await manager.BackupAsync(Device, cancellationToken);
        await Device.RemoveAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReorderAsync(IReadOnlyList<Guid> order, CancellationToken cancellationToken = default)
    {
        await manager.BackupAsync(Device, cancellationToken);
        await Device.ReorderAsync(order, cancellationToken);
    }

    /// <inheritdoc />
    public Task<StoryPack> ExportAsync(Guid id, CancellationToken cancellationToken = default) => Device.ExportAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<string> BackupAsync(CancellationToken cancellationToken = default) => manager.BackupAsync(Device, cancellationToken);

    private IStoryDevice Device => _device ?? throw new DeviceException("Aucun appareil connecté.");

    private async Task<IStoryDevice?> TryOpenAsync(IDeviceStorage storage, CancellationToken cancellationToken)
    {
        try
        {
            return await manager.OpenAsync(storage, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException || IsPlatformIoError(e))
        {
            return null;
        }
    }

    private async Task<DeviceSummary> SummaryAsync(CancellationToken cancellationToken)
    {
        var i = await Device.GetInfoAsync(cancellationToken);
        return new DeviceSummary(i.Generation.ToString(), i.Firmware, i.SerialNumber, i.TotalBytes, i.FreeBytes, i.Location, i.IsWriteSupported);
    }

    private static bool IsPlatformIoError(Exception e) =>
#if ANDROID
        e is Java.Lang.Exception;
#else
        e is System.Runtime.InteropServices.COMException;
#endif

    private static string Phase(TransferPhase phase) => phase switch
    {
        TransferPhase.Converting => "Conversion des images et des sons",
        TransferPhase.BackingUp => "Sauvegarde de l'index",
        TransferPhase.Writing => "Copie sur l'appareil",
        TransferPhase.Verifying => "Vérification",
        TransferPhase.Committing => "Finalisation",
        _ => "Terminé",
    };
}

