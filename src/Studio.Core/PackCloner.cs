using Studio.Core.Model;

namespace Studio.Core;

/// <summary>Deep copies of story packs.</summary>
public static class PackCloner
{
    /// <summary>
    /// Deep-copies a pack, keeping node ids, and optionally transforming every asset
    /// (each distinct asset is transformed once).
    /// </summary>
    public static StoryPack Clone(StoryPack pack, Func<MediaAsset, MediaAsset>? mapAsset = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var assets = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);
        var copy = new StoryPack
        {
            Title = pack.Title,
            Description = pack.Description,
            Version = pack.Version,
            NightModeAvailable = pack.NightModeAvailable,
            FactoryDisabled = pack.FactoryDisabled,
            Thumbnail = pack.Thumbnail,
        };

        var stages = new Dictionary<StageNode, StageNode>();
        foreach (var s in pack.StageNodes)
        {
            var c = new StageNode(s.Id)
            {
                Name = s.Name,
                Type = s.Type,
                GroupId = s.GroupId,
                Position = s.Position,
                Image = Map(s.Image),
                Audio = Map(s.Audio),
                Controls = s.Controls,
            };
            stages[s] = c;
            copy.StageNodes.Add(c);
        }

        var actions = new Dictionary<ActionNode, ActionNode>();
        foreach (var a in pack.ActionNodes)
        {
            var c = new ActionNode(a.Id) { Name = a.Name, Type = a.Type, GroupId = a.GroupId, Position = a.Position };
            c.Options.AddRange(a.Options.Where(stages.ContainsKey).Select(o => stages[o]));
            actions[a] = c;
            copy.ActionNodes.Add(c);
        }

        foreach (var s in pack.StageNodes)
        {
            stages[s].OkTransition = MapTransition(s.OkTransition);
            stages[s].HomeTransition = MapTransition(s.HomeTransition);
        }
        return copy;

        MediaAsset? Map(MediaAsset? asset)
        {
            if (asset is null) return null;
            if (mapAsset is null) return asset;
            if (!assets.TryGetValue(asset.Hash, out var mapped))
            {
                mapped = mapAsset(asset);
                assets[asset.Hash] = mapped;
            }
            return mapped;
        }

        Transition? MapTransition(Transition? t) =>
            t is not null && actions.TryGetValue(t.Action, out var a) ? new Transition(a, t.OptionIndex) : null;
    }
}
