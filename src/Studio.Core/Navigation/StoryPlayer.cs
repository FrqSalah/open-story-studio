using Studio.Core.Model;

namespace Studio.Core.Navigation;

/// <summary>Buttons of the story teller device.</summary>
public enum DeviceButton
{
    /// <summary>Wheel turned left (previous option).</summary>
    WheelLeft,
    /// <summary>Wheel turned right (next option).</summary>
    WheelRight,
    /// <summary>OK button.</summary>
    Ok,
    /// <summary>Home button.</summary>
    Home,
    /// <summary>Pause button (OK when pause is enabled, on the device).</summary>
    Pause,
}

/// <summary>
/// Pure, UI-agnostic implementation of the device navigation rules, used by the simulator.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Entering a transition selects option <c>OptionIndex</c> of the target action node.</item>
/// <item>The wheel cycles through the options of the current action node (wrapping around).</item>
/// <item>OK (or the end of the audio with autoplay) follows the OK transition.</item>
/// <item>Home follows the Home transition, or returns to square one when there is none.</item>
/// </list>
/// </remarks>
public sealed class StoryPlayer
{
    private readonly StoryPack _pack;

    /// <summary>Creates a player positioned on square one.</summary>
    public StoryPlayer(StoryPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        _pack = pack;
        Current = pack.SquareOne ?? throw new ArgumentException("Le pack est vide.", nameof(pack));
    }

    /// <summary>Raised whenever the current stage changes.</summary>
    public event EventHandler? StageChanged;

    /// <summary>Stage currently displayed.</summary>
    public StageNode Current { get; private set; }

    /// <summary>Action node whose options the wheel cycles through, if any.</summary>
    public ActionNode? CurrentAction { get; private set; }

    /// <summary>Index of <see cref="Current"/> in <see cref="CurrentAction"/>.</summary>
    public int CurrentOptionIndex { get; private set; }

    /// <summary>Whether the audio is paused.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>True when playback left the pack (Home with no transition from square one).</summary>
    public bool HasExited { get; private set; }

    /// <summary>Restarts from square one.</summary>
    public void Reset()
    {
        HasExited = false;
        CurrentAction = null;
        CurrentOptionIndex = 0;
        Go(_pack.SquareOne!);
    }

    /// <summary>Applies a button press. Returns false when the button is disabled on the current stage.</summary>
    public bool Press(DeviceButton button)
    {
        var c = Current.Controls;
        switch (button)
        {
            case DeviceButton.WheelLeft or DeviceButton.WheelRight:
                if (!c.Wheel || CurrentAction is not { Options.Count: > 1 } action) return false;
                var delta = button == DeviceButton.WheelRight ? 1 : -1;
                CurrentOptionIndex = (CurrentOptionIndex + delta + action.Options.Count) % action.Options.Count;
                Go(action.Options[CurrentOptionIndex]);
                return true;

            case DeviceButton.Pause:
                if (!c.Pause) return false;
                IsPaused = !IsPaused;
                StageChanged?.Invoke(this, EventArgs.Empty);
                return true;

            case DeviceButton.Ok:
                if (!c.Ok) return c.Pause && Press(DeviceButton.Pause);
                return Follow(Current.OkTransition);

            case DeviceButton.Home:
                if (!c.Home) return false;
                if (Current.HomeTransition is not null) return Follow(Current.HomeTransition);
                if (Current == _pack.SquareOne) HasExited = true;
                Reset();
                return true;

            default:
                return false;
        }
    }

    /// <summary>To be called by the UI when the stage audio finished playing.</summary>
    public bool AudioEnded()
    {
        if (!Current.Controls.Autoplay) return false;
        if (Current.OkTransition is null)
        {
            Reset();
            return true;
        }
        return Follow(Current.OkTransition);
    }

    private bool Follow(Transition? transition)
    {
        if (transition is null || transition.Action.Options.Count == 0) return false;
        CurrentAction = transition.Action;
        CurrentOptionIndex = Math.Clamp(transition.OptionIndex, 0, transition.Action.Options.Count - 1);
        Go(transition.Action.Options[CurrentOptionIndex]);
        return true;
    }

    private void Go(StageNode stage)
    {
        Current = stage;
        IsPaused = false;
        StageChanged?.Invoke(this, EventArgs.Empty);
    }
}
