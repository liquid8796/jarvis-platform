using System.Text;

namespace JarvisCode.Cli.Repl.Terminal;

/// <summary>
/// One keyboard event in the reference's own vocabulary: a lowercase key name
/// (<c>a</c>, <c>enter</c>, <c>escape</c>, <c>up</c>, <c>f1</c>, <c>_</c>) plus
/// the three modifiers a terminal can deliver. <see cref="Chord"/> is the
/// normalized spelling the binding table is keyed by (its <c>mNo</c>: modifiers
/// sorted, <c>meta</c> folded onto <c>alt</c>, <c>cmd</c>/<c>super</c>/<c>win</c>
/// onto <c>cmd</c>).
/// </summary>
internal sealed record KeyPress(string Key, bool Ctrl = false, bool Alt = false, bool Shift = false, bool Cmd = false)
{
    /// <summary>Printable input this press inserts, if any (a pasted chunk is one press carrying many characters).</summary>
    public string? Text { get; init; }

    /// <summary>True when the text arrived as a paste rather than typed keys.</summary>
    public bool IsPaste { get; init; }

    /// <summary>The normalized chord the binding table is keyed by.</summary>
    public string Chord => KeyChords.Normalize(this);

    public static KeyPress Typed(string text) => new KeyPress(text) { Text = text };

    public static KeyPress Paste(string text) => new KeyPress("paste") { Text = text, IsPaste = true };

    /// <summary>A key with no modifiers whose name is one printable character.</summary>
    public bool IsPrintable => Text is { Length: > 0 } && !Ctrl && !Alt && !Cmd;
}

/// <summary>
/// The reference's chord spelling rules (<c>XDe</c>/<c>mNo</c> in CLI 2.1.257):
/// a space is <c>space</c>, a sequence splits on whitespace, each chord splits on
/// <c>+</c>, modifier synonyms fold (control→ctrl, option/opt/meta→alt,
/// command/cmd/super/win→cmd), modifiers sort, and the key aliases
/// esc/return/del/arrows/caps map onto their canonical names.
/// </summary>
internal static class KeyChords
{
    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.Ordinal)
    {
        ["esc"] = "escape",
        ["return"] = "enter",
        ["del"] = "delete",
        ["↑"] = "up",
        ["↓"] = "down",
        ["←"] = "left",
        ["→"] = "right",
        ["caps"] = "capslock",
        ["caps-lock"] = "capslock",
        ["caps_lock"] = "capslock",
    };

    private static readonly string[] ModifierWords =
        ["ctrl", "control", "alt", "opt", "option", "meta", "cmd", "command", "super", "win", "shift"];

    public static string Normalize(KeyPress press)
    {
        var mods = new List<string>(3);
        if (press.Alt)
        {
            mods.Add("alt");
        }

        if (press.Cmd)
        {
            mods.Add("cmd");
        }

        if (press.Ctrl)
        {
            mods.Add("ctrl");
        }

        if (press.Shift)
        {
            mods.Add("shift");
        }

        mods.Sort(StringComparer.Ordinal);
        var key = press.Key == " " ? "space" : press.Key;
        return mods.Count == 0 ? key : string.Join('+', mods) + "+" + key;
    }

    /// <summary>The reference's <c>XDe</c>: a whole binding string, sequences included.</summary>
    public static string NormalizeSpelling(string spelling)
    {
        if (spelling == " ")
        {
            return "space";
        }

        return string.Join(' ', spelling.Trim()
            .Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeChord));
    }

    /// <summary>The reference's <c>mNo</c>: one chord.</summary>
    public static string NormalizeChord(string chord)
    {
        var mods = new List<string>();
        var key = "";
        foreach (var part in chord.Split('+'))
        {
            var p = part.Trim().ToLowerInvariant();
            if (ModifierWords.Contains(p))
            {
                if (p == "control")
                {
                    mods.Add("ctrl");
                }
                else if (p is "option" or "opt" or "meta")
                {
                    mods.Add("alt");
                }
                else if (p is "command" or "cmd" or "super" or "win")
                {
                    mods.Add("cmd");
                }
                else
                {
                    mods.Add(p);
                }
            }
            else
            {
                key = KeyAliases.TryGetValue(p, out var alias) ? alias : p;
            }
        }

        mods.Sort(StringComparer.Ordinal);
        return string.Join('+', [.. mods, key]);
    }

    /// <summary>
    /// The reference's <c>_No</c>: an empty part between two pluses is a parse
    /// error rather than a chord.
    /// </summary>
    public static string? EmptyPartError(string chord)
    {
        foreach (var part in chord.ToLowerInvariant().Split('+'))
        {
            if (part.Trim().Length == 0)
            {
                return $"Empty key part in \"{chord}\"";
            }
        }

        return null;
    }

    /// <summary>Parses a normalized chord back into a key press, for tests and hint rendering.</summary>
    public static KeyPress Parse(string chord)
    {
        var normalized = NormalizeChord(chord);
        var parts = normalized.Split('+');
        var key = parts[^1];
        bool ctrl = false, alt = false, shift = false, cmd = false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i])
            {
                case "ctrl": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "cmd": cmd = true; break;
            }
        }

        if (key == "space")
        {
            key = " ";
        }

        return new KeyPress(key, ctrl, alt, shift, cmd);
    }
}
