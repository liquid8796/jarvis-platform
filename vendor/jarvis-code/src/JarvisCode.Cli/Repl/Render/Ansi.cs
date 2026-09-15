using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// The reference terminal palette (CLI 2.1.257 chunk <c>@184618110</c>): the
/// dark theme's truecolor values and, for a terminal without 24-bit colour, the
/// ANSI names the reference falls back to. Keys are the reference's own token
/// names so a renderer reads like the component it was ported from.
/// </summary>
internal sealed class Theme
{
    private readonly Dictionary<string, (int R, int G, int B)> _rgb;
    private readonly Dictionary<string, int> _ansi;

    private Theme(Dictionary<string, (int, int, int)> rgb, Dictionary<string, int> ansi)
    {
        _rgb = rgb;
        _ansi = ansi;
    }

    /// <summary>The reference's dark theme (its <c>B</c> table).</summary>
    public static Theme Dark { get; } = new(
        new Dictionary<string, (int, int, int)>
        {
            ["autoAccept"] = (175, 135, 255),
            ["bashBorder"] = (253, 93, 177),
            ["claude"] = (215, 119, 87),
            ["claudeShimmer"] = (235, 159, 127),
            ["permission"] = (177, 185, 249),
            ["planMode"] = (72, 150, 140),
            ["ide"] = (71, 130, 200),
            ["promptBorder"] = (136, 136, 136),
            ["text"] = (255, 255, 255),
            ["inverseText"] = (0, 0, 0),
            ["inactive"] = (153, 153, 153),
            ["subtle"] = (80, 80, 80),
            ["suggestion"] = (177, 185, 249),
            ["remember"] = (177, 185, 249),
            ["background"] = (0, 204, 204),
            ["success"] = (78, 186, 101),
            ["error"] = (255, 107, 128),
            ["warning"] = (255, 193, 7),
            ["diffAdded"] = (34, 92, 43),
            ["diffRemoved"] = (122, 41, 54),
            ["diffAddedWord"] = (56, 166, 96),
            ["diffRemovedWord"] = (179, 89, 107),
            ["professionalBlue"] = (106, 155, 204),
            ["clawd_body"] = (215, 119, 87),
            ["fastMode"] = (255, 106, 0),
            ["effortUltra"] = (175, 135, 255),
        },
        new Dictionary<string, int>
        {
            // The reference's ANSI table (its Y): the 30–37/90–97 codes.
            ["autoAccept"] = 35, ["bashBorder"] = 35, ["claude"] = 91, ["claudeShimmer"] = 93,
            ["permission"] = 34, ["planMode"] = 36, ["ide"] = 94, ["promptBorder"] = 37,
            ["text"] = 37, ["inverseText"] = 30, ["inactive"] = 90, ["subtle"] = 90,
            ["suggestion"] = 34, ["remember"] = 34, ["background"] = 36, ["success"] = 32,
            ["error"] = 31, ["warning"] = 33, ["diffAdded"] = 32, ["diffRemoved"] = 31,
            ["diffAddedWord"] = 92, ["diffRemovedWord"] = 91, ["professionalBlue"] = 94,
            ["clawd_body"] = 91, ["fastMode"] = 31, ["effortUltra"] = 35,
        });

    public string Foreground(string token, bool trueColor)
    {
        if (trueColor && _rgb.TryGetValue(token, out var rgb))
        {
            return $"\x1b[38;2;{rgb.R};{rgb.G};{rgb.B}m";
        }

        return _ansi.TryGetValue(token, out var code) ? $"\x1b[{code}m" : "";
    }

    public string Background(string token, bool trueColor)
    {
        if (trueColor && _rgb.TryGetValue(token, out var rgb))
        {
            return $"\x1b[48;2;{rgb.R};{rgb.G};{rgb.B}m";
        }

        return _ansi.TryGetValue(token, out var code) ? $"\x1b[{code + 10}m" : "";
    }
}

/// <summary>
/// ANSI styling for the REPL. Everything renders through one instance so a
/// non-interactive run (or NO_COLOR) can switch every code off at once, and so
/// the tests can assert on plain text.
/// </summary>
internal sealed partial class Ansi
{
    public static readonly Ansi Plain = new(enabled: false, trueColor: false);

    public Ansi(bool enabled, bool trueColor, Theme? theme = null)
    {
        Enabled = enabled;
        TrueColor = trueColor;
        Theme = theme ?? Theme.Dark;
    }

    public bool Enabled { get; }
    public bool TrueColor { get; }
    public Theme Theme { get; }

    public const string Reset = "\x1b[0m";

    public string Color(string token, string text) =>
        !Enabled || text.Length == 0 ? text : Theme.Foreground(token, TrueColor) + text + Reset;

    public string Dim(string text) => !Enabled || text.Length == 0 ? text : $"\x1b[2m{text}\x1b[22m";
    public string Bold(string text) => !Enabled || text.Length == 0 ? text : $"\x1b[1m{text}\x1b[22m";
    public string Italic(string text) => !Enabled || text.Length == 0 ? text : $"\x1b[3m{text}\x1b[23m";
    public string Underline(string text) => !Enabled || text.Length == 0 ? text : $"\x1b[4m{text}\x1b[24m";
    public string Inverse(string text) => !Enabled || text.Length == 0 ? text : $"\x1b[7m{text}\x1b[27m";
    public string Strike(string text) => !Enabled || text.Length == 0 ? text : $"\x1b[9m{text}\x1b[29m";

    public string Background(string token, string text) =>
        !Enabled || text.Length == 0 ? text : Theme.Background(token, TrueColor) + text + Reset;

    /// <summary>Colour plus dim, the reference's <c>dimColor</c> on a coloured Text.</summary>
    public string DimColor(string token, string text) => Dim(Color(token, text));

    /// <summary>A terminal hyperlink (OSC 8), falling back to the text alone.</summary>
    public string Link(string url, string text) =>
        !Enabled ? text : $"\x1b]8;;{url}\x1b\\{text}\x1b]8;;\x1b\\";

    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")]
    private static partial Regex Sequences();

    /// <summary>The text with every escape sequence removed.</summary>
    public static string Strip(string text) => text.Contains('\x1b') ? Sequences().Replace(text, "") : text;
}

/// <summary>
/// Display-width arithmetic for a terminal: East Asian wide characters take
/// two cells, combining marks and zero-width joiners none, escape sequences
/// none. Wrapping keeps escape sequences with the text they style.
/// </summary>
internal static class TextWidth
{
    public static int Of(string text)
    {
        int width = 0;
        var stripped = Ansi.Strip(text);
        var enumerator = StringInfo.GetTextElementEnumerator(stripped);
        while (enumerator.MoveNext())
        {
            width += ElementWidth((string)enumerator.Current);
        }

        return width;
    }

    private static int ElementWidth(string element)
    {
        int cp = char.ConvertToUtf32(element, 0);
        if (cp < 0x20 || cp == 0x7f)
        {
            return 0;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(cp);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsWide(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) || cp == 0x2329 || cp == 0x232A ||
        (cp >= 0x2E80 && cp <= 0xA4CF && cp != 0x303F) ||
        (cp >= 0xAC00 && cp <= 0xD7A3) || (cp >= 0xF900 && cp <= 0xFAFF) ||
        (cp >= 0xFE30 && cp <= 0xFE4F) || (cp >= 0xFF00 && cp <= 0xFF60) ||
        (cp >= 0xFFE0 && cp <= 0xFFE6) || (cp >= 0x1F300 && cp <= 0x1F64F) ||
        (cp >= 0x1F900 && cp <= 0x1F9FF) || (cp >= 0x20000 && cp <= 0x3FFFD);

    /// <summary>Truncates to a display width, keeping escape sequences intact.</summary>
    public static string Truncate(string text, int width, string ellipsis = "")
    {
        if (Of(text) <= width)
        {
            return text;
        }

        int budget = Math.Max(0, width - Of(ellipsis));
        var builder = new StringBuilder();
        int used = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\x1b')
            {
                int end = EscapeEnd(text, i);
                builder.Append(text, i, end - i);
                i = end;
                continue;
            }

            int len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            var element = text.Substring(i, len);
            int w = ElementWidth(element);
            if (used + w > budget)
            {
                break;
            }

            builder.Append(element);
            used += w;
            i += len;
        }

        return builder + ellipsis + (text.Contains('\x1b') ? Ansi.Reset : "");
    }

    /// <summary>Hard-wraps one logical line into rows no wider than the width; escape sequences ride along.</summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        if (width <= 0)
        {
            return [text];
        }

        var rows = new List<string>();
        var current = new StringBuilder();
        var word = new StringBuilder();
        int currentWidth = 0, wordWidth = 0;
        int i = 0;

        void FlushWord()
        {
            if (word.Length == 0)
            {
                return;
            }

            if (currentWidth + wordWidth > width && currentWidth > 0)
            {
                rows.Add(current.ToString());
                current.Clear();
                currentWidth = 0;
            }

            if (wordWidth > width)
            {
                // A single token wider than the row: hard-split it.
                foreach (var chunk in HardSplit(word.ToString(), width - currentWidth, width))
                {
                    if (currentWidth + Of(chunk) > width && currentWidth > 0)
                    {
                        rows.Add(current.ToString());
                        current.Clear();
                        currentWidth = 0;
                    }

                    current.Append(chunk);
                    currentWidth += Of(chunk);
                }
            }
            else
            {
                current.Append(word);
                currentWidth += wordWidth;
            }

            word.Clear();
            wordWidth = 0;
        }

        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\x1b')
            {
                int end = EscapeEnd(text, i);
                word.Append(text, i, end - i);
                i = end;
                continue;
            }

            if (c == ' ')
            {
                FlushWord();
                if (currentWidth + 1 > width)
                {
                    rows.Add(current.ToString());
                    current.Clear();
                    currentWidth = 0;
                }
                else
                {
                    current.Append(' ');
                    currentWidth++;
                }

                i++;
                continue;
            }

            int len = char.IsHighSurrogate(c) && i + 1 < text.Length ? 2 : 1;
            var element = text.Substring(i, len);
            word.Append(element);
            wordWidth += ElementWidth(element);
            i += len;
        }

        FlushWord();
        rows.Add(current.ToString());
        return rows;
    }

    private static IEnumerable<string> HardSplit(string token, int firstWidth, int width)
    {
        var chunk = new StringBuilder();
        int used = 0;
        int limit = Math.Max(1, firstWidth);
        int i = 0;
        while (i < token.Length)
        {
            if (token[i] == '\x1b')
            {
                int end = EscapeEnd(token, i);
                chunk.Append(token, i, end - i);
                i = end;
                continue;
            }

            int len = char.IsHighSurrogate(token[i]) && i + 1 < token.Length ? 2 : 1;
            var element = token.Substring(i, len);
            int w = ElementWidth(element);
            if (used + w > limit && chunk.Length > 0)
            {
                yield return chunk.ToString();
                chunk.Clear();
                used = 0;
                limit = width;
            }

            chunk.Append(element);
            used += w;
            i += len;
        }

        if (chunk.Length > 0)
        {
            yield return chunk.ToString();
        }
    }

    private static int EscapeEnd(string text, int start)
    {
        int i = start + 1;
        if (i < text.Length && text[i] == '[')
        {
            i++;
            while (i < text.Length && !(text[i] >= '@' && text[i] <= '~')) i++;
            return Math.Min(text.Length, i + 1);
        }

        if (i < text.Length && text[i] == ']')
        {
            int bel = text.IndexOf('\x07', i);
            int st = text.IndexOf("\x1b\\", i, StringComparison.Ordinal);
            int end = bel < 0 ? st : st < 0 ? bel : Math.Min(bel, st);
            return end < 0 ? text.Length : end + (end == st ? 2 : 1);
        }

        return Math.Min(text.Length, i + 1);
    }

    public static string PadRight(string text, int width)
    {
        int w = Of(text);
        return w >= width ? text : text + new string(' ', width - w);
    }
}
