using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Studio.Formats.Crypto;

namespace Studio.Formats.DeviceFs;

/// <summary>Hardware generation of the story teller.</summary>
public enum DeviceGeneration
{
    /// <summary>Unknown.</summary>
    Unknown = 0,
    /// <summary>Firmware 1.x, raw SD access over USB.</summary>
    V1 = 1,
    /// <summary>Firmware 2.x, mass storage, XXTEA.</summary>
    V2 = 2,
    /// <summary>Firmware 3.x, mass storage, AES.</summary>
    V3 = 3,
}

/// <summary>
/// Content of the <c>.md</c> file at the root of a mass storage device (firmware v2 and v3).
/// </summary>
public sealed class DeviceMetadata
{
    /// <summary>Name of the metadata file.</summary>
    public const string FileName = ".md";

    private DeviceMetadata() { }

    /// <summary>Metadata format version (1-5 for v2 devices, 6-7 for v3 devices).</summary>
    public int FormatVersion { get; private init; }

    /// <summary>Hardware generation deduced from the format.</summary>
    public DeviceGeneration Generation { get; private init; }

    /// <summary>Firmware major version.</summary>
    public int FirmwareMajor { get; private init; }

    /// <summary>Firmware minor version.</summary>
    public int FirmwareMinor { get; private init; }

    /// <summary>Firmware version as text.</summary>
    public string Firmware => $"{FirmwareMajor}.{FirmwareMinor}";

    /// <summary>Device serial number, if present.</summary>
    public string? SerialNumber { get; private init; }

    /// <summary>256-byte device UUID used to derive the v2 boot key (v2 only).</summary>
    public byte[]? DeviceUuid { get; private init; }

    /// <summary>AES material (v3 only).</summary>
    public V3DeviceKey? V3Key { get; private init; }

    /// <summary>Parses the content of a <c>.md</c> file.</summary>
    /// <exception cref="FormatException">The format version is not supported.</exception>
    public static DeviceMetadata Parse(ReadOnlySpan<byte> md)
    {
        if (md.Length < 2) throw new FormatException("Fichier .md trop court.");
        var version = BinaryPrimitives.ReadInt16LittleEndian(md);
        return version switch
        {
            >= 1 and <= 5 => ParseV1To5(md, version),
            6 or 7 => ParseV6To7(md, version),
            _ => throw new FormatException($"Version de métadonnées non supportée : {version}."),
        };
    }

    private static DeviceMetadata ParseV1To5(ReadOnlySpan<byte> md, int version)
    {
        // Layout (from STUdio): version(2) skip(4) fwMajor(2) fwMinor(2) serial(8, big-endian) ... uuid(256) at 0x100.
        Require(md, 0x200);
        var major = BinaryPrimitives.ReadInt16LittleEndian(md[6..]);
        var minor = BinaryPrimitives.ReadInt16LittleEndian(md[8..]);
        var sn = BinaryPrimitives.ReadInt64BigEndian(md[10..]);
        string? serial = sn is 0 or -1 or -4294967296L ? null : sn.ToString("D14", CultureInfo.InvariantCulture);

        return new DeviceMetadata
        {
            FormatVersion = version,
            Generation = DeviceGeneration.V2,
            FirmwareMajor = major,
            FirmwareMinor = minor,
            SerialNumber = serial,
            DeviceUuid = md.Slice(0x100, 0x100).ToArray(),
        };
    }

    private static DeviceMetadata ParseV6To7(ReadOnlySpan<byte> md, int version)
    {
        // Layout (from STUdio): version(2) fwMajor(1 ascii) '.'(1) fwMinor(1 ascii) skip(21) serial(24 ascii) at 0x1A,
        // then 14 bytes, then 32 bytes at 0x40 (bt for v6, AES key + IV for v7).
        Require(md, 0x60);
        var major = AsciiDigit(md[2]);
        var minor = AsciiDigit(md[4]);
        var snBytes = md.Slice(0x1A, 24).ToArray();
        var serial = Encoding.ASCII.GetString(snBytes).TrimEnd('\0', ' ');

        byte[] key, iv, bt;
        if (version == 6)
        {
            key = snBytes[..16];
            iv = [.. snBytes[16..24], .. snBytes[..8]];
            bt = md.Slice(0x40, 32).ToArray();
        }
        else
        {
            key = md.Slice(0x40, 16).ToArray();
            iv = md.Slice(0x50, 16).ToArray();
            bt = [.. snBytes, .. snBytes[..8]];
        }

        return new DeviceMetadata
        {
            FormatVersion = version,
            Generation = DeviceGeneration.V3,
            FirmwareMajor = major,
            FirmwareMinor = minor,
            SerialNumber = serial,
            V3Key = new V3DeviceKey(key, iv, bt),
        };
    }

    private static int AsciiDigit(byte b) => b is >= (byte)'0' and <= (byte)'9' ? b - '0' : -1;

    private static void Require(ReadOnlySpan<byte> md, int length)
    {
        if (md.Length < length) throw new FormatException($"Fichier .md tronqué ({md.Length} octets, {length} attendus).");
    }

    /// <summary>
    /// Builds a synthetic v2 <c>.md</c> file (format version 3). Used for tests and device images.
    /// </summary>
    public static byte[] CreateV2(int firmwareMajor, int firmwareMinor, long serial, ReadOnlySpan<byte> deviceUuid)
    {
        if (deviceUuid.Length != 0x100) throw new ArgumentException("Device UUID must be 256 bytes.", nameof(deviceUuid));
        var md = new byte[0x200];
        BinaryPrimitives.WriteInt16LittleEndian(md, 3);
        BinaryPrimitives.WriteInt16LittleEndian(md.AsSpan(6), (short)firmwareMajor);
        BinaryPrimitives.WriteInt16LittleEndian(md.AsSpan(8), (short)firmwareMinor);
        BinaryPrimitives.WriteInt64BigEndian(md.AsSpan(10), serial);
        deviceUuid.CopyTo(md.AsSpan(0x100));
        return md;
    }

    /// <summary>
    /// Builds a synthetic v3 <c>.md</c> file (format version 7). Used for tests and device images.
    /// </summary>
    public static byte[] CreateV7(int firmwareMajor, int firmwareMinor, string serial, ReadOnlySpan<byte> aesKey, ReadOnlySpan<byte> aesIv)
    {
        ArgumentNullException.ThrowIfNull(serial);
        var md = new byte[0x80];
        BinaryPrimitives.WriteInt16LittleEndian(md, 7);
        md[2] = (byte)('0' + firmwareMajor);
        md[3] = (byte)'.';
        md[4] = (byte)('0' + firmwareMinor);
        Encoding.ASCII.GetBytes(serial.PadRight(24, '\0')[..24]).CopyTo(md, 0x1A);
        aesKey.CopyTo(md.AsSpan(0x40));
        aesIv.CopyTo(md.AsSpan(0x50));
        return md;
    }
}
