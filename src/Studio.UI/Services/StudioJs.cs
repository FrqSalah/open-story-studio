using Microsoft.JSInterop;
using Studio.Formats.Library;

namespace Studio.UI.Services;

/// <summary>Typed wrapper of <c>studio.js</c> (download, IndexedDB, microphone, SVG helpers).</summary>
public sealed class StudioJs(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    private async ValueTask<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/Studio.UI/studio.js").ConfigureAwait(false);

    /// <summary>Triggers a browser download.</summary>
    public async Task DownloadAsync(string fileName, string mimeType, byte[] data) =>
        await (await ModuleAsync().ConfigureAwait(false)).InvokeVoidAsync("download", fileName, mimeType, data).ConfigureAwait(false);

    /// <summary>Starts recording from the microphone.</summary>
    public async Task<bool> StartRecordingAsync() =>
        await (await ModuleAsync().ConfigureAwait(false)).InvokeAsync<bool>("startRecording").ConfigureAwait(false);

    /// <summary>Stops recording and returns the audio (WAVE, mono).</summary>
    public async Task<byte[]?> StopRecordingAsync() =>
        await (await ModuleAsync().ConfigureAwait(false)).InvokeAsync<byte[]?>("stopRecording").ConfigureAwait(false);

    /// <summary>Converts client coordinates to SVG user coordinates.</summary>
    public async Task<double[]> ToSvgPointAsync(string svgId, double clientX, double clientY) =>
        await (await ModuleAsync().ConfigureAwait(false)).InvokeAsync<double[]>("toSvgPoint", svgId, clientX, clientY).ConfigureAwait(false);

    /// <summary>Plays/pauses the audio element with the given id.</summary>
    public async Task SetPausedAsync(string audioId, bool paused) =>
        await (await ModuleAsync().ConfigureAwait(false)).InvokeVoidAsync("setPaused", audioId, paused).ConfigureAwait(false);

    internal async Task DbPutAsync(string id, byte[] data) =>
        await (await ModuleAsync().ConfigureAwait(false)).InvokeVoidAsync("dbPut", id, data).ConfigureAwait(false);

    internal async Task<byte[]?> DbGetAsync(string id) =>
        await (await ModuleAsync().ConfigureAwait(false)).InvokeAsync<byte[]?>("dbGet", id).ConfigureAwait(false);

    internal async Task<DbItem[]> DbListAsync() =>
        await (await ModuleAsync().ConfigureAwait(false)).InvokeAsync<DbItem[]>("dbList").ConfigureAwait(false);

    internal async Task DbDeleteAsync(string id) =>
        await (await ModuleAsync().ConfigureAwait(false)).InvokeVoidAsync("dbDelete", id).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // Page already gone.
            }
        }
    }

    internal sealed record DbItem(string Id, long Modified);
}

/// <summary>Library persisted in the browser's IndexedDB (browser edition).</summary>
public sealed class BrowserPackLibrary(StudioJs js) : PackLibraryBase
{
    /// <inheritdoc />
    protected override async Task<IReadOnlyList<(Guid Id, DateTimeOffset Modified)>> ListIdsAsync(CancellationToken cancellationToken) =>
        (await js.DbListAsync().ConfigureAwait(false))
            .Where(i => Guid.TryParse(i.Id, out _))
            .Select(i => (Guid.Parse(i.Id), DateTimeOffset.FromUnixTimeMilliseconds(i.Modified)))
            .ToList();

    /// <inheritdoc />
    protected override Task<byte[]?> ReadAsync(Guid id, CancellationToken cancellationToken) => js.DbGetAsync(id.ToString());

    /// <inheritdoc />
    protected override Task WriteAsync(Guid id, byte[] archive, CancellationToken cancellationToken) => js.DbPutAsync(id.ToString(), archive);

    /// <inheritdoc />
    protected override Task RemoveAsync(Guid id, CancellationToken cancellationToken) => js.DbDeleteAsync(id.ToString());
}

/// <summary><see cref="IFileSaver"/> that triggers a browser download.</summary>
public sealed class BrowserFileSaver(StudioJs js) : IFileSaver
{
    /// <inheritdoc />
    public async Task<string> SaveAsync(string fileName, string mimeType, byte[] data, CancellationToken cancellationToken = default)
    {
        await js.DownloadAsync(fileName, mimeType, data).ConfigureAwait(false);
        return "Téléchargement lancé.";
    }
}
