using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace JarvisCode.App.Theming;

public enum ThemeMode { System, Light, Dark }

/// <summary>
/// Owns the active theme + light/dark mode and materializes the token map into
/// frozen brushes in <see cref="Application.Resources"/>. All XAML consumes the
/// tokens through DynamicResource, so applying a theme is live.
/// </summary>
public sealed class ThemeService
{
    private readonly ThemeCatalog _catalog;

    public ThemeService(ThemeCatalog catalog)
    {
        _catalog = catalog;
    }

    public ThemeCatalog Catalog => _catalog;

    public ThemeDefinition ActiveTheme { get; private set; } = StockPalette.Theme;

    public ThemeMode Mode { get; private set; } = ThemeMode.System;

    public bool IsDark { get; private set; }

    /// <summary>The spinner drawn while the assistant is working; themes may replace it.</summary>
    public SpinnerSpec ActiveSpinner { get; private set; } = DefaultSpinner;

    public event EventHandler? ThemeChanged;

    public void Apply(string? themeKey, ThemeMode mode)
    {
        ActiveTheme = _catalog.Find(themeKey) ?? StockPalette.Theme;
        Mode = mode;
        IsDark = mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => SystemPrefersDark(),
        };
        ActiveSpinner = ActiveTheme.Spinner ?? DefaultSpinner;

        var tokens = ResolveTokens(ActiveTheme, IsDark);
        var resources = Application.Current.Resources;
        foreach (var (token, value) in tokens)
        {
            if (CssColor.TryParse(value, out var color))
            {
                SetBrush(resources, ResourceKeyFor(token), color);
            }
        }

        ApplyDerived(resources, tokens);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-evaluates the OS light/dark preference (System mode only).</summary>
    public void RefreshSystemMode()
    {
        if (Mode == ThemeMode.System && SystemPrefersDark() != IsDark)
        {
            Apply(ActiveTheme.Key, Mode);
        }
    }

    /// <summary>Theme tokens for the requested variant, with stock values backfilling gaps.</summary>
    public static Dictionary<string, string> ResolveTokens(ThemeDefinition theme, bool dark)
    {
        var result = new Dictionary<string, string>(dark ? StockPalette.Dark : StockPalette.Light, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in dark ? theme.Dark : theme.Light)
        {
            result[key] = value;
        }

        return result;
    }

    /// <summary>"--bg-000" → "Bg000Brush", "--claude-accent-clay" → "ClaudeAccentClayBrush".</summary>
    public static string ResourceKeyFor(string token)
    {
        var sb = new StringBuilder(token.Length);
        var upperNext = true;
        foreach (var ch in token)
        {
            if (ch == '-')
            {
                upperNext = true;
                continue;
            }

            sb.Append(upperNext ? char.ToUpperInvariant(ch) : ch);
            upperNext = false;
        }

        sb.Append("Brush");
        return sb.ToString();
    }

    private void ApplyDerived(ResourceDictionary resources, Dictionary<string, string> tokens)
    {
        var fg = Parse(tokens, "--text-000");
        var border200 = Parse(tokens, "--border-200");
        var border300 = Parse(tokens, "--border-300");
        var accent = Parse(tokens, "--accent-brand");

        // Alpha-composited tokens the renderer derives at the use site.
        SetBrush(resources, "HoverOverlayBrush", CssColor.WithAlpha(fg, 0.05));
        SetBrush(resources, "SelectedOverlayBrush", CssColor.WithAlpha(fg, 0.10));
        SetBrush(resources, "ActiveOverlayBrush", CssColor.WithAlpha(fg, 0.14));
        SetBrush(resources, "StrongOverlayBrush", CssColor.WithAlpha(fg, 0.20));

        // Input-field fill. Dark themes tint the surface with ink, light themes
        // lift it with white — the same asymmetry the reference app uses.
        SetBrush(resources, "FieldFillBrush", IsDark
            ? CssColor.WithAlpha(fg, 0.05)
            : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        SetBrush(resources, "BorderSoftBrush", CssColor.WithAlpha(border200, 0.18));
        SetBrush(resources, "BorderMidBrush", CssColor.WithAlpha(border300, 0.30));
        SetBrush(resources, "BorderStrongBrush", CssColor.WithAlpha(border300, 0.45));
        SetBrush(resources, "UserMessageBgBrush", CssColor.WithAlpha(fg, 0.08));
        SetBrush(resources, "SelectionBrush", CssColor.WithAlpha(accent, 0.30));
        SetBrush(resources, "ScrollThumbBrush", CssColor.WithAlpha(accent, 0.50));
        SetBrush(resources, "ScrollThumbHoverBrush", CssColor.WithAlpha(accent, 0.70));
        SetBrush(resources, "AccentSoftBrush", CssColor.WithAlpha(accent, 0.12));

        // Panel toggles in the session header: the reference paints the active one on a
        // blue chip drawn from the interface accent, not the brand clay.
        var accentUi = Parse(tokens, "--accent-100");
        SetBrush(resources, "PanelToggleActiveBrush", CssColor.WithAlpha(accentUi, 0.22));
        SetBrush(resources, "PanelToggleActiveFgBrush", Parse(tokens, "--accent-000"));

        // Diff rows in the changes panel: a wash of the semantic colour behind the line,
        // with the marker column carrying the full-strength colour.
        var added = Parse(tokens, "--success-100");
        var removed = Parse(tokens, "--danger-100");
        SetBrush(resources, "DiffAddedBgBrush", CssColor.WithAlpha(added, 0.14));
        SetBrush(resources, "DiffRemovedBgBrush", CssColor.WithAlpha(removed, 0.14));

        // The reference's git palette, which its PR bar, its sidebar badges and its
        // home view's pull-request pills paint with. These are fixed colours in its
        // own stylesheet rather than palette tokens — one pair per state, the darker
        // half for a light theme — so they are the same in all 97 palettes here too.
        SetBrush(resources, "GitOpenedBrush", CssColor.Parse(IsDark ? "#32d74b" : "#1e9e3c"));
        SetBrush(resources, "GitDraftBrush", CssColor.Parse(IsDark ? "#a6a6a6" : "#737373"));
        SetBrush(resources, "GitMergedBrush", CssColor.Parse(IsDark ? "#b796ff" : "#8e6bd9"));
        SetBrush(resources, "GitClosedBrush", CssColor.Parse(IsDark ? "#ff6159" : "#ff3a30"));
        SetBrush(resources, "GitConflictingBrush", CssColor.Parse(IsDark ? "#fa832e" : "#c5621b"));
        SetBrush(resources, "GitQueuedBrush", CssColor.Parse(IsDark ? "#ffd014" : "#98801f"));
        SetBrush(resources, "GitRemovedBrush", CssColor.Parse(IsDark ? "#ff2c56" : "#cd2054"));

        // The reference's --t1..--t4 tint ramp, which its Code transcript paints
        // table cells, quote rules and inline code with. Light and dark differ on
        // the middle two, so the ramp is not a single alpha scale.
        SetBrush(resources, "Tint1Brush", CssColor.WithAlpha(fg, 0.04));
        SetBrush(resources, "Tint2Brush", CssColor.WithAlpha(fg, IsDark ? 0.08 : 0.06));
        SetBrush(resources, "Tint3Brush", CssColor.WithAlpha(fg, IsDark ? 0.12 : 0.10));
        SetBrush(resources, "Tint4Brush", CssColor.WithAlpha(fg, 0.16));

        // --fill-assistant-code: the transcript's inline code chip, t1 on light and t2 on dark.
        SetBrush(resources, "AssistantCodeFillBrush", CssColor.WithAlpha(fg, IsDark ? 0.08 : 0.04));

        // The Chat renderer's own compositions, which are struck from different
        // tokens than the transcript's: the chip fills from text-200, the code
        // card from bg-000, and the three rules from border-300 at three alphas.
        SetBrush(resources, "ChatCodeChipFillBrush", CssColor.WithAlpha(Parse(tokens, "--text-200"), 0.05));
        SetBrush(resources, "ChatCodeCardBgBrush", CssColor.WithAlpha(Parse(tokens, "--bg-000"), 0.50));
        SetBrush(resources, "ChatQuoteRuleBrush", CssColor.WithAlpha(border300, 0.10));
        SetBrush(resources, "ChatTableHeadRuleBrush", CssColor.WithAlpha(border300, 0.60));
        SetBrush(resources, "ChatTableRuleBrush", CssColor.WithAlpha(border300, 0.30));

        // decoration-current/40: the link underline sits at 40% of the text colour
        // and goes to full strength on hover.
        SetBrush(resources, "LinkUnderlineBrush", CssColor.WithAlpha(Parse(tokens, "--text-100"), 0.40));

        resources["IsDarkTheme"] = IsDark;
    }

    private static Color Parse(Dictionary<string, string> tokens, string key)
        => tokens.TryGetValue(key, out var value) && CssColor.TryParse(value, out var color)
            ? color
            : Colors.Magenta;

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
        resources[key[..^"Brush".Length] + "Color"] = color;
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>An original eight-ray starburst; rays alternate long/short.</summary>
    public static SpinnerSpec DefaultSpinner { get; } = new()
    {
        ViewBox = "0 0 100 100",
        Animation = "spin",
        Paths =
        [
            new SpinnerPath
            {
                D = "M50 4 L56 38 L50 46 L44 38 Z " +
                    "M96 50 L62 56 L54 50 L62 44 Z " +
                    "M50 96 L44 62 L50 54 L56 62 Z " +
                    "M4 50 L38 44 L46 50 L38 56 Z " +
                    "M82 18 L60 40 L52 40 L60 32 Z " +
                    "M82 82 L60 68 L60 60 L68 60 Z " +
                    "M18 82 L40 60 L48 60 L40 68 Z " +
                    "M18 18 L40 32 L40 40 L32 40 Z",
            },
        ],
    };
}
