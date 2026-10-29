using Studio.Core;
using Studio.Core.Model;
using Studio.Core.Navigation;
using Studio.Core.Validation;

namespace Studio.Tests.Core;

public class ValidatorTests
{
    [Fact]
    public void Templates_have_no_errors()
    {
        Assert.True(PackValidator.IsValid(PackTemplates.StoryMenu("x", 4)));
        Assert.True(PackValidator.IsValid(PackTemplates.SingleStory("x")));
    }

    [Fact]
    public void Empty_pack_is_an_error() =>
        Assert.Contains(PackValidator.Validate(new StoryPack()), i => i.Code == "empty");

    [Fact]
    public void Detects_unreachable_stages()
    {
        var pack = PackTemplates.SingleStory("x");
        var orphan = new StageNode { Name = "Orpheline" };
        pack.StageNodes.Add(orphan);
        Assert.Contains(PackValidator.Validate(pack), i => i.Code == "unreachable" && i.NodeId == orphan.Id.ToString());
    }

    [Fact]
    public void Detects_links_to_removed_action_nodes()
    {
        var pack = PackTemplates.SingleStory("x");
        pack.ActionNodes.Clear();
        Assert.Contains(PackValidator.Validate(pack), i => i.Code == "broken-link" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Detects_out_of_range_option_index()
    {
        var pack = PackTemplates.SingleStory("x");
        pack.SquareOne!.OkTransition = pack.SquareOne.OkTransition! with { OptionIndex = 5 };
        Assert.Contains(PackValidator.Validate(pack), i => i.Code == "bad-option");
    }

    [Fact]
    public void Detects_stages_with_no_active_control()
    {
        var pack = PackTemplates.SingleStory("x");
        pack.StageNodes[1].Controls = default;
        Assert.Contains(PackValidator.Validate(pack), i => i.Code == "stuck");
    }
}

public class StoryPackTests
{
    [Fact]
    public void Removing_a_stage_removes_it_from_options_and_clamps_transitions()
    {
        var pack = PackTemplates.StoryMenu("x", 3);
        var menu = pack.ActionNodes[0];
        var last = menu.Options[^1];
        var story = pack.StageNodes.Single(s => s.Name == "Histoire 3");
        Assert.Equal(2, story.HomeTransition!.OptionIndex);

        pack.RemoveStage(last);

        Assert.DoesNotContain(last, pack.StageNodes);
        Assert.DoesNotContain(last, menu.Options);
        Assert.Equal(1, story.HomeTransition!.OptionIndex);
    }

    [Fact]
    public void Removing_an_action_clears_transitions()
    {
        var pack = PackTemplates.SingleStory("x");
        pack.RemoveAction(pack.ActionNodes[0]);
        Assert.Null(pack.SquareOne!.OkTransition);
    }

    [Fact]
    public void Square_one_defines_the_pack_id()
    {
        var pack = PackTemplates.StoryMenu("x");
        var story = pack.StageNodes[^1];
        pack.SetSquareOne(story);
        Assert.Equal(story.Id, pack.Id);
    }

    [Fact]
    public void Clone_is_deep_but_shares_assets()
    {
        var pack = TestData.DeviceReadyMenu(2);
        var copy = PackCloner.Clone(pack);
        Assert.Equal(pack.Id, copy.Id);
        Assert.NotSame(pack.StageNodes[0], copy.StageNodes[0]);
        Assert.Same(pack.StageNodes[0].Image, copy.StageNodes[0].Image);
        Assert.Same(copy.ActionNodes[0], copy.SquareOne!.OkTransition!.Action);
        Assert.Contains(copy.ActionNodes[0].Options[0], copy.StageNodes);

        copy.StageNodes[0].Name = "changed";
        Assert.NotEqual("changed", pack.StageNodes[0].Name);
    }

    [Fact]
    public void Media_type_is_detected_from_content()
    {
        Assert.Equal(MediaTypes.Png, new MediaAsset(TestData.Png(8, 8)).MimeType);
        Assert.Equal(MediaTypes.Bmp, new MediaAsset(TestData.DeviceBmp()).MimeType);
        Assert.Equal(MediaTypes.Mp3, new MediaAsset(TestData.DeviceMp3).MimeType);
        Assert.Equal(MediaTypes.Wav, new MediaAsset(TestData.Wave()).MimeType);
        Assert.Equal(MediaKind.Unknown, new MediaAsset([0, 0, 0, 0], fileName: "x.bin").Kind);
    }
}

public class StoryPlayerTests
{
    private static (StoryPack Pack, StoryPlayer Player) Menu()
    {
        var pack = PackTemplates.StoryMenu("x", 3);
        return (pack, new StoryPlayer(pack));
    }

    [Fact]
    public void Starts_on_the_cover() => Assert.Equal("Couverture", Menu().Player.Current.Name);

    [Fact]
    public void Ok_enters_the_menu_and_the_wheel_wraps_around()
    {
        var (_, player) = Menu();
        Assert.True(player.Press(DeviceButton.Ok));
        Assert.Equal("Choix 1", player.Current.Name);
        player.Press(DeviceButton.WheelRight);
        Assert.Equal("Choix 2", player.Current.Name);
        player.Press(DeviceButton.WheelLeft);
        player.Press(DeviceButton.WheelLeft);
        Assert.Equal("Choix 3", player.Current.Name);
    }

    [Fact]
    public void Story_plays_and_returns_to_its_menu_entry()
    {
        var (_, player) = Menu();
        player.Press(DeviceButton.Ok);
        player.Press(DeviceButton.WheelRight);
        player.Press(DeviceButton.Ok);
        Assert.Equal("Histoire 2", player.Current.Name);
        Assert.False(player.Press(DeviceButton.WheelRight), "the wheel is disabled during a story");

        Assert.True(player.AudioEnded());
        Assert.Equal("Choix 2", player.Current.Name);
    }

    [Fact]
    public void Pause_toggles_during_a_story()
    {
        var (_, player) = Menu();
        player.Press(DeviceButton.Ok);
        player.Press(DeviceButton.Ok);
        Assert.True(player.Press(DeviceButton.Ok), "OK pauses when only pause is enabled");
        Assert.True(player.IsPaused);
        player.Press(DeviceButton.Pause);
        Assert.False(player.IsPaused);
    }

    [Fact]
    public void Home_follows_the_home_transition_or_exits()
    {
        var (_, player) = Menu();
        player.Press(DeviceButton.Ok);
        player.Press(DeviceButton.WheelRight);
        player.Press(DeviceButton.WheelRight);
        player.Press(DeviceButton.Ok);
        player.Press(DeviceButton.Home);
        Assert.Equal("Choix 3", player.Current.Name);

        player.Press(DeviceButton.Home); // menu entry without home transition → back to cover
        Assert.Equal("Couverture", player.Current.Name);
        Assert.False(player.Press(DeviceButton.Home), "home is disabled on the cover");
    }
}
