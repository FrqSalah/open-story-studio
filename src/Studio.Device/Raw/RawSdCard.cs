using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Studio.Device.Raw;

/// <summary>Bulk endpoints of a USB device (implemented with libusb on desktop and UsbManager on Android).</summary>
public interface IUsbTransport : IAsyncDisposable
{
    /// <summary>Sends data on the bulk OUT endpoint.</summary>
    Task<int> BulkOutAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Receives data from the bulk IN endpoint.</summary>
    Task<int> BulkInAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}

/// <summary>Sector-level access to the SD card and SPI flash of a firmware v1 device.</summary>
public interface IRawSdAccess
{
    /// <summary>Reads SD sectors.</summary>
    Task<byte[]> ReadSdAsync(int sector, int count, CancellationToken cancellationToken = default);

    /// <summary>Writes SD sectors (<paramref name="data"/> length must be a multiple of 512).</summary>
    Task WriteSdAsync(int sector, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Reads SPI flash sectors.</summary>
    Task<byte[]> ReadSpiAsync(int offset, int count, CancellationToken cancellationToken = default);
}

/// <summary>
/// USB mass storage Bulk-Only Transport (CBW/CSW) carrying the vendor-specific SCSI commands of v1 devices.
/// </summary>
/// <remarks>Ported from STUdio's <c>LibUsbMassStorageHelper</c> (MPL-2.0).</remarks>
public sealed class RawSdCard(IUsbTransport usb) : IRawSdAccess, IDisposable
{
    /// <summary>Sector size.</summary>
    public const int SectorSize = 512;

    private const int CbwLength = 31;
    private const int CswLength = 13;
    private static readonly byte[] ReadSpiCommand = [0xf6, 0x05, 0x06];
    private static readonly byte[] ReadSdCommand = [0xf6, 0xe1, 0x00];
    private static readonly byte[] WriteSdCommand = [0xf6, 0xe2, 0x00];

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    /// <inheritdoc />
    public Task<byte[]> ReadSdAsync(int sector, int count, CancellationToken cancellationToken = default) =>
        ReadAsync(ReadSdCommand, sector, count, cancellationToken);

    /// <inheritdoc />
    public Task<byte[]> ReadSpiAsync(int offset, int count, CancellationToken cancellationToken = default) =>
        ReadAsync(ReadSpiCommand, offset, count, cancellationToken);

    /// <inheritdoc />
    public async Task WriteSdAsync(int sector, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length == 0 || data.Length % SectorSize != 0) throw new ArgumentException("Data must be whole sectors.", nameof(data));
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var count = data.Length / SectorSize;
            await usb.BulkOutAsync(Cbw(WriteSdCommand, sector, (short)count, inbound: false), cancellationToken).ConfigureAwait(false);
            await usb.BulkOutAsync(data, cancellationToken).ConfigureAwait(false);
            await ReadCswAsync("écriture SD", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<byte[]> ReadAsync(ReadOnlyMemory<byte> command, int address, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await usb.BulkOutAsync(Cbw(command.Span, address, (short)count, inbound: true), cancellationToken).ConfigureAwait(false);
            var data = new byte[count * SectorSize];
            var received = 0;
            while (received < data.Length)
            {
                var n = await usb.BulkInAsync(data.AsMemory(received), cancellationToken).ConfigureAwait(false);
                if (n <= 0) throw new DeviceException("Lecture USB interrompue.");
                received += n;
            }
            await ReadCswAsync("lecture", cancellationToken).ConfigureAwait(false);
            return data;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ReadCswAsync(string operation, CancellationToken cancellationToken)
    {
        var csw = new byte[CswLength];
        var n = await usb.BulkInAsync(csw, cancellationToken).ConfigureAwait(false);
        // "USBS" signature, then tag (4), residue (4), status (1, 0 = passed).
        if (n != CswLength || csw[0] != 'U' || csw[1] != 'S' || csw[2] != 'B' || csw[3] != 'S' || csw[12] != 0)
        {
            throw new DeviceException($"L'appareil a refusé l'opération ({operation}).");
        }
    }

    /// <summary>Builds a Command Block Wrapper.</summary>
    internal static byte[] Cbw(ReadOnlySpan<byte> command, int address, short count, bool inbound)
    {
        var cbw = new byte[CbwLength];
        var s = cbw.AsSpan();
        "USBC"u8.CopyTo(s);
        RandomNumberGenerator.Fill(s.Slice(4, 4)); // tag
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], count * SectorSize);
        s[12] = (byte)(inbound ? 0x80 : 0x00);
        s[13] = 0; // LUN
        s[14] = 0x10; // command block length
        command.CopyTo(s[15..]);
        BinaryPrimitives.WriteInt32BigEndian(s[18..], address);
        BinaryPrimitives.WriteInt16BigEndian(s[22..], count);
        return cbw;
    }
}
