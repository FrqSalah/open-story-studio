using Studio.UI.Services;

namespace Studio.App.Services;

/// <summary>
/// Saves exports: on Windows into Documents\Open Story Studio, on Android through the share sheet
/// (Drive, e-mail, Files…).
/// </summary>
public sealed class ShareFileSaver : IFileSaver
{
    /// <inheritdoc />
    public async Task<string> SaveAsync(string fileName, string mimeType, byte[] data, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Open Story Studio");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, fileName);
            await File.WriteAllBytesAsync(path, data, cancellationToken);
            return $"Enregistré dans {path}";
        }

        var temp = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllBytesAsync(temp, data, cancellationToken);
        await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = fileName,
            File = new ShareFile(temp, mimeType),
        }));
        return "Fichier prêt à être partagé.";
    }
}
