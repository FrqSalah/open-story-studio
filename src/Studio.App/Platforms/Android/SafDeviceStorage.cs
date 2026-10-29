using Android.Content;
using Android.Provider;
using Studio.App.Services;
using Studio.Device.Storage;
using Document = Android.Provider.DocumentsContract.Document;
using Uri = Android.Net.Uri;

namespace Studio.App.Platforms.Droid;

/// <summary>
/// <see cref="IDeviceStorage"/> over a Storage Access Framework tree (the USB OTG volume picked by the user).
/// </summary>
public sealed class SafDeviceStorage : IDeviceStorage
{
    private static readonly string[] Projection = [Document.ColumnDocumentId, Document.ColumnDisplayName, Document.ColumnMimeType, Document.ColumnSize];
    private readonly ContentResolver _resolver;
    private readonly Uri _tree;
    private readonly string _rootId;
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);

    /// <summary>Creates the storage for a tree URI with persisted read/write permission.</summary>
    public SafDeviceStorage(ContentResolver resolver, Uri tree)
    {
        _resolver = resolver;
        _tree = tree;
        _rootId = DocumentsContract.GetTreeDocumentId(tree) ?? throw new ArgumentException("URI d'arborescence invalide.", nameof(tree));
    }

    /// <inheritdoc />
    public string Name => _tree.LastPathSegment ?? "USB";

    /// <inheritdoc />
    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Find(path) is { IsDirectory: false }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Find(path) is { IsDirectory: true }, cancellationToken);

    /// <inheritdoc />
    public Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Find(path)?.Size ?? throw new FileNotFoundException(path), cancellationToken);

    /// <inheritdoc />
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var entry = Find(path) ?? throw new FileNotFoundException("Fichier introuvable sur l'appareil.", path);
        using var input = _resolver.OpenInputStream(DocUri(entry.Id)) ?? throw new IOException($"Lecture impossible : {path}");
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }, cancellationToken);

    /// <inheritdoc />
    public Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var id = Find(path)?.Id ?? Create(path);
        // "wt" truncates existing content.
        using var output = _resolver.OpenOutputStream(DocUri(id), "wt") ?? throw new IOException($"Écriture impossible : {path}");
        output.Write(data.Span);
        output.Flush();
    }, cancellationToken);

    /// <inheritdoc />
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) => DeleteAsync(path, cancellationToken);

    /// <inheritdoc />
    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default) => DeleteAsync(path, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var result = new List<string>();
        var start = Find(directory);
        if (start is not { IsDirectory: true }) return result;
        var stack = new Stack<(string Path, string Id)>();
        stack.Push((directory.Trim('/'), start.Id));
        while (stack.TryPop(out var dir))
        {
            foreach (var child in Children(dir.Id))
            {
                var childPath = dir.Path.Length == 0 ? child.Name : $"{dir.Path}/{child.Name}";
                _ids[childPath] = child.Id;
                if (child.IsDirectory) stack.Push((childPath, child.Id));
                else result.Add(childPath);
            }
        }
        return result;
    }, cancellationToken);

    /// <inheritdoc />
    public Task<(long Total, long Free)?> GetSpaceAsync(CancellationToken cancellationToken = default) => Task.Run<(long, long)?>(() =>
    {
        // Roots of the provider expose the available bytes; the capacity is not always reported.
        var authority = _tree.Authority;
        if (authority is null) return null;
        using var cursor = _resolver.Query(DocumentsContract.BuildRootsUri(authority), [DocumentsContract.Root.ColumnRootId, DocumentsContract.Root.ColumnAvailableBytes, "capacity_bytes"], null, null, null);
        var rootId = _rootId.Split(':')[0];
        while (cursor?.MoveToNext() == true)
        {
            if (cursor.GetString(0) != rootId) continue;
            var free = cursor.IsNull(1) ? -1 : cursor.GetLong(1);
            var total = cursor.ColumnCount > 2 && !cursor.IsNull(2) ? cursor.GetLong(2) : -1;
            return free < 0 ? null : (total < 0 ? free : total, free);
        }
        return null;
    }, cancellationToken);

    private Task DeleteAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (Find(path) is not { } entry) return;
        DocumentsContract.DeleteDocument(_resolver, DocUri(entry.Id));
        foreach (var key in _ids.Keys.Where(k => k == path || k.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal)).ToList()) _ids.Remove(key);
    }, cancellationToken);

    private sealed record Entry(string Id, string Name, bool IsDirectory, long Size);

    private Entry? Find(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var id = _rootId;
        Entry? current = new(id, string.Empty, true, 0);
        var walked = string.Empty;
        foreach (var part in parts)
        {
            walked = walked.Length == 0 ? part : $"{walked}/{part}";
            current = Children(id).FirstOrDefault(c => c.Name == part);
            if (current is null)
            {
                _ids.Remove(walked);
                return null;
            }
            _ids[walked] = current.Id;
            id = current.Id;
        }
        return current;
    }

    private string Create(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentId = _rootId;
        for (var i = 0; i < parts.Length; i++)
        {
            var isLast = i == parts.Length - 1;
            var existing = Children(parentId).FirstOrDefault(c => c.Name == parts[i]);
            if (existing is not null)
            {
                parentId = existing.Id;
                continue;
            }
            // application/octet-stream keeps the exact name (no extension appended by the provider).
            var created = DocumentsContract.CreateDocument(_resolver, DocUri(parentId), isLast ? "application/octet-stream" : Document.MimeTypeDir, parts[i])
                ?? throw new IOException($"Création impossible : {path}");
            parentId = DocumentsContract.GetDocumentId(created) ?? throw new IOException($"Création impossible : {path}");
        }
        return parentId;
    }

    private List<Entry> Children(string parentId)
    {
        var entries = new List<Entry>();
        using var cursor = _resolver.Query(DocumentsContract.BuildChildDocumentsUriUsingTree(_tree, parentId)!, Projection, null, null, null);
        while (cursor?.MoveToNext() == true)
        {
            entries.Add(new Entry(cursor.GetString(0)!, cursor.GetString(1) ?? string.Empty, cursor.GetString(2) == Document.MimeTypeDir, cursor.IsNull(3) ? 0 : cursor.GetLong(3)));
        }
        return entries;
    }

    private Uri DocUri(string id) => DocumentsContract.BuildDocumentUriUsingTree(_tree, id)!;
}

/// <summary>Device picker based on <c>ACTION_OPEN_DOCUMENT_TREE</c>; the permission is persisted.</summary>
public sealed class SafDevicePicker : IDevicePicker
{
    private const string LastTreeKey = "device.lastTree";

    /// <inheritdoc />
    public async Task<IDeviceStorage?> PickAsync(CancellationToken cancellationToken = default)
    {
        var uri = await MainThread.InvokeOnMainThreadAsync(MainActivity.PickTreeAsync);
        if (uri is null) return null;
        Preferences.Default.Set(LastTreeKey, uri.ToString());
        return new SafDeviceStorage(Platform.AppContext.ContentResolver!, uri);
    }

    /// <inheritdoc />
    public IDeviceStorage? Restore()
    {
        if (Preferences.Default.Get<string?>(LastTreeKey, null) is not { } saved) return null;
        var uri = Uri.Parse(saved)!;
        var granted = Platform.AppContext.ContentResolver!.PersistedUriPermissions.Any(p => p.Uri?.Equals(uri) == true && p.IsWritePermission);
        return granted ? new SafDeviceStorage(Platform.AppContext.ContentResolver!, uri) : null;
    }
}
