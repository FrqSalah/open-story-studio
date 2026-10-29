namespace Studio.Core.Model;

/// <summary>Position of a node in the editor canvas (library only).</summary>
public readonly record struct NodePosition(int X, int Y);

/// <summary>
/// Which controls are active while a stage node is playing.
/// </summary>
/// <param name="Wheel">The wheel cycles through the options of the current action node.</param>
/// <param name="Ok">The OK button follows the OK transition.</param>
/// <param name="Home">The Home button follows the Home transition (or goes back to the pack list).</param>
/// <param name="Pause">The OK button pauses / resumes the audio.</param>
/// <param name="Autoplay">The OK transition is followed automatically when the audio ends.</param>
public readonly record struct ControlSettings(bool Wheel, bool Ok, bool Home, bool Pause, bool Autoplay)
{
    /// <summary>Settings of a menu entry: the user picks it with the wheel and OK.</summary>
    public static ControlSettings Menu => new(Wheel: true, Ok: true, Home: true, Pause: false, Autoplay: false);

    /// <summary>Settings of a story: plays, can be paused, continues automatically.</summary>
    public static ControlSettings Story => new(Wheel: false, Ok: false, Home: true, Pause: true, Autoplay: true);

    /// <summary>Settings of the pack cover (square one).</summary>
    public static ControlSettings Cover => new(Wheel: true, Ok: true, Home: false, Pause: false, Autoplay: false);
}

/// <summary>
/// Link from a stage node to one option of an action node.
/// </summary>
/// <param name="Action">Target action node.</param>
/// <param name="OptionIndex">Option selected when entering the action node.</param>
public sealed record Transition(ActionNode Action, int OptionIndex);

/// <summary>
/// What the device displays and plays at a given step of the story.
/// </summary>
public sealed class StageNode
{
    /// <summary>Creates a stage node.</summary>
    public StageNode(Guid? id = null) => Id = id ?? Guid.NewGuid();

    /// <summary>Node identifier.</summary>
    public Guid Id { get; }

    /// <summary>Editor label.</summary>
    public string Name { get; set; } = "Étape";

    /// <summary>Editor node type label (kept for STUdio compatibility, e.g. "stage", "cover", "story").</summary>
    public string? Type { get; set; }

    /// <summary>Editor group id (kept for STUdio compatibility).</summary>
    public string? GroupId { get; set; }

    /// <summary>Editor canvas position.</summary>
    public NodePosition? Position { get; set; }

    /// <summary>Image shown on the screen (320x240), optional.</summary>
    public MediaAsset? Image { get; set; }

    /// <summary>Audio played, optional (a silent track is generated when writing device formats).</summary>
    public MediaAsset? Audio { get; set; }

    /// <summary>Transition followed with OK (or automatically at the end of the audio).</summary>
    public Transition? OkTransition { get; set; }

    /// <summary>Transition followed with Home.</summary>
    public Transition? HomeTransition { get; set; }

    /// <summary>Active controls.</summary>
    public ControlSettings Controls { get; set; } = ControlSettings.Menu;

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>
/// An ordered list of stage nodes the user can cycle through with the wheel.
/// </summary>
public sealed class ActionNode
{
    /// <summary>Creates an action node.</summary>
    public ActionNode(string? id = null) => Id = id ?? Guid.NewGuid().ToString();

    /// <summary>Node identifier (free-form string in the STUdio format).</summary>
    public string Id { get; }

    /// <summary>Editor label.</summary>
    public string Name { get; set; } = "Choix";

    /// <summary>Editor node type label (kept for STUdio compatibility).</summary>
    public string? Type { get; set; }

    /// <summary>Editor group id (kept for STUdio compatibility).</summary>
    public string? GroupId { get; set; }

    /// <summary>Editor canvas position.</summary>
    public NodePosition? Position { get; set; }

    /// <summary>Options, in wheel order.</summary>
    public List<StageNode> Options { get; } = [];

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Options.Count} options)";
}
