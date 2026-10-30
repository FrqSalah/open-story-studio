using System.Collections.Concurrent;
using Studio.Core.Model;
using Studio.Formats.Archive;

namespace Studio.Formats.Library;

/// <summary>Summary of a pack stored in the library.</summary>
/// <param name="Id">Pack id.</param>
/// <param name="Title">Title.</param>
/// <param name="Description">Description.</param>
/// <param name="StageCount">Number of stage nodes.</param>
/// <param name="SizeBytes">Archive size.</param>
/// <param name="ModifiedUtc">Last modification.</param>
/// <param name="Thumbnail">Thumbnail (PNG) or cover image, if any.</param>
public sealed record LibraryEntry(Guid Id, string Title, string? Description, int StageCount, long SizeBytes, DateTimeOffset ModifiedUtc, MediaAsset? Thumbnail);

/// <summary>Storage of the user's packs, as STUdio-compatible archives.</summary>
public interface IPackLibrary
{
    /// <summary>Raised when packs are added, changed or removed.</summary>
    event EventHandler? Changed;

    /// <summary>Lists packs, most recent first.</summary>
    Task<IReadOnlyList<LibraryEntry>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a pack.</summary>
    Task<StoryPack?> LoadAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a pack.</summary>
    Task SaveAsync(StoryPack pack, CancellationToken cancellationToken = default);

    /// <summary>Deletes a pack.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Imports an archive (zip) and returns the imported pack.</summary>
    Task<StoryPack> ImportAsync(Stream archive, CancellationToken cancellationToken = default);

    /// <summary>Exports a pack as an archive.</summary>
    Task<byte[]> ExportAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Base class holding the archive logic shared by library implementations.</summary>
public abstract class PackLibraryBase : IPackLibrary
{
    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Lists stored archives.</summary>
    protected abstract Task<IReadOnlyList<(Guid Id, DateTimeOffset Modified)>> ListIdsAsync(CancellationToken cancellationToken);

    /// <summary>Reads an archive, or null.</summary>
    protected abstract Task<byte[]?> ReadAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Writes an archive.</summary>
    protected abstract Task WriteAsync(Guid id, byte[] archive, CancellationToken cancellationToken);

    /// <summary>Deletes an archive.</summary>
    protected abstract Task RemoveAsync(Guid id, CancellationToken cancellationToken);

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Modified, LibraryEntry Entry)> _cache = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<LibraryEntry>();
        foreach (var (id, modified) in await ListIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_cache.TryGetValue(id, out var cached) && cached.Modified == modified)
            {
                entries.Add(cached.Entry);
                continue;
            }
            var data = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
            if (data is null) continue;
            try
            {
                var pack = ArchivePackFormat.Read(data);
                var thumb = pack.Thumbnail is { Length: > 0 } t ? new MediaAsset(t, MediaTypes.Png) : pack.SquareOne?.Image;
                var entry = new LibraryEntry(id, pack.Title, pack.Description, pack.StageNodes.Count, data.Length, modified, thumb);
                _cache[id] = (modified, entry);
                entries.Add(entry);
            }
            catch (InvalidDataException)
            {
                entries.Add(new LibraryEntry(id, "(archive illisible)", null, 0, data.Length, modified, null));
            }
        }
        return entries.OrderByDescending(e => e.ModifiedUtc).ToList();
    }

    /// <inheritdoc />
    public async Task<StoryPack?> LoadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var data = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
        return data is null ? null : ArchivePackFormat.Read(data);
    }

    /// <inheritdoc />
    public async Task SaveAsync(StoryPack pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.Id == Guid.Empty) throw new InvalidOperationException("Un pack vide ne peut pas être enregistré.");
        await WriteAsync(pack.Id, ArchivePackFormat.Write(pack), cancellationToken).ConfigureAwait(false);
        _cache.TryRemove(pack.Id, out _);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        _cache.TryRemove(id, out _);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task<StoryPack> ImportAsync(Stream archive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        using var ms = new MemoryStream();
        await archive.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var pack = ArchivePackFormat.Read(ms.ToArray());
        await SaveAsync(pack, cancellationToken).ConfigureAwait(false);
        return pack;
    }

    /// <inheritdoc />
    public async Task<byte[]> ExportAsync(Guid id, CancellationToken cancellationToken = default) =>
        await ReadAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException("Pack introuvable dans la bibliothèque.");
}

/// <summary>Library stored as <c>{id}.zip</c> files in a folder.</summary>
public sealed class FolderPackLibrary : PackLibraryBase
{
    private readonly string _folder;

    /// <summary>Creates the library (the folder is created if needed).</summary>
    public FolderPackLibrary(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    /// <summary>Default location: <c>%LOCALAPPDATA%/OpenStoryStudio/library</c>.</summary>
    public static string DefaultFolder => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenStoryStudio", "library");

    /// <inheritdoc />
    protected override Task<IReadOnlyList<(Guid Id, DateTimeOffset Modified)>> ListIdsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(Guid, DateTimeOffset)> ids = Directory.EnumerateFiles(_folder, "*.zip")
            .Select(f => (Ok: Guid.TryParse(Path.GetFileNameWithoutExtension(f), out var id), Id: id, File: f))
            .Where(x => x.Ok)
            .Select(x => (x.Id, (DateTimeOffset)File.GetLastWriteTimeUtc(x.File)))
            .ToList();
        return Task.FromResult(ids);
    }

    /// <inheritdoc />
    protected override async Task<byte[]?> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var path = PathOf(id);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <inheritdoc />
    protected override async Task WriteAsync(Guid id, byte[] archive, CancellationToken cancellationToken)
    {
        // Write then rename so a crash never leaves a truncated archive.
        var path = PathOf(id);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, archive, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    /// <inheritdoc />
    protected override Task RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        File.Delete(PathOf(id));
        return Task.CompletedTask;
    }

    private string PathOf(Guid id) => Path.Join(_folder, id.ToString("D") + ".zip");
}

/// <summary>In-memory library (browser edition and tests).</summary>
public sealed class InMemoryPackLibrary : PackLibraryBase
{
    private readonly ConcurrentDictionary<Guid, (byte[] Data, DateTimeOffset Modified)> _packs = new();

    /// <inheritdoc />
    protected override Task<IReadOnlyList<(Guid Id, DateTimeOffset Modified)>> ListIdsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(Guid, DateTimeOffset)> ids = _packs.Select(p => (p.Key, p.Value.Modified)).ToList();
        return Task.FromResult(ids);
    }

    /// <inheritdoc />
    protected override Task<byte[]?> ReadAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_packs.TryGetValue(id, out var p) ? p.Data : null);

    /// <inheritdoc />
    protected override Task WriteAsync(Guid id, byte[] archive, CancellationToken cancellationToken)
    {
        _packs[id] = (archive, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        _packs.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
