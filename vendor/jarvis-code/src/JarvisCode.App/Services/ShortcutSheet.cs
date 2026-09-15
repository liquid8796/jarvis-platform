namespace JarvisCode.App.Services;

/// <summary>One row of the keyboard-shortcuts sheet: what it does, and the chords that do it.</summary>
public sealed record ShortcutRow(string Description, IReadOnlyList<string> Chords);

/// <summary>A titled block of rows.</summary>
public sealed record ShortcutSection(string Title, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// The keyboard-shortcuts sheet, ported from the reference desktop's Code-surface
/// sheet (<c>qI</c> in <c>shared-16-DFDNRrwQ.js</c>) and the pane keymap it reads
/// its chords from (<c>LI</c> in the same chunk).
///
/// Chords are stored in the reference's own spelling — <c>cmd+shift+m</c> — and
/// rendered through <see cref="Keys"/>, which is a port of its keycap splitter
/// and maps <c>cmd</c> to Ctrl on this platform exactly as the reference does.
/// </summary>
public static class ShortcutSheet
{
    public const string Title = "Keyboard shortcuts";

    /// <summary>The word between two alternative chords on one row.</summary>
    public const string ChordSeparator = "or";

    /// <summary>
    /// The reference's three sections in its order, with the rows it shows on a
    /// desktop build whose feature gates are on. Its own sheet grew from 36 rows
    /// to 41 in 1.44121.2.0 — it split the sidebar-tab row in two and added
    /// "Search or start a session", "Toggle sidebar", "Settings" and "Toggle
    /// changed files in diff" — and this build carries 39 of them. The two it
    /// does not are declared in the parity suite's surface manifest.
    /// </summary>
    public static IReadOnlyList<ShortcutSection> Sections { get; } =
    [
        new("General",
        [
            new("Search or start a session", ["ctrl+k"]),
            new("Toggle sidebar", ["ctrl+."]),
            new("Keyboard shortcuts", ["ctrl+/"]),
            new("Settings", ["ctrl+,"]),
            new("New session", ["cmd+n"]),
            new("Close session", ["cmd+w"]),
            new("Next session", ["cmd+shift+]", "ctrl+tab"]),
            new("Previous session", ["cmd+shift+[", "ctrl+shift+tab"]),
            new("Jump to session", ["cmd+1…9"]),
            new("Next sidebar tab", ["cmd+alt+right"]),
            new("Previous sidebar tab", ["cmd+alt+left"]),
            new("Archive session", ["cmd+alt+a", "cmd+shift+backspace"]),
            new("Open session in split view", ["alt+click"]),
            new("Focus next split view", ["ctrl+]"]),
            new("Focus previous split view", ["ctrl+["]),
            new("Pin / unpin session", ["cmd+alt+p"]),
            new("Rename session", ["cmd+alt+r"]),
            new("Mark session as read/unread", ["cmd+alt+u"]),
            new("Copy session link", ["cmd+alt+l"]),
            new("Open session PR", ["cmd+alt+g"]),
            new("Fork session (local sessions)", ["cmd+alt+o"]),
            new("Jump to previous prompt", ["alt+up"]),
            new("Jump to next prompt", ["alt+down"]),
            new("Stop Jarvis’s response", ["esc"]),
        ]),
        new("Panes",
        [
            new("Toggle changes", ["cmd+shift+d"]),
            new("Toggle file list in changes", ["cmd+shift+y"]),
            new("Toggle Browser", ["cmd+shift+b"]),
            new("Select element in Browser", ["cmd+shift+s"]),
            new("New Browser tab", ["cmd+t"]),
            new("Toggle Files", ["cmd+shift+f"]),
            new("Attach file or terminal selection as context", ["cmd+shift+l"]),
            new("Toggle terminal", ["ctrl+`"]),
            new("Close pane", ["cmd+\\"]),
            new("Toggle side chat", ["cmd+;"]),
        ]),
        new("Composer",
        [
            new("Open mode menu", ["cmd+shift+m"]),
            new("Open model menu", ["cmd+shift+i"]),
            new("Open effort selector", ["cmd+shift+e"]),
            new("Select menu item", ["1…9"]),
            new("Upload file", ["ctrl+u"]),
            new("Send in a forked session (local sessions)", ["cmd+alt+enter"]),
        ]),
    ];

    /// <summary>Every row across the sections, for the tests that count them.</summary>
    public static IReadOnlyList<ShortcutRow> AllRows { get; } = [.. Sections.SelectMany(static s => s.Rows)];

    private static readonly string[] ModifierOrder =
        ["ctrl", "control", "alt", "option", "shift", "cmd", "command", "meta"];

    private static readonly HashSet<string> Modifiers =
        new(ModifierOrder, StringComparer.Ordinal);

    /// <summary>
    /// One keycap per key, in the reference's order and spelling: modifiers
    /// first — sorted with cmd folded onto ctrl, as it is on a non-Mac — then
    /// the key itself.
    /// </summary>
    public static IReadOnlyList<string> Keys(string chord)
    {
        var parts = (chord ?? "").ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries);
        var modifiers = new List<string>();
        var keys = new List<string>();
        foreach (var part in parts)
        {
            (Modifiers.Contains(part) ? modifiers : keys).Add(part);
        }

        modifiers.Sort(static (a, b) => Rank(a).CompareTo(Rank(b)));
        return [.. modifiers.Select(ModifierCap), .. keys.Select(KeyCap)];

        // On this platform every command-key spelling sorts where ctrl does.
        static int Rank(string modifier) => Array.IndexOf(
            ModifierOrder, modifier is "cmd" or "command" or "meta" ? "ctrl" : modifier);
    }

    private static string ModifierCap(string modifier) => modifier switch
    {
        "alt" or "option" => "Alt",
        "shift" => "⇧",
        _ => "Ctrl",
    };

    private static string KeyCap(string key) => key switch
    {
        "space" => "Space",
        "enter" or "return" => "Enter",
        "delete" or "backspace" => "Backspace",
        "escape" or "esc" => "Esc",
        "tab" => "Tab",
        "up" => "Up",
        "down" => "Down",
        "left" => "Left",
        "right" => "Right",
        _ => key.Length == 1 || IsFunctionKey(key)
            ? key.ToUpperInvariant()
            : char.ToUpperInvariant(key[0]) + key[1..],
    };

    private static bool IsFunctionKey(string key) =>
        key.Length >= 2 && key[0] == 'f' && key[1..].All(char.IsAsciiDigit);
}
