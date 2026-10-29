using Studio.Core.Model;

namespace Studio.Core;

/// <summary>Ready-made pack structures for the editor.</summary>
public static class PackTemplates
{
    /// <summary>
    /// Cover → menu of <paramref name="storyCount"/> stories → each story plays and returns to the menu.
    /// </summary>
    public static StoryPack StoryMenu(string title, int storyCount = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(storyCount, 1);

        var pack = new StoryPack { Title = title };
        var cover = new StageNode { Name = "Couverture", Type = "cover", Controls = ControlSettings.Cover, Position = new(0, 0) };
        pack.StageNodes.Add(cover);

        var menu = new ActionNode { Name = "Menu des histoires", Position = new(260, 0) };
        var stories = new ActionNode[storyCount];
        pack.ActionNodes.Add(menu);

        for (var i = 0; i < storyCount; i++)
        {
            var entry = new StageNode { Name = $"Choix {i + 1}", Type = "menu", Controls = ControlSettings.Menu, Position = new(520, i * 140) };
            var story = new StageNode { Name = $"Histoire {i + 1}", Type = "story", Controls = ControlSettings.Story, Position = new(1040, i * 140) };
            stories[i] = new ActionNode { Name = $"Lecture {i + 1}", Position = new(780, i * 140) };
            stories[i].Options.Add(story);

            entry.OkTransition = new Transition(stories[i], 0);
            entry.HomeTransition = null;
            story.HomeTransition = new Transition(menu, i);
            story.OkTransition = new Transition(menu, i);

            menu.Options.Add(entry);
            pack.StageNodes.Add(entry);
            pack.StageNodes.Add(story);
            pack.ActionNodes.Add(stories[i]);
        }

        cover.OkTransition = new Transition(menu, 0);
        return pack;
    }

    /// <summary>A single stage pack: the cover plays one story.</summary>
    public static StoryPack SingleStory(string title)
    {
        var pack = new StoryPack { Title = title };
        var cover = new StageNode { Name = "Couverture", Type = "cover", Controls = ControlSettings.Cover, Position = new(0, 0) };
        var story = new StageNode { Name = "Histoire", Type = "story", Controls = ControlSettings.Story, Position = new(520, 0) };
        var play = new ActionNode { Name = "Lecture", Position = new(260, 0) };
        play.Options.Add(story);
        cover.OkTransition = new Transition(play, 0);
        pack.StageNodes.Add(cover);
        pack.StageNodes.Add(story);
        pack.ActionNodes.Add(play);
        return pack;
    }
}
