using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace Studio.Device.Raw;

/// <summary>
/// Desktop <see cref="IUsbTransport"/> based on libusb (LibUsbDotNet). On Windows the device must use the
/// WinUSB/libusb driver; on Linux a udev rule may be needed (see README).
/// </summary>
public sealed class LibUsbTransport : IUsbTransport
{
    /// <summary>USB ids of firmware v1 devices (vendor, product).</summary>
    public static readonly IReadOnlyList<(int Vendor, int Product)> V1Ids = [(0x0c45, 0x6820), (0x0c45, 0x6840)];

    /// <summary>USB ids of firmware v2/v3 devices.</summary>
    public static readonly (int Vendor, int Product) V2V3Id = (0x0483, 0xa341);

    private const int TimeoutMs = 5000;
    private readonly UsbContext _context;
    private readonly IUsbDevice _device;
    private readonly UsbEndpointReader _reader;
    private readonly UsbEndpointWriter _writer;

    private LibUsbTransport(UsbContext context, IUsbDevice device)
    {
        _context = context;
        _device = device;
        _reader = device.OpenEndpointReader(ReadEndpointID.Ep01);
        _writer = device.OpenEndpointWriter(WriteEndpointID.Ep02);
    }

    /// <summary>Opens the first connected v1 device, or returns null.</summary>
    public static LibUsbTransport? TryOpenV1()
    {
        UsbContext? context = null;
        IUsbDevice? device = null;
        try
        {
            context = new UsbContext();
            device = context.List().FirstOrDefault(d => V1Ids.Contains((d.VendorId, d.ProductId)));
            if (device is null || !device.TryOpen()) return null;
            device.ClaimInterface(0);
            var transport = new LibUsbTransport(context, device);
            // Ownership moves to the transport.
            (context, device) = (null, null);
            return transport;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or UsbException)
        {
            // libusb is not installed: v1 devices are simply not detected.
            return null;
        }
        finally
        {
            device?.Dispose();
            context?.Dispose();
        }
    }

    /// <inheritdoc />
    public Task<int> BulkOutAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var buffer = data.ToArray();
            var error = _writer.Write(buffer, TimeoutMs, out var transferred);
            if (error != Error.Success) throw new DeviceException($"Erreur USB en écriture : {error}.");
            return transferred;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<int> BulkInAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var tmp = new byte[buffer.Length];
            var error = _reader.Read(tmp, TimeoutMs, out var transferred);
            if (error != Error.Success) throw new DeviceException($"Erreur USB en lecture : {error}.");
            tmp.AsSpan(0, transferred).CopyTo(buffer.Span);
            return transferred;
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        try
        {
            _device.ReleaseInterface(0);
        }
        catch (UsbException)
        {
            // Device already unplugged.
        }
        _device.Dispose();
        _context.Dispose();
        return ValueTask.CompletedTask;
    }
}
