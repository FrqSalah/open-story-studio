using Studio.Core.Model;

namespace Studio.Core.Validation;

/// <summary>Severity of a validation issue.</summary>
public enum IssueSeverity
{
    /// <summary>The pack works but something looks unintended.</summary>
    Warning,
    /// <summary>The pack cannot be written to a device as is.</summary>
    Error,
}

/// <summary>A problem found in a pack.</summary>
/// <param name="Severity">Severity.</param>
/// <param name="Code">Stable machine readable code.</param>
/// <param name="Message">Human readable message (French, the UI language).</param>
/// <param name="NodeId">Related node id, if any (stage Guid or action string id).</param>
public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Message, string? NodeId = null);

/// <summary>
/// Structural checks run before any transfer: broken links, unreachable nodes, dead ends, missing media.
/// </summary>
public static class PackValidator
{
    /// <summary>Validates a pack and returns every issue found.</summary>
    public static IReadOnlyList<ValidationIssue> Validate(StoryPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var issues = new List<ValidationIssue>();

        if (pack.StageNodes.Count == 0)
        {
            issues.Add(new(IssueSeverity.Error, "empty", "Le pack ne contient aucune étape."));
            return issues;
        }

        var stages = pack.StageNodes.ToHashSet();
        var actions = pack.ActionNodes.ToHashSet();

        if (pack.StageNodes.Select(s => s.Id).Distinct().Count() != pack.StageNodes.Count)
        {
            issues.Add(new(IssueSeverity.Error, "duplicate-id", "Plusieurs étapes ont le même identifiant."));
        }

        foreach (var stage in pack.StageNodes)
        {
            var id = stage.Id.ToString();
            CheckTransition(stage.OkTransition, "OK");
            CheckTransition(stage.HomeTransition, "Accueil");

            if (stage.Image is null && stage.Audio is null)
            {
                issues.Add(new(IssueSeverity.Warning, "no-media", $"« {stage.Name} » n'a ni image ni son.", id));
            }
            if (stage.Image is { Kind: not MediaKind.Image })
            {
                issues.Add(new(IssueSeverity.Error, "bad-image", $"« {stage.Name} » : l'image n'est pas un format reconnu.", id));
            }
            if (stage.Audio is { Kind: not MediaKind.Audio })
            {
                issues.Add(new(IssueSeverity.Error, "bad-audio", $"« {stage.Name} » : le son n'est pas un format reconnu.", id));
            }
            if (stage.OkTransition is null && stage.Controls.Autoplay)
            {
                issues.Add(new(IssueSeverity.Warning, "dead-end",
                    $"« {stage.Name} » enchaîne automatiquement mais n'a pas de suite : l'appareil reviendra au menu.", id));
            }
            if (stage.Controls is { Wheel: false, Ok: false, Home: false, Autoplay: false })
            {
                issues.Add(new(IssueSeverity.Error, "stuck", $"« {stage.Name} » n'a aucune commande active : l'utilisateur serait bloqué.", id));
            }

            void CheckTransition(Transition? t, string label)
            {
                if (t is null) return;
                if (!actions.Contains(t.Action))
                {
                    issues.Add(new(IssueSeverity.Error, "broken-link", $"« {stage.Name} » : la transition {label} pointe vers un choix inexistant.", id));
                }
                else if (t.Action.Options.Count == 0)
                {
                    issues.Add(new(IssueSeverity.Error, "empty-action", $"« {stage.Name} » : la transition {label} pointe vers un choix vide.", id));
                }
                else if (t.OptionIndex < 0 || t.OptionIndex >= t.Action.Options.Count)
                {
                    issues.Add(new(IssueSeverity.Error, "bad-option", $"« {stage.Name} » : l'option {t.OptionIndex} de la transition {label} n'existe pas.", id));
                }
            }
        }

        foreach (var action in pack.ActionNodes)
        {
            if (action.Options.Count == 0)
            {
                issues.Add(new(IssueSeverity.Warning, "empty-action", $"Le choix « {action.Name} » n'a aucune option.", action.Id));
            }
            foreach (var option in action.Options.Where(o => !stages.Contains(o)))
            {
                issues.Add(new(IssueSeverity.Error, "broken-option", $"Le choix « {action.Name} » contient une étape supprimée ({option.Name}).", action.Id));
            }
        }

        foreach (var orphan in FindUnreachable(pack))
        {
            issues.Add(new(IssueSeverity.Warning, "unreachable", $"« {orphan.Name} » n'est accessible depuis aucune autre étape.", orphan.Id.ToString()));
        }

        return issues;
    }

    /// <summary>True when the pack has no error (warnings allowed).</summary>
    public static bool IsValid(StoryPack pack) => Validate(pack).All(i => i.Severity != IssueSeverity.Error);

    /// <summary>Stage nodes that cannot be reached from square one.</summary>
    public static IReadOnlyList<StageNode> FindUnreachable(StoryPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.SquareOne is null) return [];

        var seen = new HashSet<StageNode>();
        var queue = new Queue<StageNode>();
        queue.Enqueue(pack.SquareOne);
        while (queue.TryDequeue(out var stage))
        {
            if (!seen.Add(stage)) continue;
            foreach (var t in new[] { stage.OkTransition, stage.HomeTransition })
            {
                if (t is null) continue;
                foreach (var option in t.Action.Options) queue.Enqueue(option);
            }
        }
        return pack.StageNodes.Where(s => !seen.Contains(s)).ToList();
    }
}
