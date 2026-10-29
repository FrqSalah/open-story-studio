using Microsoft.Extensions.Logging;
using Studio.App.Services;
using Studio.Device;
using Studio.Formats.Library;
using Studio.Media;
using Studio.Media.Audio;
using Studio.UI;
using Studio.UI.Services;

namespace Studio.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        builder.Services.AddMauiBlazorWebView();

        // Library and backups live in the app data folder (shared with the CLI on Windows).
        var library = OperatingSystem.IsWindows() ? FolderPackLibrary.DefaultFolder : Path.Combine(FileSystem.AppDataDirectory, "library");
        var options = new DeviceManagerOptions
        {
            AllowExperimentalWrites = Preferences.Default.Get(nameof(IDeviceService.AllowExperimentalWrites), false),
        };
        if (!OperatingSystem.IsWindows()) options.BackupFolder = Path.Combine(FileSystem.AppDataDirectory, "backups");

        builder.Services.AddSingleton<IPackLibrary>(new FolderPackLibrary(library));
        builder.Services.AddSingleton<IFileSaver, ShareFileSaver>();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IAudioTranscoder>(_ => FfmpegAudioTranscoder.CreateDefault());
        builder.Services.AddSingleton<DeviceAssetPreparer>();
        builder.Services.AddSingleton<DeviceManager>();
#if ANDROID
        builder.Services.AddSingleton<IDevicePicker, Platforms.Droid.SafDevicePicker>();
        builder.Services.AddSingleton<IUsbDeviceProvider, Platforms.Droid.AndroidUsbDeviceProvider>();
#elif WINDOWS
        builder.Services.AddSingleton<IDevicePicker, Platforms.WinUI.WindowsDevicePicker>();
        builder.Services.AddSingleton<IUsbDeviceProvider, Platforms.WinUI.LibUsbDeviceProvider>();
#endif
        builder.Services.AddSingleton<IDeviceService, AppDeviceService>();
        builder.Services.AddStudioUI();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
