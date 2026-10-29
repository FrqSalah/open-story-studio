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
    private const int PickTreeRequest = 4210;
    private static TaskCompletionSource<Android.Net.Uri?>? _pickTree;

    /// <summary>Opens the Storage Access Framework folder picker and returns the chosen tree URI.</summary>
    public static Task<Android.Net.Uri?> PickTreeAsync()
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No current activity.");
        _pickTree?.TrySetResult(null);
        _pickTree = new TaskCompletionSource<Android.Net.Uri?>();
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        activity.StartActivityForResult(intent, PickTreeRequest);
        return _pickTree.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickTreeRequest) return;
        var uri = resultCode == Result.Ok ? data?.Data : null;
        if (uri is not null)
        {
            ContentResolver?.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        _pickTree?.TrySetResult(uri);
        _pickTree = null;
    }
}
