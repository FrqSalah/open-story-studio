using System.Buffers.Binary;
using Studio.Core.Model;
using Studio.Formats.Assets;

namespace Studio.Formats.Raw;

/// <summary>
/// Binary pack image of firmware v1 devices ("raw" format): a sequence of 512-byte sectors written
/// directly to the SD card. Sector 0 is the header, then one sector per stage node, one per action node,
/// then the assets (24-bit BMP and WAVE), then a 512-byte trailer.
/// </summary>
/// <remarks>Ported from STUdio's <c>BinaryStoryPackWriter</c> (MPL-2.0), without the enriched metadata extension.</remarks>
public static class RawPackFormat
{
    /// <summary>SD sector size.</summary>
    public const int SectorSize = 512;

    /// <summary>Sample rate of WAVE files on v1 devices.</summary>
    public const int WaveSampleRate = 32000;

    /// <summary>Writes the binary image of a pack.</summary>
    /// <exception cref="InvalidDataException">An asset is not in the expected format.</exception>
    public static byte[] Write(StoryPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.StageNodes.Count == 0) throw new InvalidDataException("Le pack ne contient aucune étape.");

        // Assign a sector to each action node, then to each asset.
        var actionSectors = new Dictionary<ActionNode, int>();
        var next = pack.StageNodes.Count;
        foreach (var stage in pack.StageNodes)
        {
            foreach (var action in new[] { stage.OkTransition, stage.HomeTransition }.OfType<Transition>().Select(t => t.Action))
            {
                if (!actionSectors.ContainsKey(action)) actionSectors[action] = next++;
            }
        }

        var assetSectors = new Dictionary<string, (int Offset, int Size, byte[] Data)>(StringComparer.Ordinal);
        foreach (var image in pack.StageNodes.Select(s => s.Image).OfType<MediaAsset>().DistinctBy(a => a.Hash))
        {
            var info = AssetInspector.ReadBmpInfo(image.Data);
            if (info is not { Width: AssetInspector.ScreenWidth, Height: AssetInspector.ScreenHeight })
                throw new InvalidDataException("Les images des appareils v1 doivent être des BMP 320x240.");
            next = Add(image, next);
        }
        foreach (var audio in pack.StageNodes.Select(s => s.Audio).OfType<MediaAsset>().DistinctBy(a => a.Hash).Where(a => !assetSectors.ContainsKey(a.Hash)))
        {
            if (CheckWave(audio.Data) is { } problem) throw new InvalidDataException(problem);
            next = Add(audio, next);
        }

        var stageIndexes = pack.StageNodes.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        // Sector 0 is the header; node/asset offset k lives in sector k + 1.
        var output = new byte[(long)(next + 1) * SectorSize + EmbeddedData.V1CheckBytes.Length];
        var span = output.AsSpan();

        // Header sector.
        BinaryPrimitives.WriteInt16BigEndian(span, (short)pack.StageNodes.Count);
        span[2] = (byte)(pack.FactoryDisabled ? 1 : 0);
        BinaryPrimitives.WriteInt16BigEndian(span[3..], pack.Version);

        // Stage node sectors (from sector 1).
        for (var i = 0; i < pack.StageNodes.Count; i++)
        {
            var stage = pack.StageNodes[i];
            var s = span.Slice((i + 1) * SectorSize, SectorSize);
            stage.Id.TryWriteBytes(s, bigEndian: true, out _);
            WriteAsset(s[16..], stage.Image);
            WriteAsset(s[24..], stage.Audio);
            WriteTransition(s[32..], stage.OkTransition);
            WriteTransition(s[38..], stage.HomeTransition);
            BinaryPrimitives.WriteInt16BigEndian(s[44..], Flag(stage.Controls.Wheel));
            BinaryPrimitives.WriteInt16BigEndian(s[46..], Flag(stage.Controls.Ok));
            BinaryPrimitives.WriteInt16BigEndian(s[48..], Flag(stage.Controls.Home));
            BinaryPrimitives.WriteInt16BigEndian(s[50..], Flag(stage.Controls.Pause));
            BinaryPrimitives.WriteInt16BigEndian(s[52..], Flag(stage.Controls.Autoplay));
        }

        // Action node sectors.
        foreach (var (action, sector) in actionSectors)
        {
            var s = span.Slice((sector + 1) * SectorSize, SectorSize);
            if (action.Options.Count > SectorSize / 2) throw new InvalidDataException($"Le choix « {action.Name} » a trop d'options.");
            for (var o = 0; o < action.Options.Count; o++)
            {
                BinaryPrimitives.WriteInt16BigEndian(s[(o * 2)..], (short)stageIndexes[action.Options[o]]);
            }
        }

        // Assets.
        foreach (var (offset, _, data) in assetSectors.Values)
        {
            data.CopyTo(span[((offset + 1) * SectorSize)..]);
        }

        EmbeddedData.V1CheckBytes.CopyTo(span[((next + 1) * SectorSize)..]);
        return output;

        int Add(MediaAsset asset, int offset)
        {
            var sectors = (asset.Data.Length + SectorSize - 1) / SectorSize;
            assetSectors[asset.Hash] = (offset, sectors, asset.Data);
            return offset + sectors;
        }

        void WriteAsset(Span<byte> s, MediaAsset? asset)
        {
            var (offset, size) = asset is null ? (-1, -1) : (assetSectors[asset.Hash].Offset, assetSectors[asset.Hash].Size);
            BinaryPrimitives.WriteInt32BigEndian(s, offset);
            BinaryPrimitives.WriteInt32BigEndian(s[4..], size);
        }

        void WriteTransition(Span<byte> s, Transition? t)
        {
            BinaryPrimitives.WriteInt16BigEndian(s, (short)(t is null ? -1 : actionSectors[t.Action]));
            BinaryPrimitives.WriteInt16BigEndian(s[2..], (short)(t is null ? -1 : t.Action.Options.Count));
            BinaryPrimitives.WriteInt16BigEndian(s[4..], (short)(t is null ? -1 : t.OptionIndex));
        }
    }

    /// <summary>Number of sectors of a pack image (rounded up).</summary>
    public static int SectorCount(int byteLength) => (byteLength + SectorSize - 1) / SectorSize;

    /// <summary>Checks a WAVE file is mono, 16-bit, 32 kHz PCM.</summary>
    public static string? CheckWave(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[8] != 'W' || wav[12] != 'f') return "le son doit être un fichier WAVE";
        var format = BinaryPrimitives.ReadInt16LittleEndian(wav[20..]);
        var channels = BinaryPrimitives.ReadInt16LittleEndian(wav[22..]);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(wav[24..]);
        var bits = BinaryPrimitives.ReadInt16LittleEndian(wav[34..]);
        return format == 1 && channels == 1 && rate == WaveSampleRate && bits == 16
            ? null
            : $"le son doit être en WAVE PCM 16 bits mono {WaveSampleRate} Hz";
    }

    private static short Flag(bool value) => (short)(value ? 1 : 0);
}
