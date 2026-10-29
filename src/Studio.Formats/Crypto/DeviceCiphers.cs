using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Studio.Formats.Crypto;

/// <summary>
/// Device specific AES material of firmware v3 devices, read from the <c>.md</c> file.
/// </summary>
/// <param name="AesKey">16-byte AES key (as stored, word order not yet reversed).</param>
/// <param name="AesIv">16-byte AES IV (as stored).</param>
/// <param name="BootFile">32-byte content of the <c>bt</c> file written in each pack folder.</param>
public sealed record V3DeviceKey(byte[] AesKey, byte[] AesIv, byte[] BootFile);

/// <summary>
/// Scrambling applied to pack files on the device.
/// </summary>
/// <remarks>
/// <para>Firmware v2: the first 512 bytes of most files are XXTEA-encrypted with a common key, and the
/// <c>bt</c> file contains the first 64 (encrypted) bytes of <c>ri</c> encrypted with a device-specific key.</para>
/// <para>Firmware v3: the first 512 bytes of most files are AES-CBC encrypted with a device-specific key;
/// the <c>bt</c> file comes from the device metadata.</para>
/// Ported from STUdio's <c>CipherUtils</c>/<c>AESCBCCipher</c> (MPL-2.0).
/// </remarks>
public static class DeviceCiphers
{
    /// <summary>Number of scrambled bytes at the beginning of asset/index files.</summary>
    public const int AssetBlockSize = 512;

    /// <summary>Number of <c>ri</c> bytes used to build the v2 boot file.</summary>
    public const int BootBlockSizeV2 = 64;

    private static readonly uint[] CommonKeyWords = Xxtea.KeyFromBytes(Xxtea.CommonKey);

    /// <summary>Files of a pack folder that are never scrambled.</summary>
    public static bool IsClearFile(string fileName) => fileName is "ni" or "nm" or "bt" or ".cleartext";

    /// <summary>Scrambles the first block of a v2 file with the common key.</summary>
    public static byte[] EncryptCommonKey(ReadOnlySpan<byte> data) => TransformXxtea(data, AssetBlockSize, CommonKeyWords, encrypt: true);

    /// <summary>Unscrambles the first block of a v2 file with the common key.</summary>
    public static byte[] DecryptCommonKey(ReadOnlySpan<byte> data) => TransformXxtea(data, AssetBlockSize, CommonKeyWords, encrypt: false);

    /// <summary>
    /// Derives the v2 device-specific key from the 256-byte device UUID found in <c>.md</c>.
    /// </summary>
    public static byte[] DeriveV2SpecificKey(ReadOnlySpan<byte> deviceUuid)
    {
        var k = DecryptCommonKey(deviceUuid);
        if (k.Length < 16) throw new ArgumentException("Device UUID is too short.", nameof(deviceUuid));
        return
        [
            k[11], k[10], k[9], k[8],
            k[15], k[14], k[13], k[12],
            k[3], k[2], k[1], k[0],
            k[7], k[6], k[5], k[4],
        ];
    }

    /// <summary>
    /// Builds the v2 <c>bt</c> file: the first 64 bytes of the <b>scrambled</b> <c>ri</c> file
    /// encrypted with the device-specific key.
    /// </summary>
    public static byte[] BuildBootFileV2(ReadOnlySpan<byte> scrambledRi, ReadOnlySpan<byte> deviceUuid)
    {
        var key = Xxtea.KeyFromBytes(DeriveV2SpecificKey(deviceUuid));
        var block = scrambledRi[..Math.Min(BootBlockSizeV2, scrambledRi.Length)];
        return TransformXxtea(block, BootBlockSizeV2, key, encrypt: true);
    }

    /// <summary>Encrypts the first block of a v3 file. The output may grow by up to 15 bytes (zero padding).</summary>
    [UnsupportedOSPlatform("browser")]
    public static byte[] EncryptV3(ReadOnlySpan<byte> data, V3DeviceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var blockLength = Math.Min(AssetBlockSize, data.Length);
        var padded = new byte[RoundUp16(blockLength)];
        data[..blockLength].CopyTo(padded);
        using var aes = CreateAes(key);
        var encrypted = aes.EncryptCbc(padded, ReverseWords(key.AesIv), PaddingMode.None);

        var output = new byte[encrypted.Length + Math.Max(0, data.Length - AssetBlockSize)];
        encrypted.CopyTo(output, 0);
        if (data.Length > AssetBlockSize) data[AssetBlockSize..].CopyTo(output.AsSpan(encrypted.Length));
        return output;
    }

    /// <summary>Decrypts the first block of a v3 file.</summary>
    [UnsupportedOSPlatform("browser")]
    public static byte[] DecryptV3(ReadOnlySpan<byte> data, V3DeviceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var blockLength = Math.Min(AssetBlockSize, data.Length);
        var padded = new byte[RoundUp16(blockLength)];
        data[..blockLength].CopyTo(padded);
        using var aes = CreateAes(key);
        var decrypted = aes.DecryptCbc(padded, ReverseWords(key.AesIv), PaddingMode.None);

        var output = data.ToArray();
        decrypted.AsSpan(0, blockLength).CopyTo(output);
        return output;
    }

    private static Aes CreateAes(V3DeviceKey key)
    {
        var aes = Aes.Create();
        aes.Key = ReverseWords(key.AesKey);
        return aes;
    }

    /// <summary>Reverses the byte order of each 32-bit word (STUdio's <c>reverseEndianness</c>).</summary>
    internal static byte[] ReverseWords(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length / 4 * 4];
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(i), BinaryPrimitives.ReadUInt32LittleEndian(data[i..]));
        }
        return output;
    }

    private static int RoundUp16(int length) => (length + 15) / 16 * 16;

    private static byte[] TransformXxtea(ReadOnlySpan<byte> data, int blockSize, uint[] key, bool encrypt)
    {
        var output = data.ToArray();
        var words = Math.Min(blockSize, data.Length) / 4;
        if (words < 2) return output;

        var v = new uint[words];
        for (var i = 0; i < words; i++) v[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
        if (encrypt) Xxtea.Encrypt(v, key);
        else Xxtea.Decrypt(v, key);
        for (var i = 0; i < words; i++) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4), v[i]);
        return output;
    }
}
