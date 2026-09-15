using System.Windows.Input;

namespace JarvisCode.App.Services;

/// <summary>
/// The Quick Entry shortcut as the reference desktop stores and shows it — an
/// Electron accelerator ("Ctrl+Alt+Space") — ported from the Desktop settings
/// page's recorder (ion-dist <c>c71860c77-j8GBEGCP.js</c>: <c>re</c>/<c>se</c>/<c>le</c>
/// for the display, <c>ne</c> for the recorder and its reserved list) and the main
/// process's registration (<c>lP</c>/<c>uP</c> in <c>index.chunk-DnlgCaT3.js</c>).
/// Measured on the reference: outside macOS <c>nativeQuickEntry</c> is
/// <c>unavailable</c>, so Windows shows the legacy recorder row and neither the
/// Tap-Alt-twice picker nor the dictation shortcut, which are darwin-only there.
/// </summary>
public static class QuickEntryShortcuts
{
    /// <summary>What this build registered before the setting existed.</summary>
    public const string DefaultAccelerator = "Ctrl+Alt+Space";

    /// <summary>
    /// The reference's reserved combinations — common actions a global shortcut
    /// must not steal (its <c>m</c> in the recorder).
    /// </summary>
    public static readonly IReadOnlyList<string> Reserved =
    [
        "Cmd+Q", "Cmd+W", "Cmd+H", "Cmd+M", "Cmd+,", "Cmd+N", "Cmd+O", "Cmd+T", "Cmd+S", "Cmd+C", "Cmd+V",
        "Ctrl+W", "Ctrl+N", "Ctrl+O", "Ctrl+T", "Ctrl+S", "Ctrl+C", "Ctrl+V",
    ];

    public const string ReservedMessage = "This shortcut is reserved for common actions. Try a different combination.";
    public const string InUseMessage = "This shortcut is already in use by another app. Try a different combination.";
    public const string UnsupportedMessage = "This shortcut combination isn’t supported. Try a different combination.";
    public const string Placeholder = "Set shortcut";

    /// <summary>A parsed accelerator: modifier flags plus the key token.</summary>
    public readonly record struct Chord(bool Control, bool Alt, bool Shift, bool Win, string Key)
    {
        public bool HasModifier => Control || Alt || Shift || Win;
    }

    /// <summary>
    /// The reference's <c>re</c> table for win32: each modifier name, the label it
    /// shows and the order it sorts to — Control first, then Alt, Shift, Windows.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Label, int Order)> ModifierLabels =
        new Dictionary<string, (string, int)>(StringComparer.Ordinal)
        {
            ["CommandOrControl"] = ("Control", 3),
            ["CmdOrCtrl"] = ("Control", 3),
            ["AltGr"] = ("Alt", 1),
            ["Command"] = ("Windows", 3),
            ["Cmd"] = ("Windows", 3),
            ["Control"] = ("Control", 0),
            ["Ctrl"] = ("Control", 0),
            ["Alt"] = ("Alt", 1),
            ["Option"] = ("Alt", 1),
            ["Shift"] = ("Shift", 2),
        };

    /// <summary>The reference's <c>le</c>: modifiers sorted by their order, labelled, then the keys, joined with "+".</summary>
    public static string Display(string accelerator)
    {
        if (string.IsNullOrWhiteSpace(accelerator))
        {
            return "";
        }

        var parts = accelerator.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = new List<(string Label, int Order)>();
        var keys = new List<string>();
        foreach (var part in parts)
        {
            if (ModifierLabels.TryGetValue(part, out var modifier))
            {
                modifiers.Add(modifier);
            }
            else
            {
                keys.Add(part);
            }
        }

        return string.Join("+", modifiers.OrderBy(m => m.Order).Select(m => m.Label).Concat(keys));
    }

    /// <summary>Parses an accelerator into a chord; null when it names no key.</summary>
    public static Chord? Parse(string accelerator)
    {
        if (string.IsNullOrWhiteSpace(accelerator))
        {
            return null;
        }

        bool control = false, alt = false, shift = false, win = false;
        string? key = null;
        foreach (var part in accelerator.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part)
            {
                case "Ctrl":
                case "Control":
                case "CommandOrControl":
                case "CmdOrCtrl":
                    control = true;
                    break;
                case "Alt":
                case "Option":
                case "AltGr":
                    alt = true;
                    break;
                case "Shift":
                    shift = true;
                    break;
                case "Cmd":
                case "Command":
                case "Super":
                case "Meta":
                    win = true;
                    break;
                default:
                    key = part;
                    break;
            }
        }

        return key is null ? null : new Chord(control, alt, shift, win, key);
    }

    /// <summary>
    /// The reference's recorder rule: a bare key is refused unless it is a
    /// function key, and the reserved combinations are refused by name.
    /// </summary>
    public static bool IsReserved(IReadOnlyList<string> modifiers, string key)
    {
        var isFunctionKey = key.StartsWith('F') && key.Length > 1;
        if (modifiers.Count == 0 && !isFunctionKey)
        {
            return true;
        }

        var joined = string.Join("+", modifiers.Append(key));
        return Reserved.Contains(joined, StringComparer.Ordinal);
    }

    /// <summary>
    /// The key token the reference derives from a DOM key event: a letter from
    /// its KeyX code, "Space", an upper-cased single character, else the code
    /// name itself. Null for a modifier alone or a key WPF has no name for.
    /// </summary>
    public static string? KeyToken(Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None)
        {
            return null;
        }

        if (key >= Key.A && key <= Key.Z)
        {
            return key.ToString();
        }

        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString();
        }

        if (key >= Key.F1 && key <= Key.F24)
        {
            return key.ToString();
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Enter => "Enter",
            Key.Escape => "Escape",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            _ => null,
        };
    }

    /// <summary>The modifier names the recorder records, in the order it pushes them: Shift, Alt, Ctrl, Cmd.</summary>
    public static IReadOnlyList<string> Modifiers(ModifierKeys modifiers)
    {
        var names = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Shift)) names.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) names.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Control)) names.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Windows)) names.Add("Cmd");
        return names;
    }

    /// <summary>The accelerator the recorder stores for a chord: modifiers then the key, joined with "+".</summary>
    public static string Compose(IReadOnlyList<string> modifiers, string key) =>
        string.Join("+", modifiers.Append(key));

    /// <summary>The Win32 virtual-key code a key token registers as; null when none maps.</summary>
    public static int? VirtualKey(string key)
    {
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }

            return c switch
            {
                ',' => 0xBC,
                '.' => 0xBE,
                '-' => 0xBD,
                '=' => 0xBB,
                '/' => 0xBF,
                ';' => 0xBA,
                '\'' => 0xDE,
                '[' => 0xDB,
                ']' => 0xDD,
                '\\' => 0xDC,
                '`' => 0xC0,
                _ => null,
            };
        }

        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24)
        {
            return 0x70 + f - 1;
        }

        return key switch
        {
            "Space" => 0x20,
            "Tab" => 0x09,
            "Enter" or "Return" => 0x0D,
            "Escape" or "Esc" => 0x1B,
            "Backspace" => 0x08,
            "Delete" => 0x2E,
            "Insert" => 0x2D,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "Up" => 0x26,
            "Down" => 0x28,
            "Left" => 0x25,
            "Right" => 0x27,
            _ => null,
        };
    }
}
