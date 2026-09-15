using JarvisCode.Cli.Repl.Keys;
using JarvisCode.Core.Permissions;

namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// The line under the composer (CLI 2.1.257, its <c>qnt</c>): the permission
/// mode's own wording when the session is not in the default mode, then
/// whatever hint the state calls for, joined by the reference's <c>" · "</c>.
/// In shell mode the whole footer is replaced by one coloured line.
/// </summary>
internal static class Footer
{
    public const string Separator = " · ";

    /// <summary>The reference's shell-mode footer, drawn in its <c>bashBorder</c> colour.</summary>
    public const string ShellMode = "! for shell mode";

    /// <summary>The reference's resting hint.</summary>
    public const string Shortcuts = "? for shortcuts";

    /// <summary>The hints the reference offers beside the mode.</summary>
    public const string TasksHint = "/tasks to see subagents";
    public const string HideDiffHint = "/diff to hide diff";
    public const string SideQuestionHint = "/btw for side question";

    /// <summary>What the footer is currently saying, in the reference's own order of preference.</summary>
    internal enum Hint
    {
        None,
        Interrupt,
        Cycle,
        Shortcuts,
        ToggleTasks,
    }

    internal sealed record State(
        PermissionMode Mode,
        bool DontAsk = false,
        bool IsRunning = false,
        bool ShowHint = true,
        int RunningTasks = 0,
        string? ContextIndicator = null,
        string? Statusline = null);

    /// <summary>
    /// The footer's text. The mode is shown only while it is something other
    /// than the reference's default, which is what keeps a plain session's
    /// footer to one hint.
    /// </summary>
    public static string Render(State state, KeyMap map)
    {
        var parts = new List<string>(4);
        var descriptor = ModeDescriptors.For(state.Mode, state.DontAsk);
        if (state.Mode != PermissionMode.Manual || state.DontAsk)
        {
            parts.Add(ModeDescriptors.FooterText(descriptor));
        }

        if (state.ContextIndicator is { Length: > 0 } context)
        {
            parts.Add(context);
        }

        if (state.RunningTasks > 0)
        {
            parts.Add(TasksHint);
        }

        if (HintText(state, map) is { Length: > 0 } hint)
        {
            parts.Add(hint);
        }

        if (state.Statusline is { Length: > 0 } statusline)
        {
            parts.Add(statusline);
        }

        return string.Join(Separator, parts);
    }

    /// <summary>The reference's hint ladder: interrupting first, then cycling, then the shortcuts offer.</summary>
    internal static string HintText(State state, KeyMap map)
    {
        if (state.IsRunning)
        {
            var esc = map.ChordFor("chat:cancel", "Chat", "escape");
            return ChordFormat.Hint(esc, "interrupt", ChordStyle.LowerKeys);
        }

        if (!state.ShowHint)
        {
            return "";
        }

        if (state.Mode != PermissionMode.Manual || state.DontAsk)
        {
            var cycle = map.ChordFor("chat:cycleMode", "Chat", KeyBindings.CycleModeChord);
            return ChordFormat.Hint(cycle, "cycle", ChordStyle.LowerKeys, parens: true);
        }

        return Shortcuts;
    }
}

/// <summary>
/// The reference's context indicator (its <c>W9</c>): nothing while the context
/// is comfortable, then the countdown to the compaction the session will
/// actually make, then the red low-context line. Which sentence appears turns
/// on whether auto-compaction is on and whether the model's own window is the
/// one being enforced.
/// </summary>
internal static class ContextIndicator
{
    /// <summary>
    /// Builds the indicator, or null when the level is comfortable.
    /// <paramref name="percentLeft"/> is the room left before the compaction
    /// threshold; <paramref name="enforced"/> is the reference's question of
    /// whether the model's own window decides the threshold.
    /// </summary>
    public static string? Render(
        string level,
        int percentLeft,
        bool autoCompactEnabled,
        bool enforced,
        long effectiveWindow = 0,
        long usedTokens = 0,
        bool compactDisabled = false,
        string? warning = null)
    {
        if (level == "ok")
        {
            return null;
        }

        int left = percentLeft;
        if (!enforced && effectiveWindow > 0)
        {
            // With no enforced window the reference re-derives the figure from
            // the raw usage and reports it as context used instead.
            left = Math.Max(0, (int)Math.Round((effectiveWindow - usedTokens) / (double)effectiveWindow * 100));
        }

        var headline = enforced ? $"{left}% until auto-compact" : $"{100 - left}% context used";
        if (autoCompactEnabled)
        {
            return warning is { Length: > 0 } ? $"{headline}{Footer.Separator}{warning}" : headline;
        }

        var low = $"Context low ({percentLeft}% remaining)";
        if (warning is { Length: > 0 })
        {
            return $"{low}{Footer.Separator}{warning}";
        }

        return compactDisabled ? low : $"{low}{Footer.Separator}Run /compact to compact & continue";
    }
}
