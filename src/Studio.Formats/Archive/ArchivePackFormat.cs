using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Studio.Core.Model;

namespace Studio.Formats.Archive;

/// <summary>
/// The library format: a zip containing <c>story.json</c>, <c>thumbnail.png</c> and <c>assets/*</c>.
/// Compatible with STUdio archives and Moiki exports.
/// </summary>
public static class ArchivePackFormat
{
    /// <summary>Descriptor file name.</summary>
    public const string StoryJson = "story.json";

    /// <summary>Thumbnail file name.</summary>
    public const string Thumbnail = "thumbnail.png";

    /// <summary>Assets folder prefix.</summary>
    public const string AssetsPrefix = "assets/";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Writes a pack archive.</summary>
    public static void Write(StoryPack pack, Stream output)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(output);

        var actionIds = pack.ActionNodes.ToDictionary(a => a, a => a.Id);
        var root = new JsonObject
        {
            ["format"] = "v1",
            ["title"] = pack.Title,
            ["description"] = pack.Description,
            ["version"] = pack.Version,
            ["nightModeAvailable"] = pack.NightModeAvailable,
            ["factoryDisabled"] = pack.FactoryDisabled,
            ["stageNodes"] = new JsonArray([.. pack.StageNodes.Select((s, i) => (JsonNode)StageToJson(s, i == 0))]),
            ["actionNodes"] = new JsonArray([.. pack.ActionNodes.Select(a => (JsonNode)ActionToJson(a))]),
        };

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var entry = zip.CreateEntry(StoryJson, CompressionLevel.Optimal);
        using (var json = entry.Open())
        {
            JsonSerializer.Serialize(json, root, Indented);
        }

        if (pack.Thumbnail is { Length: > 0 } thumb)
        {
            using var t = zip.CreateEntry(Thumbnail, CompressionLevel.NoCompression).Open();
            t.Write(thumb);
        }

        foreach (var asset in pack.Assets)
        {
            // Media files are already compressed: store them as is.
            using var a = zip.CreateEntry(AssetsPrefix + AssetName(asset), CompressionLevel.NoCompression).Open();
            a.Write(asset.Data);
        }

        JsonObject StageToJson(StageNode s, bool squareOne) => new()
        {
            ["uuid"] = s.Id.ToString(),
            ["type"] = s.Type ?? (squareOne ? "cover" : "stage"),
            ["name"] = s.Name,
            ["groupId"] = s.GroupId,
            ["position"] = PositionToJson(s.Position),
            ["squareOne"] = squareOne ? true : null,
            ["image"] = s.Image is null ? null : AssetName(s.Image),
            ["audio"] = s.Audio is null ? null : AssetName(s.Audio),
            ["okTransition"] = TransitionToJson(s.OkTransition),
            ["homeTransition"] = TransitionToJson(s.HomeTransition),
            ["controlSettings"] = new JsonObject
            {
                ["wheel"] = s.Controls.Wheel,
                ["ok"] = s.Controls.Ok,
                ["home"] = s.Controls.Home,
                ["pause"] = s.Controls.Pause,
                ["autoplay"] = s.Controls.Autoplay,
            },
        };

        JsonObject ActionToJson(ActionNode a) => new()
        {
            ["id"] = a.Id,
            ["type"] = a.Type ?? "action",
            ["name"] = a.Name,
            ["groupId"] = a.GroupId,
            ["position"] = PositionToJson(a.Position),
            ["options"] = new JsonArray([.. a.Options.Select(o => (JsonNode)JsonValue.Create(o.Id.ToString()))]),
        };

        JsonObject? TransitionToJson(Transition? t) => t is null ? null : new JsonObject
        {
            ["actionNode"] = actionIds.TryGetValue(t.Action, out var id) ? id : t.Action.Id,
            ["optionIndex"] = t.OptionIndex,
        };

        static JsonObject? PositionToJson(NodePosition? p) => p is null ? null : new JsonObject { ["x"] = p.Value.X, ["y"] = p.Value.Y };
    }

    /// <summary>Writes a pack archive to a byte array.</summary>
    public static byte[] Write(StoryPack pack)
    {
        using var ms = new MemoryStream();
        Write(pack, ms);
        return ms.ToArray();
    }

    /// <summary>Reads a pack archive.</summary>
    /// <exception cref="InvalidDataException">The archive is not a valid story pack.</exception>
    public static StoryPack Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);

        var storyEntry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(StoryJson, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("L'archive ne contient pas de story.json.");

        JsonObject root;
        using (var s = storyEntry.Open())
        {
            root = JsonNode.Parse(s)?.AsObject() ?? throw new InvalidDataException("story.json est vide.");
        }

        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        byte[]? thumbnail = null;
        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            if (entry.FullName.StartsWith(AssetsPrefix, StringComparison.Ordinal))
            {
                assets[entry.FullName[AssetsPrefix.Length..]] = ReadAll(entry);
            }
            else if (entry.FullName.Equals(Thumbnail, StringComparison.OrdinalIgnoreCase))
            {
                thumbnail = ReadAll(entry);
            }
        }

        var pack = new StoryPack
        {
            Title = Str(root, "title") ?? "Sans titre",
            Description = Str(root, "description"),
            Version = (short)(root["version"]?.GetValue<int>() ?? 1),
            NightModeAvailable = root["nightModeAvailable"]?.GetValue<bool>() ?? false,
            FactoryDisabled = root["factoryDisabled"]?.GetValue<bool>() ?? true,
            Thumbnail = thumbnail,
        };

        var actions = new Dictionary<string, ActionNode>(StringComparer.Ordinal);
        var actionNodes = (root["actionNodes"]?.AsArray() ?? []).OfType<JsonObject>().Select(a =>
            new ActionNode(Str(a, "id") ?? throw new InvalidDataException("Choix sans identifiant."))
            {
                Name = Str(a, "name") ?? "Choix",
                Type = Str(a, "type"),
                GroupId = Str(a, "groupId"),
                Position = Position(a),
            });
        foreach (var action in actionNodes)
        {
            actions[action.Id] = action;
            pack.ActionNodes.Add(action);
        }

        var assetCache = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);
        var stages = new Dictionary<string, StageNode>(StringComparer.OrdinalIgnoreCase);
        StageNode? squareOne = null;
        foreach (var s in (root["stageNodes"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            var id = Str(s, "uuid") ?? throw new InvalidDataException("Étape sans identifiant.");
            var controls = s["controlSettings"] as JsonObject;
            var stage = new StageNode(Guid.Parse(id))
            {
                Name = Str(s, "name") ?? "Étape",
                Type = Str(s, "type"),
                GroupId = Str(s, "groupId"),
                Position = Position(s),
                Image = AssetRef(Str(s, "image")),
                Audio = AssetRef(Str(s, "audio")),
                OkTransition = TransitionRef(s["okTransition"]),
                HomeTransition = TransitionRef(s["homeTransition"]),
                Controls = new ControlSettings(
                    Bool(controls, "wheel"), Bool(controls, "ok"), Bool(controls, "home"),
                    Bool(controls, "pause"), Bool(controls, "autoplay")),
            };
            if (s["squareOne"]?.GetValue<bool>() == true) squareOne = stage;
            stages[id] = stage;
            pack.StageNodes.Add(stage);
        }

        foreach (var a in (root["actionNodes"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            var action = actions[Str(a, "id")!];
            var optionIds = (a["options"]?.AsArray() ?? []).Select(o => o?.GetValue<string>()).OfType<string>();
            action.Options.AddRange(optionIds.Where(stages.ContainsKey).Select(id => stages[id]));
        }

        if (squareOne is not null) pack.SetSquareOne(squareOne);
        if (pack.StageNodes.Count == 0) throw new InvalidDataException("Le pack ne contient aucune étape.");
        return pack;

        MediaAsset? AssetRef(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (assetCache.TryGetValue(name, out var cached)) return cached;
            if (!assets.TryGetValue(name, out var data)) throw new InvalidDataException($"Fichier manquant dans l'archive : assets/{name}.");
            var asset = new MediaAsset(data, fileName: name);
            assetCache[name] = asset;
            return asset;
        }

        Transition? TransitionRef(JsonNode? t)
        {
            if (t is not JsonObject o) return null;
            var actionId = Str(o, "actionNode");
            if (actionId is null || !actions.TryGetValue(actionId, out var action)) return null;
            return new Transition(action, o["optionIndex"]?.GetValue<int>() ?? 0);
        }
    }

    /// <summary>Reads a pack archive from bytes.</summary>
    public static StoryPack Read(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        return Read(ms);
    }

    /// <summary>Archive file name of an asset: its SHA-1 plus extension (STUdio convention).</summary>
    public static string AssetName(MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.Hash + asset.Extension;
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static NodePosition? Position(JsonObject o) =>
        o["position"] is JsonObject p && p["x"] is JsonValue x && p["y"] is JsonValue y
            ? new NodePosition((int)x.GetValue<double>(), (int)y.GetValue<double>())
            : null;
}
