using System.Buffers.Binary;
using System.Globalization;
using Studio.Core.Model;
using Studio.Formats.DeviceFs;
using Studio.Formats.Raw;

namespace Studio.Device.Raw;

/// <summary>
/// Firmware v1 device: packs are raw images on the SD card, listed in a pack index at sector 100000.
/// </summary>
/// <remarks>Ported from STUdio's <c>RawStoryTellerAsyncDriver</c> (MPL-2.0). Writing is experimental.</remarks>
public sealed class RawStoryDevice(IRawSdAccess sd, string location, bool allowExperimentalWrites = false) : IStoryDevice
{
    /// <summary>Sector of the pack index.</summary>
    public const int PackIndexSector = 100000;

    private const int DefaultCardSizeSectors = 6815513;
    private const int Fat16PartitionSectors = 20480;
    private const int DeviceInfoSpiOffset = 520192;
    private const int DeviceInfoSdSector = 2;
    private const int ChunkSectors = 5000;
    private const string IndexSnapshotName = "raw-pack-index";
    private const int MaxPacks = (RawSdCard.SectorSize - 2) / 12;

    private sealed record Entry(int StartSector, int SizeSectors, short StatsOffset, short SamplingRate);

    /// <inheritdoc />
    public DeviceGeneration Generation => DeviceGeneration.V1;

    /// <inheritdoc />
    public async Task<DeviceInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var spi = await sd.ReadSpiAsync(DeviceInfoSpiOffset, 1, cancellationToken).ConfigureAwait(false);
        var sn = BinaryPrimitives.ReadInt64BigEndian(spi);
        var serial = sn is 0 or -1 or -4294967296L ? null : sn.ToString("D14", CultureInfo.InvariantCulture);

        var s2 = await sd.ReadSdAsync(DeviceInfoSdSector, 1, cancellationToken).ConfigureAwait(false);
        var (major, minor) = FirmwareVersion(s2);
        var cardSectors = CardSizeSectors(s2);
        var entries = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
        var used = entries.Sum(e => (long)e.Entry.SizeSectors);
        return new DeviceInfo(
            DeviceGeneration.V1,
            major < 0 ? "1.x" : $"{major}.{minor}",
            serial,
            (long)cardSectors * RawSdCard.SectorSize,
            (cardSectors - used) * RawSdCard.SectorSize,
            location,
            allowExperimentalWrites);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledPack>> ListPacksAsync(CancellationToken cancellationToken = default) =>
        (await ReadIndexAsync(cancellationToken).ConfigureAwait(false))
            .Select(e => new InstalledPack(e.Id, e.Version, (long)e.Entry.SizeSectors * RawSdCard.SectorSize, false))
            .ToList();

    /// <inheritdoc />
    public async Task InstallAsync(StoryPack pack, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        EnsureWritable();
        byte[] image;
        try
        {
            image = RawPackFormat.Write(pack);
        }
        catch (InvalidDataException e)
        {
            throw new DeviceException($"Le pack n'est pas prêt pour l'appareil : {e.Message}", e);
        }

        var sectors = RawPackFormat.SectorCount(image.Length);
        Array.Resize(ref image, sectors * RawSdCard.SectorSize);

        var entries = (await ReadIndexAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (entries.FindIndex(e => e.Id == pack.Id) is >= 0 and var existing)
        {
            entries.RemoveAt(existing);
            await WriteIndexAsync(entries.Select(e => e.Entry), cancellationToken).ConfigureAwait(false);
        }

        if (entries.Count >= MaxPacks) throw new DeviceException($"L'appareil ne peut pas contenir plus de {MaxPacks} packs.");
        var info = await sd.ReadSdAsync(DeviceInfoSdSector, 1, cancellationToken).ConfigureAwait(false);
        var cardSectors = CardSizeSectors(info);
        var start = FindFreeSpace(entries.Select(e => e.Entry), sectors, cardSectors)
            ?? throw new DeviceException("Espace insuffisant sur l'appareil.");

        for (var offset = 0; offset < sectors; offset += ChunkSectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(ChunkSectors, sectors - offset);
            await sd.WriteSdAsync(PackIndexSector + start + offset, image.AsMemory(offset * RawSdCard.SectorSize, count * RawSdCard.SectorSize), cancellationToken).ConfigureAwait(false);
            progress?.Report(new(TransferPhase.Writing, 0.9 * (offset + count) / sectors));
        }

        progress?.Report(new(TransferPhase.Committing, 0.95));
        var list = entries.Select(e => e.Entry).ToList();
        list.Add(new Entry(start, sectors, 0, 0));
        await WriteIndexAsync(list, cancellationToken).ConfigureAwait(false);
        progress?.Report(new(TransferPhase.Done, 1));
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Guid packId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        var entries = (await ReadIndexAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (entries.RemoveAll(e => e.Id == packId) == 0) throw new DeviceException("Ce pack n'est pas sur l'appareil.");
        await WriteIndexAsync(entries.Select(e => e.Entry), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReorderAsync(IReadOnlyList<Guid> order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        EnsureWritable();
        var entries = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
        if (order.Count != entries.Count || !order.ToHashSet().SetEquals(entries.Select(e => e.Id)))
        {
            throw new DeviceException("Le nouvel ordre ne correspond pas aux packs présents sur l'appareil.");
        }
        await WriteIndexAsync(order.Select(id => entries.First(e => e.Id == id).Entry), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<StoryPack> ExportAsync(Guid packId, CancellationToken cancellationToken = default) =>
        Task.FromException<StoryPack>(new DeviceException("L'export des packs depuis un appareil v1 n'est pas encore supporté."));

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, byte[]>> SnapshotIndexAsync(CancellationToken cancellationToken = default) =>
        new Dictionary<string, byte[]> { [IndexSnapshotName] = await sd.ReadSdAsync(PackIndexSector, 1, cancellationToken).ConfigureAwait(false) };

    /// <inheritdoc />
    public async Task RestoreIndexAsync(IReadOnlyDictionary<string, byte[]> snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TryGetValue(IndexSnapshotName, out var sector)) await sd.WriteSdAsync(PackIndexSector, sector, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureWritable()
    {
        if (!allowExperimentalWrites)
            throw new DeviceException("L'écriture sur les appareils v1 est expérimentale : activez-la explicitement dans les réglages.");
    }

    private static (int Major, int Minor) FirmwareVersion(byte[] sector2)
    {
        // The label "version" is stored as UTF-16LE at the beginning of the sector.
        var label = new string([(char)sector2[0], (char)sector2[2], (char)sector2[4], (char)sector2[6], (char)sector2[8], (char)sector2[10], (char)sector2[12]]);
        return label == "version" ? (sector2[16], sector2[20]) : (-1, -1);
    }

    /// <summary>Usable sectors after the pack index sector.</summary>
    private static int CardSizeSectors(byte[] sector2)
    {
        var (major, minor) = FirmwareVersion(sector2);
        var size = -1;
        if (major >= 1 && minor >= 1)
        {
            // Mixed word order as stored by the firmware (STUdio reads byte 26 twice, which looks like a typo).
            size = (sector2[26] << 24) | (sector2[27] << 16) | (sector2[24] << 8) | sector2[25];
        }
        size = size <= 0 ? DefaultCardSizeSectors : size - Fat16PartitionSectors;
        return size - PackIndexSector;
    }

    private async Task<IReadOnlyList<(Guid Id, short Version, Entry Entry)>> ReadIndexAsync(CancellationToken cancellationToken)
    {
        var index = await sd.ReadSdAsync(PackIndexSector, 1, cancellationToken).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadInt16BigEndian(index);
        var result = new List<(Guid, short, Entry)>();
        for (var i = 0; i < count && 2 + (i + 1) * 12 <= index.Length; i++)
        {
            var e = index.AsSpan(2 + i * 12);
            var entry = new Entry(
                BinaryPrimitives.ReadInt32BigEndian(e),
                BinaryPrimitives.ReadInt32BigEndian(e[4..]),
                BinaryPrimitives.ReadInt16BigEndian(e[8..]),
                BinaryPrimitives.ReadInt16BigEndian(e[10..]));
            var head = await sd.ReadSdAsync(PackIndexSector + entry.StartSector, 2, cancellationToken).ConfigureAwait(false);
            var version = BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(3));
            var id = new Guid(head.AsSpan(RawSdCard.SectorSize, 16), bigEndian: true);
            result.Add((id, version == 0 ? (short)1 : version, entry));
        }
        return result;
    }

    private async Task WriteIndexAsync(IEnumerable<Entry> entries, CancellationToken cancellationToken)
    {
        var list = entries.ToList();
        var sector = new byte[RawSdCard.SectorSize];
        BinaryPrimitives.WriteInt16BigEndian(sector, (short)list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var e = sector.AsSpan(2 + i * 12);
            BinaryPrimitives.WriteInt32BigEndian(e, list[i].StartSector);
            BinaryPrimitives.WriteInt32BigEndian(e[4..], list[i].SizeSectors);
            BinaryPrimitives.WriteInt16BigEndian(e[8..], list[i].StatsOffset);
            BinaryPrimitives.WriteInt16BigEndian(e[10..], list[i].SamplingRate);
        }
        await sd.WriteSdAsync(PackIndexSector, sector, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>First gap (in sectors relative to the index sector) large enough for a pack.</summary>
    private static int? FindFreeSpace(IEnumerable<Entry> entries, int size, int cardSectors)
    {
        var previousEnd = 0;
        foreach (var e in entries.OrderBy(e => e.StartSector))
        {
            if (e.StartSector - previousEnd - 1 >= size) return previousEnd + 1;
            previousEnd = e.StartSector + e.SizeSectors - 1;
        }
        return cardSectors - previousEnd >= size ? previousEnd + 1 : null;
    }

    /// <inheritdoc />
    public override string ToString() => $"v1 @ {location}";
}
