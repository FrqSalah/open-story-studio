using System.Buffers.Binary;

namespace Studio.Formats.Crypto;

/// <summary>
/// Corrected Block TEA (XXTEA) as used by the device firmware v1/v2.
/// Ported from STUdio's <c>XXTEACipher</c> (MPL-2.0).
/// </summary>
internal static class Xxtea
{
    private const uint Delta = 0x9e3779b9;

    /// <summary>Common key used to scramble the first 512 bytes of pack files on firmware v2.</summary>
    public static ReadOnlySpan<byte> CommonKey =>
    [
        0x91, 0xbd, 0x7a, 0x0a, 0xa7, 0x54, 0x40, 0xa9, 0xbb, 0xd4, 0x9d, 0x6c, 0xe0, 0xdc, 0xc0, 0xe3,
    ];

    /// <summary>Encrypts <paramref name="v"/> in place.</summary>
    public static void Encrypt(Span<uint> v, ReadOnlySpan<uint> k)
    {
        var n = v.Length;
        if (n < 2) return;
        var rounds = Rounds(n);
        uint sum = 0, z = v[n - 1], y;
        do
        {
            sum += Delta;
            var e = (int)((sum >> 2) & 3);
            int p;
            for (p = 0; p < n - 1; p++)
            {
                y = v[p + 1];
                z = v[p] += Mx(k, e, p, y, z, sum);
            }
            y = v[0];
            z = v[n - 1] += Mx(k, e, p, y, z, sum);
        }
        while (--rounds != 0);
    }

    /// <summary>Decrypts <paramref name="v"/> in place.</summary>
    public static void Decrypt(Span<uint> v, ReadOnlySpan<uint> k)
    {
        var n = v.Length;
        if (n < 2) return;
        var rounds = Rounds(n);
        var sum = (uint)rounds * Delta;
        uint y = v[0], z;
        do
        {
            var e = (int)((sum >> 2) & 3);
            int p;
            for (p = n - 1; p > 0; p--)
            {
                z = v[p - 1];
                y = v[p] -= Mx(k, e, p, y, z, sum);
            }
            z = v[n - 1];
            y = v[0] -= Mx(k, e, p, y, z, sum);
            sum -= Delta;
        }
        while (--rounds != 0);
    }

    /// <summary>Reads a 16-byte key as four big-endian words, like STUdio does.</summary>
    public static uint[] KeyFromBytes(ReadOnlySpan<byte> key)
    {
        if (key.Length != 16) throw new ArgumentException("XXTEA keys are 16 bytes long.", nameof(key));
        var k = new uint[4];
        for (var i = 0; i < 4; i++) k[i] = BinaryPrimitives.ReadUInt32BigEndian(key[(i * 4)..]);
        return k;
    }

    /// <summary>The firmware uses 1 + 52/n rounds instead of the standard 6 + 52/n.</summary>
    private static int Rounds(int n) => 1 + 52 / n;

    private static uint Mx(ReadOnlySpan<uint> k, int e, int p, uint y, uint z, uint sum) =>
        (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((sum ^ y) + (k[(p & 3) ^ e] ^ z));
}
