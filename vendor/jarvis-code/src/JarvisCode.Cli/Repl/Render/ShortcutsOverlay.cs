using JarvisCode.Cli.Repl.Keys;

namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// The <c>?</c> overlay (CLI 2.1.257, its <c>BHe</c>): three columns of hints,
/// 24 / 35 / free wide, every chord resolved through the binding table so a
/// rebound key is what the overlay offers. The reference's chord style here is
/// <c>{keyCase:"lower", modSep:" + "}</c>, which is why a modifier reads
/// "ctrl + o" rather than "ctrl+o".
/// </summary>
internal static class ShortcutsOverlay
{
    /// <summary>The reference's <c>Dp</c>.</summary>
    public static readonly ChordStyle Style = new(KeyCase: "lower", ModSep: " + ");

    public const int FirstColumnWidth = 24;
    public const int SecondColumnWidth = 35;

    /// <summary>The reference's newline hint (<c>h7t</c>), in the form this terminal can honour.</summary>
    public const string NewlineHint = "backslash (\\) + return (⏎) for newline";

    /// <summary>Its shift+enter variant, for a terminal that delivers the chord.</summary>
    public const string NewlineHintShiftEnter = "shift + ⏎ for newline";

    public static IReadOnlyList<string> FirstColumn() =>
    [
        Footer.ShellMode,
        "/ for commands",
        "@ for file paths",
        Footer.SideQuestionHint,
    ];

    public static IReadOnlyList<string> SecondColumn(KeyMap map, bool shiftEnterSupported = false) =>
    [
        "double tap esc to clear input",
        ChordFormat.Hint(map.ChordFor("chat:cycleMode", "Chat", KeyBindings.CycleModeChord), "auto-accept edits", Style),
        $"{ChordFormat.Format(map.ChordFor("app:toggleTranscript", "Global", "ctrl+o"), Style)} for verbose output",
        ChordFormat.Hint(map.ChordFor("app:toggleTodos", "Global", "ctrl+t"), "toggle tasks", Style),
        shiftEnterSupported ? NewlineHintShiftEnter : NewlineHint,
    ];

    public static IReadOnlyList<string> ThirdColumn(KeyMap map, bool suspendAvailable = false) =>
    [
        ChordFormat.Hint(map.ChordFor("chat:undo", "Chat", "ctrl+_"), "undo", Style),
        .. suspendAvailable ? (string[])[ChordFormat.Hint("ctrl+z", "suspend", Style)] : [],
        ChordFormat.Hint(map.ChordFor("chat:imagePaste", "Chat", KeyBindings.ImagePasteChord), "paste images", Style),
        ChordFormat.Hint(map.ChordFor("chat:modelPicker", "Chat", "alt+p"), "switch model", Style),
        ChordFormat.Hint(map.ChordFor("chat:stash", "Chat", "ctrl+s"), "stash prompt", Style),
        ChordFormat.Hint(map.ChordFor("chat:externalEditor", "Chat", "ctrl+g"), "edit in $EDITOR", Style),
        "/keybindings to customize",
    ];

    /// <summary>The three columns laid out side by side at the terminal's width.</summary>
    public static IReadOnlyList<string> Render(
        KeyMap map, int columns, bool shiftEnterSupported = false, bool suspendAvailable = false)
    {
        var first = FirstColumn();
        var second = SecondColumn(map, shiftEnterSupported);
        var third = ThirdColumn(map, suspendAvailable);
        int rows = Math.Max(first.Count, Math.Max(second.Count, third.Count));
        bool narrow = columns < FirstColumnWidth + SecondColumnWidth + 20;
        if (narrow)
        {
            // Too narrow for three columns: the reference falls back to one.
            return [.. first, .. second, .. third];
        }

        var lines = new List<string>(rows);
        for (int i = 0; i < rows; i++)
        {
            var a = TextWidth.PadRight(i < first.Count ? first[i] : "", FirstColumnWidth);
            var b = TextWidth.PadRight(i < second.Count ? second[i] : "", SecondColumnWidth);
            var c = i < third.Count ? third[i] : "";
            lines.Add((a + " " + b + " " + c).TrimEnd());
        }

        return lines;
    }
}
