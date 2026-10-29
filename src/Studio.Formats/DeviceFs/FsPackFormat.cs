using System.Buffers.Binary;
using System.Text;
using Studio.Core.Model;
using Studio.Formats.Assets;

namespace Studio.Formats.DeviceFs;

/// <summary>
/// Files of one pack folder (<c>.content/XXXXXXXX/</c>), keyed by relative path with '/' separators.
/// Content is always kept in clear text; scrambling happens in the device driver.
/// </summary>
public sealed class PackFolder : SortedDictionary<string, byte[]>
{
    /// <summary>Creates an empty folder.</summary>
    public PackFolder() : base(StringComparer.Ordinal) { }

    /// <summary>Total size in bytes.</summary>
    public long Size => Values.Sum(v => (long)v.Length);
}

/// <summary>
/// Pack format of firmware v2/v3 devices ("FS" format in STUdio): <c>ni</c> (stage nodes), <c>li</c> (action
/// nodes), <c>ri</c>/<c>rf/</c> (images), <c>si</c>/<c>sf/</c> (sounds), <c>nm</c> (night mode).
/// </summary>
/// <remarks>Ported from STUdio's <c>FsStoryPackWriter</c> / <c>FsStoryPackReader</c> (MPL-2.0).</remarks>
public static class FsPackFormat
{
    /// <summary>Stage nodes index.</summary>
    public const string NodeIndex = "ni";
    /// <summary>Action nodes (lists) index.</summary>
    public const string ListIndex = "li";
    /// <summary>Images index.</summary>
    public const string ImageIndex = "ri";
    /// <summary>Sounds index.</summary>
    public const string SoundIndex = "si";
    /// <summary>Images folder.</summary>
    public const string ImageFolder = "rf";
    /// <summary>Sounds folder.</summary>
    public const string SoundFolder = "sf";
    /// <summary>Night mode marker.</summary>
    public const string NightMode = "nm";
    /// <summary>Boot (authorization) file.</summary>
    public const string Boot = "bt";

    private const int HeaderSize = 512;
    private const int StageNodeSize = 44;
    private const int IndexEntrySize = 12;

    /// <summary>
    /// Writes a pack. Assets must already be device-ready (see <see cref="AssetInspector"/>):
    /// 320x240 4-bit RLE BMP images and mono 44.1 kHz MP3 sounds without ID3 tags.
    /// </summary>
    /// <exception cref="InvalidDataException">An asset is not in the expected format.</exception>
    public static PackFolder Write(StoryPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.StageNodes.Count == 0) throw new InvalidDataException("Le pack ne contient aucune étape.");

        var images = new List<byte[]>();
        var imageIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var sounds = new List<byte[]>();
        var soundIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var actionOffsets = new Dictionary<ActionNode, int>();
        var actionsOrdered = new List<ActionNode>();
        var nextActionOffset = 0;
        var stageIndexes = pack.StageNodes.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);

        var nodes = new byte[pack.StageNodes.Count * StageNodeSize];
        for (var i = 0; i < pack.StageNodes.Count; i++)
        {
            var stage = pack.StageNodes[i];
            var imageIndex = -1;
            if (stage.Image is { } image)
            {
                if (AssetInspector.CheckDeviceImage(image.Data) is { } problem)
                    throw new InvalidDataException($"« {stage.Name} » : {problem}.");
                imageIndex = IndexOf(image, images, imageIndexes);
            }

            var audio = stage.Audio ?? new MediaAsset(EmbeddedData.BlankMp3, MediaTypes.Mp3, "blank.mp3");
            if (AssetInspector.CheckDeviceAudio(audio.Data) is { } audioProblem)
                throw new InvalidDataException($"« {stage.Name} » : {audioProblem}.");
            var soundIndex = IndexOf(audio, sounds, soundIndexes);

            Register(stage.OkTransition);
            Register(stage.HomeTransition);

            var n = nodes.AsSpan(i * StageNodeSize, StageNodeSize);
            BinaryPrimitives.WriteInt32LittleEndian(n[0..], imageIndex);
            BinaryPrimitives.WriteInt32LittleEndian(n[4..], soundIndex);
            WriteTransition(n[8..], stage.OkTransition);
            WriteTransition(n[20..], stage.HomeTransition);
            BinaryPrimitives.WriteInt16LittleEndian(n[32..], Flag(stage.Controls.Wheel));
            BinaryPrimitives.WriteInt16LittleEndian(n[34..], Flag(stage.Controls.Ok));
            BinaryPrimitives.WriteInt16LittleEndian(n[36..], Flag(stage.Controls.Home));
            BinaryPrimitives.WriteInt16LittleEndian(n[38..], Flag(stage.Controls.Pause));
            BinaryPrimitives.WriteInt16LittleEndian(n[40..], Flag(stage.Controls.Autoplay));
        }

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(0), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(2), pack.Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), StageNodeSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), pack.StageNodes.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), images.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), sounds.Count);
        header[24] = (byte)(pack.FactoryDisabled ? 1 : 0);

        var li = new byte[nextActionOffset * 4];
        foreach (var action in actionsOrdered)
        {
            var offset = actionOffsets[action];
            for (var o = 0; o < action.Options.Count; o++)
            {
                if (!stageIndexes.TryGetValue(action.Options[o], out var target))
                    throw new InvalidDataException($"Le choix « {action.Name} » contient une étape qui n'appartient pas au pack.");
                BinaryPrimitives.WriteInt32LittleEndian(li.AsSpan((offset + o) * 4), target);
            }
        }

        var folder = new PackFolder
        {
            [NodeIndex] = [.. header, .. nodes],
            [ListIndex] = li,
            [ImageIndex] = BuildIndex(images.Count),
            [SoundIndex] = BuildIndex(sounds.Count),
        };
        for (var i = 0; i < images.Count; i++) folder[$"{ImageFolder}/{AssetPath(i)}"] = images[i];
        for (var i = 0; i < sounds.Count; i++) folder[$"{SoundFolder}/{AssetPath(i)}"] = sounds[i];
        if (pack.NightModeAvailable) folder[NightMode] = [];
        return folder;

        void Register(Transition? t)
        {
            if (t is null || actionOffsets.ContainsKey(t.Action)) return;
            actionOffsets[t.Action] = nextActionOffset;
            actionsOrdered.Add(t.Action);
            nextActionOffset += t.Action.Options.Count;
        }

        void WriteTransition(Span<byte> span, Transition? t)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[0..], t is null ? -1 : actionOffsets[t.Action]);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], t is null ? -1 : t.Action.Options.Count);
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], t is null ? -1 : t.OptionIndex);
        }
    }

    /// <summary>Reads a clear-text pack folder.</summary>
    /// <param name="folder">Pack files.</param>
    /// <param name="packId">Pack id (from the device index); becomes the id of the first stage node.</param>
    public static StoryPack Read(IReadOnlyDictionary<string, byte[]> folder, Guid packId)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var ni = Get(folder, NodeIndex);
        var li = folder.TryGetValue(ListIndex, out var l) ? l : [];
        var ri = folder.TryGetValue(ImageIndex, out var r) ? r : [];
        var si = folder.TryGetValue(SoundIndex, out var s) ? s : [];
        if (ni.Length < HeaderSize) throw new InvalidDataException("Fichier ni tronqué.");

        var version = BinaryPrimitives.ReadInt16LittleEndian(ni.AsSpan(2));
        var nodesStart = BinaryPrimitives.ReadInt32LittleEndian(ni.AsSpan(4));
        var nodeSize = BinaryPrimitives.ReadInt32LittleEndian(ni.AsSpan(8));
        var stageCount = BinaryPrimitives.ReadInt32LittleEndian(ni.AsSpan(12));
        var factoryDisabled = ni[24] != 0;
        if (nodeSize < StageNodeSize - 2 || nodesStart + (long)stageCount * nodeSize > ni.Length)
            throw new InvalidDataException("Fichier ni incohérent.");

        var pack = new StoryPack
        {
            Title = $"Pack {PackIndex.FolderName(packId)}",
            Version = version,
            FactoryDisabled = factoryDisabled,
            NightModeAvailable = folder.ContainsKey(NightMode),
        };

        var raw = new (int ok, int okCount, int okOpt, int home, int homeCount, int homeOpt)[stageCount];
        var imageCache = new Dictionary<int, MediaAsset>();
        var soundCache = new Dictionary<int, MediaAsset>();
        for (var i = 0; i < stageCount; i++)
        {
            var n = ni.AsSpan(nodesStart + i * nodeSize, nodeSize);
            var stage = new StageNode(i == 0 ? packId : Guid.NewGuid())
            {
                Name = i == 0 ? "Couverture" : $"Étape {i}",
                Image = Asset(BinaryPrimitives.ReadInt32LittleEndian(n[0..]), ri, ImageFolder, MediaTypes.Bmp, imageCache),
                Audio = Asset(BinaryPrimitives.ReadInt32LittleEndian(n[4..]), si, SoundFolder, MediaTypes.Mp3, soundCache),
                Controls = new ControlSettings(
                    Wheel: BinaryPrimitives.ReadInt16LittleEndian(n[32..]) != 0,
                    Ok: BinaryPrimitives.ReadInt16LittleEndian(n[34..]) != 0,
                    Home: BinaryPrimitives.ReadInt16LittleEndian(n[36..]) != 0,
                    Pause: BinaryPrimitives.ReadInt16LittleEndian(n[38..]) != 0,
                    Autoplay: BinaryPrimitives.ReadInt16LittleEndian(n[40..]) != 0),
            };
            raw[i] = (
                BinaryPrimitives.ReadInt32LittleEndian(n[8..]), BinaryPrimitives.ReadInt32LittleEndian(n[12..]), BinaryPrimitives.ReadInt32LittleEndian(n[16..]),
                BinaryPrimitives.ReadInt32LittleEndian(n[20..]), BinaryPrimitives.ReadInt32LittleEndian(n[24..]), BinaryPrimitives.ReadInt32LittleEndian(n[28..]));
            pack.StageNodes.Add(stage);
        }

        var actions = new SortedDictionary<int, ActionNode>();
        for (var i = 0; i < stageCount; i++)
        {
            var (ok, okCount, okOpt, home, homeCount, homeOpt) = raw[i];
            pack.StageNodes[i].OkTransition = Link(ok, okCount, okOpt);
            pack.StageNodes[i].HomeTransition = Link(home, homeCount, homeOpt);
        }
        var number = 1;
        foreach (var action in actions.Values)
        {
            action.Name = $"Choix {number++}";
            pack.ActionNodes.Add(action);
        }
        return pack;

        Transition? Link(int offset, int count, int option)
        {
            if (offset < 0 || count <= 0 || option < 0) return null;
            if (!actions.TryGetValue(offset, out var action))
            {
                action = new ActionNode();
                for (var o = 0; o < count; o++)
                {
                    var pos = (offset + o) * 4;
                    if (pos + 4 > li.Length) throw new InvalidDataException("Fichier li tronqué.");
                    var target = BinaryPrimitives.ReadInt32LittleEndian(li.AsSpan(pos));
                    if (target < 0 || target >= pack.StageNodes.Count) throw new InvalidDataException("Fichier li incohérent.");
                    action.Options.Add(pack.StageNodes[target]);
                }
                actions[offset] = action;
            }
            return new Transition(action, Math.Min(option, action.Options.Count - 1));
        }

        MediaAsset? Asset(int index, byte[] indexFile, string assetFolder, string mime, Dictionary<int, MediaAsset> cache)
        {
            if (index < 0) return null;
            if (cache.TryGetValue(index, out var cached)) return cached;
            if ((index + 1) * IndexEntrySize > indexFile.Length) throw new InvalidDataException($"Index {assetFolder} incohérent.");
            var path = Encoding.ASCII.GetString(indexFile, index * IndexEntrySize, IndexEntrySize).Replace('\\', '/');
            var asset = new MediaAsset(Get(folder, $"{assetFolder}/{path}"), mime, Path.GetFileName(path) + MediaTypes.ExtensionOf(mime));
            cache[index] = asset;
            return asset;
        }
    }

    /// <summary>Relative asset path for an index, e.g. <c>000/00000003</c>.</summary>
    public static string AssetPath(int index) => $"000/{index:D8}";

    private static byte[] BuildIndex(int count)
    {
        var index = new byte[count * IndexEntrySize];
        for (var i = 0; i < count; i++)
        {
            Encoding.ASCII.GetBytes(AssetPath(i).Replace('/', '\\')).CopyTo(index, i * IndexEntrySize);
        }
        return index;
    }

    private static int IndexOf(MediaAsset asset, List<byte[]> list, Dictionary<string, int> indexes)
    {
        if (!indexes.TryGetValue(asset.Hash, out var index))
        {
            index = list.Count;
            list.Add(asset.Data);
            indexes[asset.Hash] = index;
        }
        return index;
    }

    private static short Flag(bool value) => (short)(value ? 1 : 0);

    private static byte[] Get(IReadOnlyDictionary<string, byte[]> folder, string path) =>
        folder.TryGetValue(path, out var data) ? data : throw new InvalidDataException($"Fichier manquant dans le pack : {path}.");
}
