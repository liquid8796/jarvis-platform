using System.Windows.Input;

namespace JarvisCode.App.Services;

/// <summary>One row of the pane rail: what it is called, what it draws, and its chord.</summary>
/// <param name="Pane">The pane key the toggle opens.</param>
/// <param name="Icon">The glyph resource key.</param>
/// <param name="Label">The tooltip and accessible name while the pane is quiet.</param>
/// <param name="Shortcut">The chord, spelled the way the keycaps render it, or null.</param>
/// <param name="Active">Whether the pane is on screen.</param>
/// <param name="Activity">Whether the pane has something waiting — draws the accent dot.</param>
/// <param name="ActiveLabel">The label the tooltip uses instead while <paramref name="Activity"/> holds.</param>
public sealed record PaneRailSpec(
    string Pane,
    string Icon,
    string Label,
    string? Shortcut,
    bool Active,
    bool Activity = false,
    string? ActiveLabel = null)
{
    /// <summary>
    /// The reference's own rule (its Jb): a pane with something waiting is described by
    /// its activity label, and the same string is both the tooltip and the accessible name.
    /// </summary>
    public string Tooltip => Activity && ActiveLabel is { Length: > 0 } ? ActiveLabel : Label;
}

/// <summary>
/// The side panes, measured off the desktop app: its title switch qH and icon map NP
/// (ion-dist chunk c360a9e1c-DUoNQd2W.js, at the "Background tasks"/"Run in background"
/// literals). The keys are the reference's own tile ids except where this build already
/// used one for something else — see <see cref="RunHistory"/>.
/// </summary>
public static class SidePanes
{
    public const string Preview = "browser";
    public const string Artifact = "artifacts";
    public const string Diff = "changes";
    public const string Terminal = "terminal";
    public const string File = "file";
    public const string FileBrowser = "files";
    public const string Plan = "plan";
    public const string BackgroundTasks = "runs";
    public const string Todos = "tasks";
    public const string Subagent = "subagent";
    public const string Session = "session";
    public const string Transcript = "transcript";
    public const string PullRequest = "pr";
    public const string SideChat = "sidechat";

    /// <summary>
    /// The reference's simulator pane — the live device view its Android Emulator
    /// and iOS Simulator servers attach to. Only the Android half exists here.
    /// </summary>
    public const string Simulator = "simulator";

    /// <summary>
    /// The routine-run history the reference files under "runs". This build's Background
    /// tasks pane already answers to that key, so the run history takes a second name
    /// rather than two panes sharing one tile id in a stored layout.
    /// </summary>
    public const string RunHistory = "runhistory";

    /// <summary>
    /// The reference's qH: a pane's own title, shown in its header. The one adaptation is
    /// the preview pane, which the reference titles "Preview" and labels "Browser" on the
    /// rail wherever an external preview exists — and "Browser" is what this build calls
    /// that surface everywhere else, from the settings rows to the Claude_Browser tools.
    /// </summary>
    public static string Title(string pane) => pane switch
    {
        Preview => "Browser",
        Artifact => "Artifacts",
        Diff => "Changes",
        Terminal => "Terminal",
        File => "File",
        FileBrowser => "Files",
        Plan => "Plan",
        BackgroundTasks => "Background tasks",
        Subagent => "Agent",
        Session => "Session",
        Transcript => "Transcript",
        RunHistory => "Runs",
        PullRequest => "Pull request",
        Todos => "Tasks",
        SideChat => "Side Chat",

        // The reference's $H, which titles this pane by the platform it is
        // showing. This build has only the Android one.
        Simulator => "Android Emulator",
        _ => "",
    };

    /// <summary>The reference's NP: the glyph a pane's rail toggle and menu row draw.</summary>
    public static string? Icon(string pane) => pane switch
    {
        Preview => "GlobeGlyph",
        Artifact => "ArtifactsGlyph",
        Diff => "ChangesGlyph",
        Terminal => "TerminalGlyph",
        FileBrowser => "FolderGlyph",
        Plan => "ChecklistGlyph",
        BackgroundTasks => "AgentsSimpleGlyph",
        RunHistory => "ClockGlyph",
        PullRequest => "GitPullRequestGlyph",
        Simulator => "MobilePhoneGlyph",
        _ => null,
    };

    /// <summary>"Diff (uncommitted changes)" — what the diff toggle reads while the tree is dirty.</summary>
    public const string DiffActivityLabel = "Changes (uncommitted changes)";
}

/// <summary>
/// How a chord from <see cref="PaneKeymap"/> is spelled on a tooltip or a menu row: one
/// cap per key, the way the reference draws every shortcut. The keymap itself is the
/// single table (its LI, ported in <see cref="PaneKeymap"/>); this only formats it, so a
/// rail tooltip and the chord the window answers cannot drift apart.
/// </summary>
public static class PaneShortcuts
{
    /// <summary>
    /// The chord a command's tooltip shows: its first row in the keymap, which is how the
    /// reference's own RI resolves it. togglePreview declares two — the browser chord
    /// first, gated on an external preview being available, then the plain one.
    /// </summary>
    public static string? Display(PaneCommand command, bool externalPreviewAvailable = false)
    {
        foreach (var chord in PaneKeymap.Chords)
        {
            if (chord.Command != command)
            {
                continue;
            }

            if (command == PaneCommand.TogglePreview && chord.Key == Key.B && !externalPreviewAvailable)
            {
                continue;
            }

            return Spell(chord);
        }

        return null;
    }

    /// <summary>"Ctrl ⇧ D" — the modifiers in the reference's order, then the key.</summary>
    private static string Spell(PaneChord chord)
    {
        var parts = new List<string>();
        if (chord.Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (chord.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("⇧");
        }

        if (chord.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        parts.Add(KeyName(chord.Key));
        return string.Join(" ", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        Key.OemTilde => "`",
        Key.Oem5 => "\\",
        Key.OemSemicolon => ";",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Tab => "Tab",
        _ => key.ToString(),
    };
}
