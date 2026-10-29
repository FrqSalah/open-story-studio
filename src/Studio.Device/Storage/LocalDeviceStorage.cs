namespace Studio.Device.Storage;

/// <summary><see cref="IDeviceStorage"/> over a mounted folder (drive letter or mount point).</summary>
public sealed class LocalDeviceStorage(string root) : IDeviceStorage
{
    private readonly string _root = Path.GetFullPath(root);

    /// <inheritdoc />
    public string Name => _root;

    /// <inheritdoc />
    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(File.Exists(Full(path)));

    /// <inheritdoc />
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Directory.Exists(Full(path)));

    /// <inheritdoc />
    public Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new FileInfo(Full(path)).Length);

    /// <inheritdoc />
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) => File.ReadAllBytesAsync(Full(path), cancellationToken);

    /// <inheritdoc />
    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Hidden files (.pi, .md) cannot be opened for writing with FileMode.Create on Windows:
        // clear the attributes while writing and restore them afterwards.
        FileAttributes? attributes = File.Exists(full) ? File.GetAttributes(full) : null;
        if (attributes is not null) File.SetAttributes(full, FileAttributes.Normal);
        await using (var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        if (attributes is { } a && a != FileAttributes.Normal) File.SetAttributes(full, a);
    }

    /// <inheritdoc />
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = Full(path);
        if (File.Exists(full))
        {
            File.SetAttributes(full, FileAttributes.Normal);
            File.Delete(full);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = Full(path);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken = default)
    {
        var full = Full(directory);
        IReadOnlyList<string> files = Directory.Exists(full)
            ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
                .ToList()
            : [];
        return Task.FromResult(files);
    }

    /// <inheritdoc />
    public Task<(long Total, long Free)?> GetSpaceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var drive = new DriveInfo(_root);
            return Task.FromResult<(long, long)?>((drive.TotalSize, drive.AvailableFreeSpace));
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<(long, long)?>(null);
        }
    }

    private string Full(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Chemin hors de l'appareil : {path}");
        }
        return full;
    }
}
