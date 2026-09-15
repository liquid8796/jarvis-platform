using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Repl.Keys;

/// <summary>
/// The reference's chord formatter (<c>cte</c> in CLI 2.1.257, chunk
/// <c>m88kj4yj</c>): three styles — <c>default</c> (Title keys, lower mods, "+"),
/// <c>compact</c> (lower, caret-ctrl, shift-as-case) and <c>symbol</c> (glyphs) —
/// with overrides for key case, modifier separator and case, and the shared
/// leading-modifier collapse for a list of chords ("shift+←/→").
/// </summary>
internal sealed record ChordStyle(
    string KeyCase = "title",
    string ModCase = "lower",
    bool CaretCtrl = false,
    string ModSep = "+",
    string ArrowSep = "/",
    string ChordSep = " ",
    bool ShiftAsCase = false,
    string CharCase = "preserve")
{
    public static readonly ChordStyle Default = new();
    public static readonly ChordStyle Compact = new("lower", "lower", true, "+", "", " ", true, "preserve");
    public static readonly ChordStyle Symbol = new("glyph", "glyph", false, "", "", " ", true, "upper");

    /// <summary>The <c>Dp</c> the shortcuts overlay uses: lowercase keys joined by " + ".</summary>
    public static readonly ChordStyle Overlay = new(KeyCase: "lower", ModSep: " + ");

    /// <summary>The footer's <c>{keyCase:"lower"}</c>.</summary>
    public static readonly ChordStyle LowerKeys = new(KeyCase: "lower");

    /// <summary>The resume picker's title-cased modifiers (<c>modCase:"title", charCase:"upper"</c>).</summary>
    public static readonly ChordStyle TitleMods = new(ModCase: "title", CharCase: "upper");
}

internal static class ChordFormat
{
    private static readonly Dictionary<string, string[]> KeyNames = new(StringComparer.Ordinal)
    {
        ["enter"] = ["Enter", "enter", "⏎"],
        ["escape"] = ["Esc", "esc", "⎋"],
        ["tab"] = ["Tab", "tab", "⇥"],
        [" "] = ["Space", "space", "␣"],
        ["space"] = ["Space", "space", "␣"],
        ["backspace"] = ["Backspace", "backspace", "⌫"],
        ["delete"] = ["Delete", "delete", "⌦"],
        ["up"] = ["↑", "↑", "↑"],
        ["down"] = ["↓", "↓", "↓"],
        ["left"] = ["←", "←", "←"],
        ["right"] = ["→", "→", "→"],
        ["pageup"] = ["PageUp", "pgup", "⇞"],
        ["pagedown"] = ["PageDown", "pgdn", "⇟"],
        ["home"] = ["Home", "home", "↖"],
        ["end"] = ["End", "end", "↘"],
    };

    private static readonly HashSet<string> Arrows = ["up", "down", "left", "right"];

    private static int CaseIndex(string keyCase) => keyCase switch
    {
        "title" => 0,
        "lower" => 1,
        "glyph" => 2,
        _ => 0,
    };

    private static string Modifier(string mod, ChordStyle style)
    {
        var modCase = style.ModCase;
        return mod switch
        {
            "ctrl" => modCase switch { "glyph" => "⌃", "title" => "Ctrl", _ => "ctrl" },
            "shift" => modCase switch { "glyph" => "⇧", "title" => "Shift", _ => "shift" },
            "alt" => modCase switch { "glyph" => "⌥", "title" => "Alt", _ => "alt" },
            "cmd" => modCase switch { "glyph" => "⌘", "title" => "Super", _ => "super" },
            _ => mod,
        };
    }

    private static string KeyName(string key, ChordStyle style)
    {
        if (KeyNames.TryGetValue(key, out var names))
        {
            return names[CaseIndex(style.KeyCase)];
        }

        return style.CharCase == "upper" ? key.ToUpperInvariant() : key;
    }

    private static List<string> Mods(KeyPress press)
    {
        var mods = new List<string>(4);
        if (press.Ctrl) mods.Add("ctrl");
        if (press.Shift) mods.Add("shift");
        if (press.Alt) mods.Add("alt");
        if (press.Cmd) mods.Add("cmd");
        return mods;
    }

    private static bool ShiftAsCase(KeyPress press) =>
        press.Shift && !press.Ctrl && !press.Alt && !press.Cmd && press.Key.Length == 1 && press.Key[0] is >= 'a' and <= 'z';

    /// <summary>The reference's <c>U</c>: one chord.</summary>
    public static string One(KeyPress press, ChordStyle style)
    {
        if (style.ShiftAsCase && ShiftAsCase(press))
        {
            return press.Key.ToUpperInvariant();
        }

        var mods = Mods(press);
        var name = KeyName(press.Key, style);
        if (style.CaretCtrl && mods.Count == 1 && mods[0] == "ctrl")
        {
            return "^" + name;
        }

        if (style.ModCase == "glyph")
        {
            return string.Concat(mods.Select(m => Modifier(m, style))) + name;
        }

        var parts = mods.Select(m => Modifier(m, style)).ToList();
        parts.Add(name);
        return string.Join(style.ModSep, parts);
    }

    /// <summary>The reference's <c>cte</c>: one or more chords, each possibly a sequence.</summary>
    public static string Format(IReadOnlyList<string> chords, ChordStyle? style = null)
    {
        style ??= ChordStyle.Default;
        if (chords.Count == 0)
        {
            return "";
        }

        var sequences = chords.Select(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(KeyChords.Parse).ToList()).ToList();
        string Sequence(List<KeyPress> seq) => string.Join(style.ChordSep, seq.Select(p => One(p, style)));

        if (sequences.Count == 1)
        {
            return Sequence(sequences[0]);
        }

        if (sequences.Any(s => s.Count != 1))
        {
            return string.Join("/", sequences.Select(Sequence));
        }

        var singles = sequences.Select(s => s[0]).ToList();
        var shared = SharedLeadingModifiers(singles, style);
        bool allArrows = singles.All(p => Arrows.Contains(p.Key));
        var sep = allArrows && (shared is not null || singles.All(p => EffectiveMods(p, style).Count == 0))
            ? style.ArrowSep
            : "/";
        if (shared is not null)
        {
            var bare = singles.Select(p => One(p with { Ctrl = false, Alt = false, Shift = false, Cmd = false }, style));
            return Prefix(shared, style) + string.Join(sep, bare);
        }

        return string.Join(sep, singles.Select(p => One(p, style)));
    }

    public static string Format(string chord, ChordStyle? style = null) => Format([chord], style);

    private static List<string> EffectiveMods(KeyPress press, ChordStyle style) =>
        style.ShiftAsCase && ShiftAsCase(press) ? [] : Mods(press);

    private static KeyPress? SharedLeadingModifiers(List<KeyPress> presses, ChordStyle style)
    {
        var first = presses[0];
        if (EffectiveMods(first, style).Count == 0)
        {
            return null;
        }

        var firstMods = EffectiveMods(first, style);
        foreach (var other in presses.Skip(1))
        {
            if (!EffectiveMods(other, style).SequenceEqual(firstMods))
            {
                return null;
            }
        }

        return first;
    }

    private static string Prefix(KeyPress press, ChordStyle style)
    {
        var mods = Mods(press);
        if (style.CaretCtrl && mods.Count == 1 && mods[0] == "ctrl")
        {
            return "^";
        }

        if (style.ModCase == "glyph")
        {
            return string.Concat(mods.Select(m => Modifier(m, style)));
        }

        return string.Join(style.ModSep, mods.Select(m => Modifier(m, style))) + style.ModSep;
    }

    /// <summary>The reference's <c>F</c> component: "{chord} to {action}", or in parentheses.</summary>
    public static string Hint(string chord, string action, ChordStyle? style = null, bool parens = false)
    {
        var text = Format(chord, style);
        return parens ? $"({text} to {action})" : $"{text} to {action}";
    }

    public static string Hint(IReadOnlyList<string> chords, string action, ChordStyle? style = null, bool parens = false)
    {
        var text = Format(chords, style);
        return parens ? $"({text} to {action})" : $"{text} to {action}";
    }
}
