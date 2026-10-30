using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Usb;

namespace Studio.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter([UsbManager.ActionUsbDeviceAttached])]
[MetaData(UsbManager.ActionUsbDeviceAttached, Resource = "@xml/device_filter")]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>Opens the Storage Access Framework folder picker and returns the chosen tree URI.</summary>
    public static Task<Android.Net.Uri?> PickTreeAsync() => TreePicker.Start();

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != TreePicker.RequestCode) return;
        var uri = resultCode == Result.Ok ? data?.Data : null;
        if (uri is not null)
        {
            ContentResolver?.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        TreePicker.Complete(uri);
    }
}

/// <summary>Bridges <c>ACTION_OPEN_DOCUMENT_TREE</c> (activity result) to a task.</summary>
internal static class TreePicker
{
    public const int RequestCode = 4210;
    private static TaskCompletionSource<Android.Net.Uri?>? _pending;

    public static Task<Android.Net.Uri?> Start()
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No current activity.");
        _pending?.TrySetResult(null);
        _pending = new TaskCompletionSource<Android.Net.Uri?>();
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        activity.StartActivityForResult(intent, RequestCode);
        return _pending.Task;
    }

    public static void Complete(Android.Net.Uri? uri)
    {
        _pending?.TrySetResult(uri);
        _pending = null;
    }
}
