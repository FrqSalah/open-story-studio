namespace Studio.Core.Model;

/// <summary>
/// A story pack: a graph of <see cref="StageNode"/> (what the device shows and plays)
/// linked through <see cref="ActionNode"/> (lists of choices the wheel cycles through).
/// </summary>
/// <remarks>
/// The first stage node is the pack entry point ("square one"); by convention its id is the pack id.
/// </remarks>
public sealed class StoryPack
{
    /// <summary>Pack identifier. Equals the id of the first stage node.</summary>
    public Guid Id => StageNodes.Count > 0 ? StageNodes[0].Id : Guid.Empty;

    /// <summary>Human readable title (library only, not stored on the device).</summary>
    public string Title { get; set; } = "Nouvelle histoire";

    /// <summary>Optional description (library only).</summary>
    public string? Description { get; set; }

    /// <summary>Pack version, stored on the device.</summary>
    public short Version { get; set; } = 1;

    /// <summary>Whether the pack supports the device night mode.</summary>
    public bool NightModeAvailable { get; set; }

    /// <summary>
    /// Marks the pack as a "factory" pack so the official software does not try to inspect it.
    /// </summary>
    public bool FactoryDisabled { get; set; } = true;

    /// <summary>Optional PNG thumbnail (library only).</summary>
    public byte[]? Thumbnail { get; set; }

    /// <summary>Stage nodes. The first one is the entry point.</summary>
    public List<StageNode> StageNodes { get; } = [];

    /// <summary>Action nodes (choice lists).</summary>
    public List<ActionNode> ActionNodes { get; } = [];

    /// <summary>Entry point of the pack, or null for an empty pack.</summary>
    public StageNode? SquareOne => StageNodes.Count > 0 ? StageNodes[0] : null;

    /// <summary>Finds a stage node by id.</summary>
    public StageNode? FindStage(Guid id) => StageNodes.Find(s => s.Id == id);

    /// <summary>Finds an action node by id.</summary>
    public ActionNode? FindAction(string id) => ActionNodes.Find(a => a.Id == id);

    /// <summary>All distinct assets referenced by the pack.</summary>
    public IEnumerable<MediaAsset> Assets =>
        StageNodes.SelectMany(s => new[] { s.Image, s.Audio })
            .OfType<MediaAsset>()
            .DistinctBy(a => a.Hash);

    /// <summary>
    /// Removes a stage node and every reference to it (action options, square one).
    /// </summary>
    public void RemoveStage(StageNode stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        StageNodes.Remove(stage);
        foreach (var action in ActionNodes)
        {
            action.Options.RemoveAll(o => o == stage);
        }
        FixDanglingTransitions();
    }

    /// <summary>Removes an action node and clears every transition pointing to it.</summary>
    public void RemoveAction(ActionNode action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ActionNodes.Remove(action);
        foreach (var stage in StageNodes)
        {
            if (stage.OkTransition?.Action == action) stage.OkTransition = null;
            if (stage.HomeTransition?.Action == action) stage.HomeTransition = null;
        }
    }

    /// <summary>Moves a stage node to the first position, making it the entry point.</summary>
    public void SetSquareOne(StageNode stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (!StageNodes.Remove(stage))
        {
            throw new ArgumentException("Stage node does not belong to this pack.", nameof(stage));
        }
        StageNodes.Insert(0, stage);
    }

    private void FixDanglingTransitions()
    {
        foreach (var stage in StageNodes)
        {
            stage.OkTransition = Clamp(stage.OkTransition);
            stage.HomeTransition = Clamp(stage.HomeTransition);
        }

        static Transition? Clamp(Transition? t)
        {
            if (t is null || t.Action.Options.Count == 0) return t is null ? null : t with { OptionIndex = 0 };
            return t.OptionIndex >= t.Action.Options.Count ? t with { OptionIndex = t.Action.Options.Count - 1 } : t;
        }
    }
}
