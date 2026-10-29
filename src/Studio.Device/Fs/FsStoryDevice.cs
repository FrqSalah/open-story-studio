using System.Runtime.Versioning;
using Studio.Core.Model;
using Studio.Device.Storage;
using Studio.Formats.Crypto;
using Studio.Formats.DeviceFs;

namespace Studio.Device.Fs;

/// <summary>
/// Firmware v2/v3 device seen as mass storage: <c>.md</c> (metadata), <c>.pi</c> (pack index) and
/// <c>.content/XXXXXXXX/</c> (one folder per pack).
/// </summary>
/// <remarks>
/// Writes are ordered so that the device always stays usable: pack files are written to their folder
/// (ignored by the firmware until listed in <c>.pi</c>), read back and compared, and only then is the
/// index rewritten. On failure the folder is removed and the previous index is kept.
/// </remarks>
[UnsupportedOSPlatform("browser")]
public sealed class FsStoryDevice : IStoryDevice
{
    private const string ContentPrefix = PackIndex.ContentFolder + "/";
    private readonly IDeviceStorage _storage;
    private readonly bool _allowExperimentalWrites;

    private FsStoryDevice(IDeviceStorage storage, DeviceMetadata metadata, bool allowExperimentalWrites)
    {
        _storage = storage;
        Metadata = metadata;
        _allowExperimentalWrites = allowExperimentalWrites;
    }

    /// <summary>Parsed <c>.md</c> file.</summary>
    public DeviceMetadata Metadata { get; }

    /// <inheritdoc />
    public DeviceGeneration Generation => Metadata.Generation;

    /// <summary>
    /// Opens a device. Returns null when the storage is not a story teller (no <c>.md</c> file).
    /// </summary>
    /// <param name="storage">Device root.</param>
    /// <param name="allowExperimentalWrites">Allow writing to firmware versions not validated on real hardware (v3).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<FsStoryDevice?> TryOpenAsync(IDeviceStorage storage, bool allowExperimentalWrites = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (!await storage.FileExistsAsync(DeviceMetadata.FileName, cancellationToken).ConfigureAwait(false)) return null;
        var md = await storage.ReadAllBytesAsync(DeviceMetadata.FileName, cancellationToken).ConfigureAwait(false);
        try
        {
            return new FsStoryDevice(storage, DeviceMetadata.Parse(md), allowExperimentalWrites);
        }
        catch (FormatException e)
        {
            throw new DeviceException($"Appareil détecté sur {storage.Name} mais ses métadonnées sont illisibles : {e.Message}", e);
        }
    }

    /// <summary>True when writing is enabled for this firmware.</summary>
    public bool IsWriteSupported => Metadata switch
    {
        { Generation: DeviceGeneration.V2, FirmwareMajor: 2 } => true,
        { Generation: DeviceGeneration.V3, FirmwareMajor: 3 } => _allowExperimentalWrites,
        _ => false,
    };

    /// <inheritdoc />
    public async Task<DeviceInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var space = await _storage.GetSpaceAsync(cancellationToken).ConfigureAwait(false);
        return new DeviceInfo(Generation, Metadata.Firmware, Metadata.SerialNumber, space?.Total, space?.Free, _storage.Name, IsWriteSupported);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledPack>> ListPacksAsync(CancellationToken cancellationToken = default)
    {
        var ids = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
        var packs = new List<InstalledPack>(ids.Count);
        foreach (var id in ids)
        {
            var folder = FolderOf(id);
            var files = await _storage.ListFilesAsync(folder, cancellationToken).ConfigureAwait(false);
            var niPath = $"{folder}/{FsPackFormat.NodeIndex}";
            if (!files.Contains(niPath))
            {
                packs.Add(new InstalledPack(id, 0, 0, false, IsValid: false));
                continue;
            }
            var ni = await _storage.ReadAllBytesAsync(niPath, cancellationToken).ConfigureAwait(false);
            var version = ni.Length >= 4 ? BitConverter.ToInt16(ni, 2) : (short)0;
            long size = 0;
            foreach (var f in files) size += await _storage.GetFileSizeAsync(f, cancellationToken).ConfigureAwait(false);
            packs.Add(new InstalledPack(id, version, size, files.Contains($"{folder}/{FsPackFormat.NightMode}")));
        }
        return packs;
    }

    /// <inheritdoc />
    public async Task InstallAsync(StoryPack pack, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        EnsureWritable();

        PackFolder clear;
        try
        {
            clear = FsPackFormat.Write(pack);
        }
        catch (InvalidDataException e)
        {
            throw new DeviceException($"Le pack n'est pas prêt pour l'appareil : {e.Message}", e);
        }

        var files = Scramble(clear);
        var size = files.Values.Sum(v => (long)v.Length);
        var space = await _storage.GetSpaceAsync(cancellationToken).ConfigureAwait(false);
        if (space is { } s && s.Free < size)
        {
            throw new DeviceException($"Espace insuffisant sur l'appareil ({size / 1024 / 1024} Mo nécessaires, {s.Free / 1024 / 1024} Mo libres).");
        }

        progress?.Report(new(TransferPhase.BackingUp, 0));
        var index = (await ReadIndexAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var folder = FolderOf(pack.Id);
        var replacing = index.Contains(pack.Id);
        if (replacing)
        {
            // Unlist first so the firmware never sees a half-written pack.
            index.Remove(pack.Id);
            await WriteIndexAsync(index, cancellationToken).ConfigureAwait(false);
        }
        await _storage.DeleteDirectoryAsync(folder, cancellationToken).ConfigureAwait(false);

        try
        {
            long written = 0;
            foreach (var (path, data) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _storage.WriteAllBytesAsync($"{folder}/{path}", data, cancellationToken).ConfigureAwait(false);
                written += data.Length;
                progress?.Report(new(TransferPhase.Writing, 0.05 + 0.75 * written / Math.Max(1, size)));
            }

            long verified = 0;
            foreach (var (path, data) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var back = await _storage.ReadAllBytesAsync($"{folder}/{path}", cancellationToken).ConfigureAwait(false);
                if (!back.AsSpan().SequenceEqual(data))
                {
                    throw new DeviceException($"Vérification échouée pour {path} : le fichier relu diffère de celui écrit.");
                }
                verified += data.Length;
                progress?.Report(new(TransferPhase.Verifying, 0.8 + 0.15 * verified / Math.Max(1, size)));
            }
        }
        catch
        {
            await _storage.DeleteDirectoryAsync(folder, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        progress?.Report(new(TransferPhase.Committing, 0.97));
        index.Add(pack.Id);
        await WriteIndexAsync(index, cancellationToken).ConfigureAwait(false);
        progress?.Report(new(TransferPhase.Done, 1));
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Guid packId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        var index = (await ReadIndexAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (!index.Remove(packId)) throw new DeviceException("Ce pack n'est pas sur l'appareil.");
        await WriteIndexAsync(index, cancellationToken).ConfigureAwait(false);
        await _storage.DeleteDirectoryAsync(FolderOf(packId), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReorderAsync(IReadOnlyList<Guid> order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        EnsureWritable();
        var index = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
        if (order.Count != index.Count || !order.ToHashSet().SetEquals(index))
        {
            throw new DeviceException("Le nouvel ordre ne correspond pas aux packs présents sur l'appareil.");
        }
        await WriteIndexAsync(order, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StoryPack> ExportAsync(Guid packId, CancellationToken cancellationToken = default)
    {
        var folder = FolderOf(packId);
        var files = await _storage.ListFilesAsync(folder, cancellationToken).ConfigureAwait(false);
        if (files.Count == 0) throw new DeviceException("Ce pack n'est pas sur l'appareil.");

        var clear = new PackFolder();
        foreach (var file in files)
        {
            var relative = file[(folder.Length + 1)..];
            var data = await _storage.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            clear[relative] = Unscramble(relative, data);
        }
        try
        {
            return FsPackFormat.Read(clear, packId);
        }
        catch (InvalidDataException e)
        {
            throw new DeviceException($"Pack illisible : {e.Message}", e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, byte[]>> SnapshotIndexAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [DeviceMetadata.FileName] = await _storage.ReadAllBytesAsync(DeviceMetadata.FileName, cancellationToken).ConfigureAwait(false),
        };
        if (await _storage.FileExistsAsync(PackIndex.FileName, cancellationToken).ConfigureAwait(false))
        {
            snapshot[PackIndex.FileName] = await _storage.ReadAllBytesAsync(PackIndex.FileName, cancellationToken).ConfigureAwait(false);
        }
        return snapshot;
    }

    /// <inheritdoc />
    public async Task RestoreIndexAsync(IReadOnlyDictionary<string, byte[]> snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // The metadata file is never rewritten: it identifies the device and holds its keys.
        // A snapshot without index means the device had no pack.
        var pi = snapshot.TryGetValue(PackIndex.FileName, out var saved) ? saved : [];
        await _storage.WriteAllBytesAsync(PackIndex.FileName, pi, cancellationToken).ConfigureAwait(false);
    }

    private PackFolder Scramble(PackFolder clear)
    {
        var output = new PackFolder();
        foreach (var (path, data) in clear)
        {
            output[path] = DeviceCiphers.IsClearFile(Path.GetFileName(path)) ? data : Generation switch
            {
                DeviceGeneration.V2 => DeviceCiphers.EncryptCommonKey(data),
                DeviceGeneration.V3 => DeviceCiphers.EncryptV3(data, Metadata.V3Key!),
                _ => throw new DeviceException("Génération d'appareil non supportée."),
            };
        }

        output[FsPackFormat.Boot] = Generation == DeviceGeneration.V2
            ? DeviceCiphers.BuildBootFileV2(output[FsPackFormat.ImageIndex], Metadata.DeviceUuid!)
            : Metadata.V3Key!.BootFile;
        return output;
    }

    private byte[] Unscramble(string path, byte[] data)
    {
        if (DeviceCiphers.IsClearFile(Path.GetFileName(path))) return data;
        return Generation switch
        {
            DeviceGeneration.V2 => DeviceCiphers.DecryptCommonKey(data),
            DeviceGeneration.V3 => DeviceCiphers.DecryptV3(data, Metadata.V3Key!),
            _ => data,
        };
    }

    private void EnsureWritable()
    {
        if (!IsWriteSupported)
        {
            throw new DeviceException(Generation == DeviceGeneration.V3
                ? "L'écriture sur les appareils v3 est expérimentale : activez-la explicitement dans les réglages."
                : $"Firmware {Metadata.Firmware} non supporté en écriture.");
        }
    }

    private async Task<IReadOnlyList<Guid>> ReadIndexAsync(CancellationToken cancellationToken) =>
        await _storage.FileExistsAsync(PackIndex.FileName, cancellationToken).ConfigureAwait(false)
            ? PackIndex.Read(await _storage.ReadAllBytesAsync(PackIndex.FileName, cancellationToken).ConfigureAwait(false))
            : [];

    private async Task WriteIndexAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var data = PackIndex.Write(ids);
        await _storage.WriteAllBytesAsync(PackIndex.FileName, data, cancellationToken).ConfigureAwait(false);
        var back = await _storage.ReadAllBytesAsync(PackIndex.FileName, cancellationToken).ConfigureAwait(false);
        if (!back.AsSpan().SequenceEqual(data)) throw new DeviceException("L'index des packs n'a pas pu être écrit correctement.");
    }

    private static string FolderOf(Guid id) => ContentPrefix + PackIndex.FolderName(id);
}
