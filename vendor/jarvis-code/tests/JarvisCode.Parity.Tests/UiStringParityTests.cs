using JarvisCode.App.ViewModels;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Every user-visible string this app took from Claude Code Desktop, checked
/// against that app's own en-US catalogue by the message id it carries there.
///
/// Matching by id rather than by "some entry happens to have this text" is what
/// makes the check meaningful: it proves we still say what *that* reference
/// string says, so a reference rewording fails the test instead of passing on a
/// coincidental twin elsewhere in a 23k-entry catalogue.
/// </summary>
public sealed class UiStringParityTests
{
    /// <summary>Reference message id → the string our code produces for it.</summary>
    public static TheoryData<string, string> PortedStrings => new()
    {
        // Turn status line phases.
        { "QhT5HdB9mD", TurnStatusLine.WaitingForJarvis },
        { "/BJXX9tCZt", TurnStatusLine.RunningTools },
        { "NACfNicI6z", TurnStatusLine.StoppingLabel },
        { "FIEdqIul1I", TurnStatusLine.CompactingSession },

        // The elapsed-bucketed thinking ladder.
        { "P1QHV0AhYD", TurnStatusLine.ThinkingLabel(0) },
        { "q8XCKBJ8hC", TurnStatusLine.ThinkingLabel(15) },
        { "u1pmp+DI3F", TurnStatusLine.ThinkingLabel(30) },
        { "TBOtXkHUES", TurnStatusLine.ThinkingLabel(45) },
        { "mOzN+6yGec", TurnStatusLine.ThinkingLabel(60) },

        // Background tasks pane.
        { "tibq2MpiPM", BackgroundTaskPresentation.EmptyStateText },

        // A workflow run's progress groups.
        { "P7djTuHDN0", WorkflowPhaseLabels.Phases },
        { "QGVI63s1dJ", WorkflowPhaseLabels.AgentColumn },
        { "rhSI1/3g21", WorkflowPhaseLabels.ModelColumn },
        { "P6EE/aQ7SS", WorkflowPhaseLabels.TokensColumn },
        { "ug01MkZy2v", WorkflowPhaseLabels.TimeColumn },
        { "eKEL/gUqiZ", WorkflowPhaseLabels.Status(WorkflowPhaseStatus.Pending) },
        { "nDyaq/M7Oj", WorkflowPhaseLabels.Status(WorkflowPhaseStatus.Running) },
        { "JXdbo8Vnlw", WorkflowPhaseLabels.Status(WorkflowPhaseStatus.Done) },
        { "KN7zKn8z4F", WorkflowPhaseLabels.Status(WorkflowPhaseStatus.Error) },
        { "byLbiDmAsH", WorkflowPhaseLabels.NoAgents(WorkflowRunStatus.Running) },
        { "jwf/WURmBJ", WorkflowPhaseLabels.NoAgents(WorkflowRunStatus.Completed) },
        { "Sgd7x9pszL", WorkflowPhaseLabels.NoAgents(WorkflowRunStatus.Stopped) },
        { "D8loZOMbyP", WorkflowPhaseLabels.NoAgents(WorkflowRunStatus.Failed) },

        // The chat transcript's thinking cell.
        { "aWpBzjCXKS", ThinkingCell.ShowMore },
        { "qyJtWyZ0yt", ThinkingCell.ShowLess },
        { "zl6fNbo7RW", ThinkingLabels.NoDuration },
        { "GWEGVdQi8W", CopyActionName(copied: false) },
        { "p556q3uvbn", CopyActionName(copied: true) },
    };

    private static string CopyActionName(bool copied) =>
        new ThinkingItem { Text = "thought", IsCopied = copied }.CopyActionName;

    [ReferenceAppTheory]
    [MemberData(nameof(PortedStrings))]
    public void Ported_string_matches_the_reference_catalogue_entry(string messageId, string ours)
    {
        var catalogue = ReferenceInstall.Catalogue!;
        Assert.True(catalogue.TryGetValue(messageId, out var reference),
            $"the reference catalogue has no entry '{messageId}' any more — the string was renamed or " +
            "removed upstream; re-find it by its wording and update the id here.");
        Assert.True(UiBrand.Matches(reference!, ours),
            $"'{messageId}' reads \"{reference}\" in the reference desktop app, and this app renders " +
            $"\"{ours}\" — which is neither that text nor its rebranding \"{UiBrand.Apply(reference!)}\".");
    }

    /// <summary>
    /// The phase header's tally and the phase button's accessible name come from
    /// ICU templates, so they are checked against the template rather than
    /// against a rendered string — including the reference's own plural rule.
    /// </summary>
    [ReferenceAppFact]
    public void Phase_header_renders_the_reference_templates()
    {
        var catalogue = ReferenceInstall.Catalogue!;
        Assert.Equal("{done}/{total}", catalogue["ihIcdpwqY4"]);
        Assert.Equal(
            "1/3",
            WorkflowPhaseLabels.Tally(new WorkflowPhaseCounts(Done: 1, Pending: 2, Total: 3)));

        Assert.Equal(
            "Phase: {title}, {status}{total, plural, =0 {} one {, {done} of {total} agent done} " +
            "other {, {done} of {total} agents done}}",
            catalogue["fDzdmwIyjw"]);
        Assert.Equal(
            "Phase: Scan, Running, 1 of 3 agents done",
            WorkflowPhaseLabels.AccessibleName(Phase(done: 1, total: 3)));
        Assert.Equal(
            "Phase: Scan, Done, 1 of 1 agent done",
            WorkflowPhaseLabels.AccessibleName(Phase(done: 1, total: 1)));
        Assert.Equal("Phase: Scan, Pending", WorkflowPhaseLabels.AccessibleName(Phase(done: 0, total: 0)));
    }

    private static WorkflowPhaseGroup Phase(int done, int total)
    {
        var counts = new WorkflowPhaseCounts(Done: done, Pending: total - done, Total: total);
        return new WorkflowPhaseGroup(
            1, "Scan", null, [], counts,
            WorkflowProgressGrouping.StatusOf(counts, settled: false), 0, null);
    }

    /// <summary>
    /// The status line's "Thought for" label. The reference status line uses the
    /// seconds form only (its transcript thinking header has a longer ladder,
    /// which is a different component); this pins the template we render.
    /// </summary>
    [ReferenceAppFact]
    public void Thought_for_label_uses_the_reference_template()
    {
        var template = ReferenceInstall.Catalogue!["Jj0DasG7Mx"];
        Assert.Equal("Thought for {seconds}s", template);
        Assert.Equal(template.Replace("{seconds}", "42"), TurnStatusLine.ThoughtForLabel(42));
    }

    [ReferenceAppFact]
    public void Thinking_ladder_switches_at_the_reference_thresholds()
    {
        // The bucket boundaries themselves, not just the wording: one second
        // below each threshold must still read as the previous rung.
        var catalogue = ReferenceInstall.Catalogue!;
        Assert.Equal(catalogue["P1QHV0AhYD"], TurnStatusLine.ThinkingLabel(14));
        Assert.Equal(catalogue["q8XCKBJ8hC"], TurnStatusLine.ThinkingLabel(29));
        Assert.Equal(catalogue["u1pmp+DI3F"], TurnStatusLine.ThinkingLabel(44));
        Assert.Equal(catalogue["TBOtXkHUES"], TurnStatusLine.ThinkingLabel(59));
        Assert.Equal(catalogue["mOzN+6yGec"], TurnStatusLine.ThinkingLabel(600));
    }

    /// <summary>
    /// The chat transcript's thinking header renders every rung of the reference's
    /// ladder from that rung's own catalogue template, so a reworded reference (or
    /// a rung rendered from the wrong template) fails here rather than shipping.
    /// </summary>
    [ReferenceAppFact]
    public void Thinking_header_renders_every_reference_rung()
    {
        var catalogue = ReferenceInstall.Catalogue!;

        Assert.Equal(
            catalogue["Jj0DasG7Mx"].Replace("{seconds}", "42"),
            ThinkingLabels.ForTotal(TimeSpan.FromSeconds(42)));
        Assert.Equal(
            catalogue["vFyvc213kN"].Replace("{minutes}", "3"),
            ThinkingLabels.ForTotal(TimeSpan.FromMinutes(3)));
        Assert.Equal(
            catalogue["4LM6AdGWNg"].Replace("{minutes}", "3").Replace("{seconds}", "8"),
            ThinkingLabels.ForTotal(TimeSpan.FromSeconds((3 * 60) + 8)));
        Assert.Equal(
            catalogue["MFx4UWu2HS"].Replace("{hours}", "2"),
            ThinkingLabels.ForTotal(TimeSpan.FromHours(2)));
        Assert.Equal(
            catalogue["voTuOb/ijW"].Replace("{hours}", "2").Replace("{minutes}", "5"),
            ThinkingLabels.ForTotal(TimeSpan.FromMinutes((2 * 60) + 5)));
    }
}
