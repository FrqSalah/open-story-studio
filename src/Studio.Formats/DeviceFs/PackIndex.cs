namespace Studio.Formats.DeviceFs;

/// <summary>
/// The <c>.pi</c> file: ordered list of installed pack ids, 16 bytes each (RFC 4122 / big-endian order).
/// </summary>
public static class PackIndex
{
    /// <summary>Name of the pack index file.</summary>
    public const string FileName = ".pi";

    /// <summary>Name of the folder holding pack folders.</summary>
    public const string ContentFolder = ".content";

    /// <summary>Reads the pack ids, in device order.</summary>
    public static IReadOnlyList<Guid> Read(ReadOnlySpan<byte> pi)
    {
        var ids = new List<Guid>(pi.Length / 16);
        for (var i = 0; i + 16 <= pi.Length; i += 16)
        {
            ids.Add(new Guid(pi.Slice(i, 16), bigEndian: true));
        }
        return ids;
    }

    /// <summary>Serializes pack ids.</summary>
    public static byte[] Write(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.ToList();
        var pi = new byte[list.Count * 16];
        for (var i = 0; i < list.Count; i++)
        {
            list[i].TryWriteBytes(pi.AsSpan(i * 16, 16), bigEndian: true, out _);
        }
        return pi;
    }

    /// <summary>Folder name of a pack on the device: last 8 hex digits of its id, uppercase.</summary>
    public static string FolderName(Guid packId) => packId.ToString("N")[^8..].ToUpperInvariant();
}
