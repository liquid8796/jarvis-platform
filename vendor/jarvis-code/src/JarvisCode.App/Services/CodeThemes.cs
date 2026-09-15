using System.Windows.Media;

namespace JarvisCode.App.Services;

public enum CodeThemeMode
{
    Light,
    Dark,
}

/// <summary>
/// One code theme as this app's highlighter can honour it: the editor background
/// and foreground plus the colour of each token kind the highlighter emits. A null
/// colour keeps the app's own theme brush, which is how the two Claude themes work
/// in the reference too — their background is the surface token, not a literal.
/// </summary>
public sealed record CodeTheme(
    string Id,
    string Label,
    CodeThemeMode Mode,
    string? Background,
    string? Foreground,
    string? Comment,
    string? String,
    string? Keyword,
    string? Number,
    string? Variable)
{
    public Color? Color(string? hex) => hex is null ? null : Theming.CssColor.TryParse(hex, out var color) ? color : null;
}

/// <summary>
/// The code themes the reference's "Light code theme" / "Dark code theme" selects
/// offer (ion-dist <c>c5d464fa3-TkKojVeh.js</c>, its <c>w</c> list, in its order), each
/// reduced to what this highlighter paints. The colours are read out of the Shiki
/// theme JSON the desktop itself bundles (<c>cd14a9327-jHAmUVkN.js</c> and its
/// siblings hold one <c>JSON.parse</c>'d theme each): <c>editor.background</c> and
/// <c>editor.foreground</c> from <c>colors</c>, and the winning <c>tokenColors</c>
/// rule for the scopes this tokenizer's six kinds correspond to — <c>comment</c>,
/// <c>string</c>, <c>keyword</c>, <c>constant.numeric</c> and
/// <c>variable.other.normal</c> (a plain <c>$name</c> reference, which is the only
/// thing this tokenizer calls a variable). A theme that names no colour for a kind
/// leaves it null and the app's own brush paints it.
/// </summary>
public static class CodeThemes
{
    public const string DefaultLight = "claude-light";
    public const string DefaultDark = "claude-dark";

    public static readonly IReadOnlyList<CodeTheme> All =
    [
        // The two Claude themes are the reference's own palettes rather than Shiki's:
        // light is the hex table in the same chunk (its `p`), dark is
        // shared-12's `Ba` hsl set converted with the chunk's own `h()`, and both
        // take their editor background from the surface token the chunk's `k()`
        // assigns — shared-6's Ip (#fcfcfb) and Up (#111111).
        new(DefaultLight, "Claude Light", CodeThemeMode.Light, "#fcfcfb", "#1a1a1a", "#999999", "#1e9e3c", "#c5621b", "#cd2054", null),
        new(DefaultDark, "Claude Dark", CodeThemeMode.Dark, "#111111", "#eaecf0", "#818898", "#9be963", "#cc7bf4", "#5eeded", "#fbad60"),
        new("github-light", "GitHub Light", CodeThemeMode.Light, "#fff", "#24292e", "#6a737d", "#032f62", "#d73a49", "#005cc5", "#24292e"),
        new("github-dark", "GitHub Dark", CodeThemeMode.Dark, "#24292e", "#e1e4e8", "#6a737d", "#9ecbff", "#f97583", "#79b8ff", "#e1e4e8"),
        new("github-dark-dimmed", "GitHub Dark Dimmed", CodeThemeMode.Dark, "#22272e", "#adbac7", "#768390", "#96d0ff", "#f47067", "#6cb6ff", "#adbac7"),
        new("pierre-light", "Pierre Light", CodeThemeMode.Light, "#ffffff", "#0a0a0a", "#737373", "#199f43", "#d32a61", "#1ca1c7", "#d47628"),
        new("pierre-dark", "Pierre Dark", CodeThemeMode.Dark, "#0a0a0a", "#fafafa", "#737373", "#5ecc71", "#ff678d", "#68cdf2", "#ffa359"),
        new("one-dark-pro", "One Dark Pro", CodeThemeMode.Dark, "#282c34", "#abb2bf", "#7f848e", "#98c379", "#c678dd", "#d19a66", "#e06c75"),
        new("one-light", "One Light", CodeThemeMode.Light, "#FAFAFA", "#383A42", "#A0A1A7", "#50A14F", "#A626A4", "#986801", "#E45649"),
        new("dracula", "Dracula", CodeThemeMode.Dark, "#282A36", "#F8F8F2", "#6272A4", "#F1FA8C", "#FF79C6", "#BD93F9", "#F8F8F2"),
        new("dracula-soft", "Dracula Soft", CodeThemeMode.Dark, "#282A36", "#f6f6f4", "#7b7f8b", "#e7ee98", "#f286c4", "#bf9eee", "#f6f6f4"),
        new("catppuccin-latte", "Catppuccin Latte", CodeThemeMode.Light, "#eff1f5", "#4c4f69", "#7c7f93", "#40a02b", "#8839ef", "#fe640b", null),
        new("catppuccin-mocha", "Catppuccin Mocha", CodeThemeMode.Dark, "#1e1e2e", "#cdd6f4", "#9399b2", "#a6e3a1", "#cba6f7", "#fab387", null),
        new("nord", "Nord", CodeThemeMode.Dark, "#2e3440", "#d8dee9", "#616E88", "#A3BE8C", "#81A1C1", "#B48EAD", "#D8DEE9"),
        new("solarized-dark", "Solarized Dark", CodeThemeMode.Dark, "#002B36", "#839496", "#586E75", "#2AA198", "#859900", "#D33682", "#268BD2"),
        new("solarized-light", "Solarized Light", CodeThemeMode.Light, "#FDF6E3", "#657B83", "#93A1A1", "#2AA198", "#859900", "#D33682", "#268BD2"),
        new("vitesse-dark", "Vitesse Dark", CodeThemeMode.Dark, "#121212", "#dbd7caee", "#758575dd", "#c98a7d", "#4d9375", "#4C9A91", "#bd976a"),
        new("vitesse-light", "Vitesse Light", CodeThemeMode.Light, "#ffffff", "#393a34", "#a0ada0", "#b56959", "#1e754f", "#2f798a", "#b07d48"),
        new("min-dark", "Min Dark", CodeThemeMode.Dark, "#1f1f1f", null, "#6b737c", "#9db1c5", "#f97583", "#f8f8f8", null),
        new("min-light", "Min Light", CodeThemeMode.Light, "#ffffff", "#212121", "#c2c3c5", "#2b5581", "#D32F2F", "#1976D2", null),
        new("monokai", "Monokai", CodeThemeMode.Dark, "#272822", "#f8f8f2", "#88846f", "#E6DB74", "#F92672", "#AE81FF", "#F8F8F2"),
        new("tokyo-night", "Tokyo Night", CodeThemeMode.Dark, "#1a1b26", "#a9b1d6", "#51597d", "#9ece6a", "#bb9af7", "#ff9e64", "#c0caf5"),
        new("night-owl", "Night Owl", CodeThemeMode.Dark, "#011627", "#d6deeb", "#637777", "#ecc48d", "#c792ea", "#F78C6C", "#c5e478"),
        new("rose-pine", "Rosé Pine", CodeThemeMode.Dark, "#191724", "#e0def4", "#6e6a86", "#f6c177", "#31748f", "#ebbcba", "#e0def4"),
        new("rose-pine-dawn", "Rosé Pine Dawn", CodeThemeMode.Light, "#faf4ed", "#575279", "#9893a5", "#ea9d34", "#286983", "#d7827e", "#575279"),
        new("ayu-dark", "Ayu Dark", CodeThemeMode.Dark, "#10141c", "#bfbdb6", "#5a6673", "#aad94c", "#ff8f40", "#d2a6ff", "#bfbdb6"),
        new("slack-dark", "Slack Dark", CodeThemeMode.Dark, "#222222", "#E6E6E6", "#6A9955", "#ce9178", "#569cd6", "#b5cea8", "#9CDCFE"),
        new("slack-ochin", "Slack Ochin", CodeThemeMode.Light, "#FFF", "#000", "#357b42", "#a44185", "#7b30d0", "#174781", "#2f86d2"),
    ];

    /// <summary>The themes the light select lists — the reference filters its list by <c>mode</c>.</summary>
    public static IReadOnlyList<CodeTheme> LightThemes { get; } = [.. All.Where(t => t.Mode == CodeThemeMode.Light)];

    /// <summary>The themes the dark select lists.</summary>
    public static IReadOnlyList<CodeTheme> DarkThemes { get; } = [.. All.Where(t => t.Mode == CodeThemeMode.Dark)];

    public static CodeTheme? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The theme in force for a mode, falling back to the Claude theme of that mode when the stored id is unknown.</summary>
    public static CodeTheme Resolve(string? id, bool dark) =>
        Find(id) is { } theme && theme.Mode == (dark ? CodeThemeMode.Dark : CodeThemeMode.Light)
            ? theme
            : Find(dark ? DefaultDark : DefaultLight)!;

    /// <summary>
    /// The theme every code view paints with right now: the light or dark pick
    /// after the app's resolved theme mode. Set by the App when either changes.
    /// </summary>
    public static CodeTheme Active { get; private set; } = Find(DefaultLight)!;

    public static event EventHandler? ActiveChanged;

    public static void Apply(UiSettings settings, bool dark)
    {
        var next = Resolve(dark ? settings.CodeThemeDark : settings.CodeThemeLight, dark);
        if (ReferenceEquals(next, Active))
        {
            return;
        }

        Active = next;
        ActiveChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The reference's preview file: <c>greet.ts</c> before and after an edit, shown as a unified diff.</summary>
    public const string PreviewOld = "function greet(name: string) {\n  return \"Hello, \" + name;\n}\n";

    public const string PreviewNew = "function greet(name: string) {\n  return `Hello, ${name}!`;\n}\n";
}
