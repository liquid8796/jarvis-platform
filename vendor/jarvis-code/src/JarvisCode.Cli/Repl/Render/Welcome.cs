using JarvisCode.Cli.Repl.Keys;

namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// The session's opening lines: the reference's welcome row (its <c>zB</c>) and
/// the rotating startup tip it prints under it. The reference's ASCII mascot is
/// deliberately not carried — it is Anthropic's own character art — so this
/// build always draws the short form the reference itself uses in a screen
/// reader session or a terminal under 30 rows.
/// </summary>
internal static class Welcome
{
    /// <summary>The reference's <c>W</c>: below this many rows it prints the short form.</summary>
    public const int ShortFormBelowRows = 30;

    public static string Line(string version) => $"{Glyphs.Star} Welcome to Jarvis Code v{version}";

    /// <summary>The cwd and model line this front-end prints under the welcome.</summary>
    public static string Where(string model, string workingDirectory) => $"  model: {model} · cwd: {workingDirectory}";
}

/// <summary>One rotating tip: what it says, how many sessions must pass before it may repeat, and when it applies.</summary>
internal sealed record StartupTip(
    string Id,
    string Text,
    int CooldownSessions,
    int Priority = 0,
    int? MaxLifetimeShows = null,
    Func<TipContext, bool>? IsRelevant = null);

/// <summary>What a tip's relevance predicate is allowed to look at.</summary>
internal sealed record TipContext(
    int StartupCount,
    bool HasCustomSkills,
    bool WorkflowsEnabled,
    bool InGitRepository);

/// <summary>
/// The reference's startup tips (its <c>Hm</c> array), carrying the entries
/// whose subject exists in this build. The ones it gates on a claude.ai
/// account, Claude Desktop, an IDE extension or the Slack app are left out —
/// a tip that advertises an affordance this app does not have would be a
/// falsehood the user cannot act on. The rotation is the reference's own: a
/// tip is offered once its cooldown in sessions has passed, highest priority
/// first, and never more than its lifetime cap.
/// </summary>
internal static class StartupTips
{
    /// <summary>The tips this build carries, with the reference's own cooldowns and priorities.</summary>
    public static IReadOnlyList<StartupTip> All(KeyMap map) =>
    [
        new("plan-mode-for-complex-tasks",
            "Use Plan Mode to prepare for a complex request before making changes. Press " +
            ChordFormat.Format(map.ChordFor("chat:cycleMode", "Chat", KeyBindings.CycleModeChord)) +
            " twice to enable.",
            CooldownSessions: 5, Priority: 2),
        new("default-permission-mode-config",
            "Use /config to change your default permission mode (including Plan Mode)", 10),
        new("git-worktrees", "Use git worktrees to run multiple Jarvis sessions in parallel.", 10),
        new("color-when-multi-clauding",
            "Running multiple Jarvis sessions? Use /color and /rename to tell them apart at a glance.", 10),
        new("memory-command", "Use /memory to view and manage Jarvis memory", 15),
        new("theme-command", "Use /theme to change the color theme", 20),
        new("status-line",
            "Use /statusline to set up a custom status line that will display beneath the input box", 25),
        new("prompt-queue", "Hit Enter to queue up additional messages while Jarvis is working.", 5),
        new("enter-to-steer-in-relatime",
            "Send messages to Jarvis while it works to steer Jarvis in real-time", 20),
        new("todo-list",
            "Ask Jarvis to create a todo list when working on complex tasks to track progress and remain on track", 20),
        new("permissions", "Use /permissions to pre-approve and pre-deny bash, edit, and MCP tools", 10),
        new("double-esc-code-restore",
            "Double-tap esc to rewind the code and/or conversation to a previous point in time", 10),
        new("continue", "Run jarvis --continue or jarvis --resume to resume a conversation", 10),
        new("rename-conversation", "Name your conversations with /rename to find them easily in /resume later", 15),
        new("custom-commands",
            "Create skills by adding .md files to .jarvis/skills/ in your project or to the skills folder in " +
            "your Jarvis Code profile for skills that work in any project", 15),
        new("image-paste",
            "Use " + ChordFormat.Format(map.ChordFor("chat:imagePaste", "Chat", KeyBindings.ImagePasteChord)) +
            " to paste images from your clipboard", 20),
        new("subagent-fanout-nudge",
            "Say \"fan out subagents\" and Jarvis sends a team. Each one digs deep so nothing gets missed.", 3),
        new("dynamic-workflows",
            "Dynamic workflows let Jarvis write a script that orchestrates many agents for you. Mention the " +
            "keyword ultracode or ask Jarvis to use a workflow directly.", 3,
            IsRelevant: context => context.WorkflowsEnabled),
        new("code-review-low-fast",
            "For a fast, cheap code review, try /code-review low. It runs the built-in skill at its lightest " +
            "effort level.", 8),
        new("goal-command-nudge", "Set an objective with /goal — Jarvis keeps working until it's met", 3),
    ];

    /// <summary>
    /// The tip to show, or null. <paramref name="lastShown"/> maps a tip id to
    /// the startup count it was last shown at, which is what the cooldown is
    /// measured in; <paramref name="shownCount"/> is its lifetime tally.
    /// </summary>
    public static StartupTip? Pick(
        IReadOnlyList<StartupTip> tips,
        TipContext context,
        IReadOnlyDictionary<string, int> lastShown,
        IReadOnlyDictionary<string, int> shownCount)
    {
        StartupTip? best = null;
        foreach (var tip in tips)
        {
            if (tip.IsRelevant is { } relevant && !relevant(context))
            {
                continue;
            }

            if (tip.MaxLifetimeShows is { } cap && shownCount.GetValueOrDefault(tip.Id) >= cap)
            {
                continue;
            }

            if (lastShown.TryGetValue(tip.Id, out int at) && context.StartupCount - at < tip.CooldownSessions)
            {
                continue;
            }

            if (best is null || tip.Priority > best.Priority)
            {
                best = tip;
            }
        }

        return best;
    }

    /// <summary>
    /// The line a tip is drawn on. The reference's tip texts are whole
    /// sentences — it prefixes them with its ※ mark and nothing else, which is
    /// why there is no "Tip:" label here.
    /// </summary>
    public static string Render(StartupTip tip) => $"{Glyphs.TipMark} {tip.Text}";
}
