using Studio.App.Services;
using Studio.Device.Raw;
using Studio.Device.Storage;
using Windows.Storage.Pickers;

namespace Studio.App.Platforms.WinUI;

/// <summary>Folder picker for the device drive.</summary>
public sealed class WindowsDevicePicker : IDevicePicker
{
    private const string LastFolderKey = "device.lastFolder";

    /// <inheritdoc />
    public async Task<IDeviceStorage?> PickAsync(CancellationToken cancellationToken = default)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        var windows = Application.Current?.Windows;
        var window = windows is { Count: > 0 } ? windows[0].Handler?.PlatformView as MauiWinUIWindow : null;
        if (window is not null) WinRT.Interop.InitializeWithWindow.Initialize(picker, window.WindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return null;
        Preferences.Default.Set(LastFolderKey, folder.Path);
        return new LocalDeviceStorage(folder.Path);
    }

    /// <inheritdoc />
    public IDeviceStorage? Restore() =>
        Preferences.Default.Get<string?>(LastFolderKey, null) is { } path && File.Exists(Path.Join(path, ".md"))
            ? new LocalDeviceStorage(path)
            : null;
}

/// <summary>libusb access to v1 devices (requires the WinUSB/libusb driver).</summary>
public sealed class LibUsbDeviceProvider : IUsbDeviceProvider
{
    /// <inheritdoc />
    public Task<IUsbTransport?> OpenV1Async(CancellationToken cancellationToken = default) =>
        Task.Run<IUsbTransport?>(LibUsbTransport.TryOpenV1, cancellationToken);
}
