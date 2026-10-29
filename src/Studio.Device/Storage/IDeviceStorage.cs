namespace Studio.Device.Storage;

/// <summary>
/// File access to the root of a mass storage device (firmware v2/v3).
/// Paths are relative to the device root and use '/' separators.
/// </summary>
/// <remarks>
/// Implemented with <see cref="System.IO"/> on desktop, with the Storage Access Framework on Android,
/// and in memory for tests and device images.
/// </remarks>
public interface IDeviceStorage
{
    /// <summary>Display name of the storage (drive letter, mount point…).</summary>
    string Name { get; }

    /// <summary>True when the file exists.</summary>
    Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>True when the directory exists.</summary>
    Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Size of a file in bytes.</summary>
    Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Reads a whole file.</summary>
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a file, creating parent directories.</summary>
    Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file if it exists.</summary>
    Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Deletes a directory and its content if it exists.</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Lists every file under a directory, recursively, as paths relative to the device root.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>Total and free space in bytes, when known.</summary>
    Task<(long Total, long Free)?> GetSpaceAsync(CancellationToken cancellationToken = default);
}
