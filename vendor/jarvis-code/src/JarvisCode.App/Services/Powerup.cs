namespace JarvisCode.App.Services;

/// <summary>
/// /powerup — the reference's "discover features through quick interactive
/// lessons", as a rotation of short lessons about this app's own features.
/// Each invocation shows the next one; the cursor persists in ui-settings.
/// </summary>
public static class Powerup
{
    public static readonly IReadOnlyList<(string Title, string Body)> Lessons =
    [
        ("Plan mode",
         "Press Ctrl+Shift+M (or type /plan) to enter plan mode: Jarvis researches with read-only tools, writes a " +
         "plan file, and presents it for approval before touching anything. Approving swaps the full tool set back " +
         "mid-turn. Try it on your next non-trivial change."),
        ("Effort levels",
         "The effort chip (Ctrl+Shift+E, or /effort) trades speed against depth of thinking. Digits 1–3 pick a level " +
         "while the popover is open — and typing \"ultrathink\" anywhere in a prompt runs that turn at High."),
        ("Fork and branch",
         "Ctrl+Alt+Enter sends the typed prompt in a fork of the session, leaving the original untouched. Hovering a " +
         "past prompt offers rewind (restore the files that turn changed) and branch (copy history up to that point " +
         "into a new session). /fork and /branch do the same from the composer."),
        ("Background agents",
         "Ask Jarvis to run work \"in the background\" and it launches a worker agent whose report arrives as a task " +
         "notification — the {n} running tasks chip opens the runs panel. /subtask <prompt> spawns one that inherits " +
         "this conversation's full context."),
        ("Worktrees",
         "Ask Jarvis to \"work in a worktree\" and it creates an isolated git worktree, repoints the session there " +
         "live, and can exit (keeping or discarding the branch) when done — parallel experiments without touching " +
         "your checkout. The context bar's worktree toggle does the same by hand."),
        ("Skills and custom commands",
         "Markdown files become slash commands: skills in Customize › Skills (or ~/.claude/skills via Import), " +
         "custom commands per project. The composer's \"/\" popup lists everything; /skills manages them."),
        ("The terminal and browser panels",
         "The panel rail (right edge) holds a real ConPTY terminal, the git changes view, and the Jarvis Browser " +
         "pane Jarvis can drive. Ctrl+Shift+L attaches the terminal's selected output to your next message as context."),
        ("Split view and Side Chat",
         "Alt+click a sidebar session (or /tasks › split view) tiles up to four sessions in one window. Side Chat " +
         "(right-click a response › Send to side chat, or /btw <question>) answers side questions without adding to " +
         "the main conversation."),
        ("Queued messages",
         "Type while a turn runs and the prompt queues above the composer — several stack, drag to reorder — " +
         "draining one per turn. No need to wait for Jarvis to finish before saying the next thing."),
        ("Hooks and the statusline",
         "Shell hooks fire on session, prompt, tool, and permission events (/hooks lists what is loaded), and " +
         "/statusline renders your own script's output under the composer. Both make the harness yours."),
    ];

    /// <summary>The lesson at index (wrapped), plus the footer line.</summary>
    public static string Render(int index)
    {
        int wrapped = ((index % Lessons.Count) + Lessons.Count) % Lessons.Count;
        var (title, body) = Lessons[wrapped];
        return $"Powerup {wrapped + 1}/{Lessons.Count} — {title}\n\n{body}\n\n/powerup again shows the next lesson.";
    }
}
