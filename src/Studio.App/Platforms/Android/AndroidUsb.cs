using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Studio.App.Services;
using Studio.Device.Raw;

namespace Studio.App.Platforms.Droid;

/// <summary>Opens firmware v1 devices with the Android USB host API (USB OTG cable).</summary>
public sealed class AndroidUsbDeviceProvider : IUsbDeviceProvider
{
    private const string PermissionAction = "io.github.frqsalah.openstorystudio.USB_PERMISSION";

    /// <inheritdoc />
    public async Task<IUsbTransport?> OpenV1Async(CancellationToken cancellationToken = default)
    {
        var context = Platform.AppContext;
        if (context.GetSystemService(Context.UsbService) is not UsbManager manager) return null;
        var device = manager.DeviceList?.Values.FirstOrDefault(d => LibUsbTransport.V1Ids.Contains((d.VendorId, d.ProductId)));
        if (device is null) return null;

        if (!manager.HasPermission(device) && !await RequestPermissionAsync(context, manager, device, cancellationToken)) return null;

        var connection = manager.OpenDevice(device);
        var usbInterface = device.InterfaceCount > 0 ? device.GetInterface(0) : null;
        if (connection is null || usbInterface is null) return null;
        if (!connection.ClaimInterface(usbInterface, true))
        {
            connection.Close();
            return null;
        }

        UsbEndpoint? input = null, output = null;
        for (var i = 0; i < usbInterface.EndpointCount; i++)
        {
            var endpoint = usbInterface.GetEndpoint(i);
            if (endpoint?.Type != UsbAddressing.XferBulk) continue;
            if (endpoint.Direction == UsbAddressing.In) input = endpoint;
            else output = endpoint;
        }
        if (input is null || output is null)
        {
            connection.Close();
            return null;
        }
        return new AndroidUsbTransport(connection, usbInterface, input, output);
    }

    private static async Task<bool> RequestPermissionAsync(Context context, UsbManager manager, UsbDevice device, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<bool>();
        var receiver = new PermissionReceiver(result);
        context.RegisterReceiver(receiver, new IntentFilter(PermissionAction), ReceiverFlags.NotExported);
        try
        {
            var intent = new Intent(PermissionAction).SetPackage(context.PackageName);
            var pending = PendingIntent.GetBroadcast(context, 0, intent, PendingIntentFlags.Mutable);
            manager.RequestPermission(device, pending);
            using (cancellationToken.Register(() => result.TrySetCanceled()))
            {
                return await result.Task;
            }
        }
        finally
        {
            context.UnregisterReceiver(receiver);
        }
    }

    private sealed class PermissionReceiver(TaskCompletionSource<bool> result) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) =>
            result.TrySetResult(intent?.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false) == true);
    }
}

/// <summary>Bulk transfers through <see cref="UsbDeviceConnection"/>.</summary>
public sealed class AndroidUsbTransport(UsbDeviceConnection connection, UsbInterface usbInterface, UsbEndpoint input, UsbEndpoint output) : IUsbTransport
{
    private const int TimeoutMs = 5000;

    /// <inheritdoc />
    public Task<int> BulkOutAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var buffer = data.ToArray();
        var sent = 0;
        while (sent < buffer.Length)
        {
            var n = connection.BulkTransfer(output, buffer, sent, buffer.Length - sent, TimeoutMs);
            if (n < 0) throw new Studio.Device.DeviceException("Erreur USB en écriture.");
            sent += n;
        }
        return sent;
    }, cancellationToken);

    /// <inheritdoc />
    public Task<int> BulkInAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var tmp = new byte[buffer.Length];
        var n = connection.BulkTransfer(input, tmp, tmp.Length, TimeoutMs);
        if (n < 0) throw new Studio.Device.DeviceException("Erreur USB en lecture.");
        tmp.AsSpan(0, n).CopyTo(buffer.Span);
        return n;
    }, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        connection.ReleaseInterface(usbInterface);
        connection.Close();
        return ValueTask.CompletedTask;
    }
}
