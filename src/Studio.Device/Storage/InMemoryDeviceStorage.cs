using System.Collections.Concurrent;

namespace Studio.Device.Storage;

/// <summary>
/// In-memory <see cref="IDeviceStorage"/>: device images for tests and the "virtual device" mode.
/// </summary>
public sealed class InMemoryDeviceStorage(string name = "virtual", long totalSize = 4L * 1024 * 1024 * 1024) : IDeviceStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    /// <summary>Makes the next write to a path matching this predicate fail (fault injection for tests).</summary>
    public Func<string, bool>? FailWritesTo { get; set; }

    /// <summary>Files currently stored.</summary>
    public IReadOnlyDictionary<string, byte[]> Files => _files;

    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(_files.ContainsKey(Norm(path)));

    /// <inheritdoc />
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var prefix = Norm(path).TrimEnd('/') + "/";
        return Task.FromResult(_files.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)));
    }

    /// <inheritdoc />
    public Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken = default) =>
        _files.TryGetValue(Norm(path), out var data)
            ? Task.FromResult((long)data.Length)
            : Task.FromException<long>(new FileNotFoundException("Fichier introuvable sur l'appareil.", path));

    /// <inheritdoc />
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) =>
        _files.TryGetValue(Norm(path), out var data)
            ? Task.FromResult(data.ToArray())
            : Task.FromException<byte[]>(new FileNotFoundException("Fichier introuvable sur l'appareil.", path));

    /// <inheritdoc />
    public Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var p = Norm(path);
        if (FailWritesTo?.Invoke(p) == true)
        {
            FailWritesTo = null;
            return Task.FromException(new IOException($"Écriture simulée en échec : {p}"));
        }
        _files[p] = data.ToArray();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        _files.TryRemove(Norm(path), out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var prefix = Norm(path).TrimEnd('/') + "/";
        foreach (var key in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _files.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken = default)
    {
        var prefix = Norm(directory).TrimEnd('/') + "/";
        IReadOnlyList<string> files = _files.Keys.Where(k => prefix == "/" || k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        return Task.FromResult(files);
    }

    /// <inheritdoc />
    public Task<(long Total, long Free)?> GetSpaceAsync(CancellationToken cancellationToken = default)
    {
        var used = _files.Values.Sum(v => (long)v.Length);
        return Task.FromResult<(long, long)?>((totalSize, totalSize - used));
    }

    private static string Norm(string path) => path.Replace('\\', '/').TrimStart('/');
}
