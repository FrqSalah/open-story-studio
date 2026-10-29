using Studio.Core;
using Studio.Core.Model;
using Studio.Core.Validation;

namespace Studio.UI.Services;

/// <summary>
/// The pack being edited, with undo/redo. Snapshots share the (immutable) media assets, so they are cheap.
/// </summary>
public sealed class EditorSession
{
    private const int MaxHistory = 100;
    private readonly LinkedList<StoryPack> _undo = new();
    private readonly Stack<StoryPack> _redo = new();

    /// <summary>Raised after any change.</summary>
    public event EventHandler? Changed;

    /// <summary>Pack being edited.</summary>
    public StoryPack? Pack { get; private set; }

    /// <summary>True when there are unsaved changes.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Selected stage node.</summary>
    public StageNode? SelectedStage { get; private set; }

    /// <summary>Selected action node.</summary>
    public ActionNode? SelectedAction { get; private set; }

    /// <summary>Whether undo is possible.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether redo is possible.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Current validation issues.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; private set; } = [];

    /// <summary>Starts editing a pack.</summary>
    public void Open(StoryPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Pack = pack;
        _undo.Clear();
        _redo.Clear();
        IsDirty = false;
        ClearSelection();
        EnsureLayout(pack);
        Refresh();
    }

    /// <summary>Clears the selection.</summary>
    public void ClearSelection()
    {
        SelectedStage = null;
        SelectedAction = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects a stage node.</summary>
    public void Select(StageNode stage)
    {
        SelectedStage = stage;
        SelectedAction = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects an action node.</summary>
    public void Select(ActionNode action)
    {
        SelectedAction = action;
        SelectedStage = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies an edit, recording an undo step first.</summary>
    public void Edit(Action<StoryPack> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Pack is null) return;
        _undo.AddLast(PackCloner.Clone(Pack));
        if (_undo.Count > MaxHistory) _undo.RemoveFirst();
        _redo.Clear();
        change(Pack);
        IsDirty = true;
        Refresh();
    }

    /// <summary>Records a position change without an undo step per pixel (call once per drag).</summary>
    public void BeginDrag()
    {
        if (Pack is null) return;
        _undo.AddLast(PackCloner.Clone(Pack));
        _redo.Clear();
    }

    /// <summary>Marks a drag as finished.</summary>
    public void EndDrag()
    {
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undoes the last edit.</summary>
    public void Undo()
    {
        if (Pack is null || _undo.Last is not { } last) return;
        _undo.RemoveLast();
        _redo.Push(PackCloner.Clone(Pack));
        Restore(last.Value);
    }

    /// <summary>Redoes the last undone edit.</summary>
    public void Redo()
    {
        if (Pack is null || !_redo.TryPop(out var next)) return;
        _undo.AddLast(PackCloner.Clone(Pack));
        Restore(next);
    }

    /// <summary>Marks the pack as saved.</summary>
    public void MarkSaved()
    {
        IsDirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Restore(StoryPack snapshot)
    {
        var stageId = SelectedStage?.Id;
        var actionId = SelectedAction?.Id;
        Pack = snapshot;
        SelectedStage = stageId is { } s ? snapshot.FindStage(s) : null;
        SelectedAction = actionId is { } a ? snapshot.FindAction(a) : null;
        IsDirty = true;
        Refresh();
    }

    private void Refresh()
    {
        Issues = Pack is null ? [] : PackValidator.Validate(Pack);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Gives a position to nodes that have none (layered layout from square one).</summary>
    public static void EnsureLayout(StoryPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.StageNodes.All(s => s.Position is not null) && pack.ActionNodes.All(a => a.Position is not null)) return;

        var depth = new Dictionary<object, int>();
        var queue = new Queue<(object Node, int Depth)>();
        if (pack.SquareOne is { } first) queue.Enqueue((first, 0));
        while (queue.TryDequeue(out var item))
        {
            if (depth.ContainsKey(item.Node)) continue;
            depth[item.Node] = item.Depth;
            switch (item.Node)
            {
                case StageNode s:
                    foreach (var t in new[] { s.OkTransition, s.HomeTransition }.OfType<Transition>()) queue.Enqueue((t.Action, item.Depth + 1));
                    break;
                case ActionNode a:
                    foreach (var o in a.Options) queue.Enqueue((o, item.Depth + 1));
                    break;
            }
        }

        var max = depth.Count == 0 ? 0 : depth.Values.Max();
        var rows = new Dictionary<int, int>();
        foreach (var s in pack.StageNodes) Place(s, n => s.Position ??= n);
        foreach (var a in pack.ActionNodes) Place(a, n => a.Position ??= n);

        void Place(object node, Action<NodePosition> set)
        {
            var d = depth.TryGetValue(node, out var v) ? v : max + 1;
            var row = rows[d] = rows.GetValueOrDefault(d) + 1;
            set(new NodePosition(d * 240, (row - 1) * 120));
        }
    }
}
