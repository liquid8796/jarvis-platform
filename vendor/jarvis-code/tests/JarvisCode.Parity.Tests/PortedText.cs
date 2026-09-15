using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Where the repository sits, resolved from this file's own compile-time path.
/// The parity tests read the port's sources, and a test binary can be run from
/// anywhere — the build directory says nothing about where the sources are.
/// </summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Src { get; } = Path.Combine(Root, "src");

    /// <summary>This test project's own directory, where its data files are kept.</summary>
    public static string ParityTests { get; } = Path.Combine(Root, "tests", "JarvisCode.Parity.Tests");

    /// <summary>Reads a source file named relative to <see cref="Src"/>.</summary>
    public static string ReadSource(string relative) =>
        File.ReadAllText(Path.Combine(Src, relative.Replace('/', Path.DirectorySeparatorChar)));

    public static bool SourceExists(string relative) =>
        File.Exists(Path.Combine(Src, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRoot([CallerFilePath] string thisFile = "")
    {
        var directory = Path.GetDirectoryName(thisFile);
        while (directory is not null && !File.Exists(Path.Combine(directory, "JarvisCode.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException(
            $"no JarvisCode.slnx above {thisFile}; the parity tests read the port's sources and " +
            "cannot locate the repository.");
    }
}

/// <summary>One piece of a string literal: either fixed text or an interpolation hole.</summary>
internal readonly record struct TextSegment(string Text, bool IsHole);

/// <summary>
/// A string as the *program* sees it: the literal tokens a C# expression
/// concatenates, joined back together, with interpolation holes marked.
/// </summary>
/// <remarks>
/// Joining matters more than it looks. This port wraps its long prompts across
/// several concatenated literals, so a per-literal reading of the source yields
/// half-sentences that appear nowhere in the reference — which would read as
/// drift when the text is in fact identical.
/// </remarks>
internal sealed record PortedString(IReadOnlyList<TextSegment> Segments, int Line, bool Multiline = false)
{
    /// <summary>The fixed runs between holes — the parts a reference must contain verbatim.</summary>
    public IEnumerable<string> FixedRuns()
    {
        var run = new StringBuilder();
        foreach (var segment in Segments)
        {
            if (segment.IsHole)
            {
                if (run.Length > 0)
                {
                    yield return run.ToString();
                    run.Clear();
                }
            }
            else
            {
                run.Append(segment.Text);
            }
        }

        if (run.Length > 0)
        {
            yield return run.ToString();
        }
    }

    /// <summary>The whole text when nothing is interpolated, else null.</summary>
    public string? Constant =>
        Segments.Any(static s => s.IsHole)
            ? null
            : string.Concat(Segments.Select(static s => s.Text));
}

/// <summary>
/// Reads the string literals out of C# and XAML sources.
///
/// A parser rather than a regular expression, because everything that makes
/// this measurement honest is exactly what a regex gets wrong: comments that
/// quote the reference, verbatim and raw literals, interpolation holes, and
/// concatenation chains that split one sentence across four literals.
/// </summary>
internal static class PortedText
{
    /// <summary>Every string expression in a C# source, concatenations joined.</summary>
    public static IReadOnlyList<PortedString> CSharpStrings(string source, bool preserveLineEndings = false)
    {
        var results = new List<PortedString>();
        var segments = new List<TextSegment>();
        int startLine = 1;
        int line = 1;
        int i = 0;

        bool multiline = false;
        void Flush()
        {
            if (segments.Count > 0)
            {
                results.Add(new PortedString([.. segments], startLine, multiline));
                segments.Clear();
                multiline = false;
            }
        }

        while (i < source.Length)
        {
            char c = source[i];

            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            // Comments: skipped, so a reference sentence quoted in a comment is
            // not mistaken for text the program actually sends.
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n')
                    {
                        line++;
                    }

                    i++;
                }

                i = Math.Min(source.Length, i + 2);
                continue;
            }

            // A character literal: skipped whole, so '"' does not open a string.
            if (c == '\'')
            {
                i++;
                while (i < source.Length && source[i] != '\'')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }

                i++;
                continue;
            }

            var prefix = ReadPrefix(source, i, out int literalStart);
            if (prefix is null)
            {
                // Anything that is not whitespace, a comment or a '+' ends a
                // concatenation chain.
                if (!char.IsWhiteSpace(c) && c != '+')
                {
                    Flush();
                }

                i++;
                continue;
            }

            if (segments.Count == 0)
            {
                startLine = line;
            }

            int before = line;
            var read = ReadLiteral(source, literalStart, prefix.Value, preserveLineEndings, ref line);
            if (read is null)
            {
                // An unterminated literal means the parse went wrong; stop
                // reading this file rather than reporting nonsense.
                Flush();
                break;
            }

            segments.AddRange(read.Value.Segments);
            multiline |= read.Value.Multiline;
            i = read.Value.End;
            _ = before;

            // Look ahead: a '+' between two literals continues one string.
            int lookahead = SkipTrivia(source, i, ref line);
            if (lookahead < source.Length && source[lookahead] == '+')
            {
                int after = SkipTrivia(source, lookahead + 1, ref line);
                if (ReadPrefix(source, after, out _) is not null)
                {
                    i = after;
                    continue;
                }
            }

            Flush();
        }

        Flush();
        return results;
    }

    /// <summary>The user-visible text of a XAML source: attributes and inline runs.</summary>
    public static IReadOnlyList<string> XamlStrings(string source)
    {
        var results = new List<string>();
        foreach (var name in new[] { "Text", "Content", "Header", "ToolTip", "Title", "PlaceholderText" })
        {
            int i = 0;
            var needle = name + "=\"";
            while ((i = source.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                // Only when the attribute name stands alone: "HeaderText=" is not "Header=".
                bool standalone = i == 0 || !char.IsLetterOrDigit(source[i - 1]);
                int start = i + needle.Length;
                int end = source.IndexOf('"', start);
                i = start;
                if (!standalone || end < 0)
                {
                    continue;
                }

                var value = source[start..end];
                // Bindings and resource lookups are markup, not text.
                if (value.Length > 0 && value[0] != '{')
                {
                    results.Add(DecodeXml(value));
                }
            }
        }

        return results;
    }

    private static string DecodeXml(string value) => value
        .Replace("&amp;", "&", StringComparison.Ordinal)
        .Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal)
        .Replace("&quot;", "\"", StringComparison.Ordinal)
        .Replace("&#10;", "\n", StringComparison.Ordinal)
        .Replace("&apos;", "'", StringComparison.Ordinal);

    private enum LiteralKind
    {
        Regular,
        Verbatim,
        Raw,
    }

    private readonly record struct LiteralPrefix(LiteralKind Kind, int InterpolationDollars);

    /// <summary>
    /// Recognizes what kind of literal starts here, if any: <c>"</c>, <c>@"</c>,
    /// <c>$"</c>, <c>$@"</c>, <c>"""</c>, <c>$$"""</c> and their spellings.
    /// </summary>
    private static LiteralPrefix? ReadPrefix(string source, int index, out int literalStart)
    {
        literalStart = index;
        int dollars = 0;
        bool verbatim = false;
        int i = index;

        while (i < source.Length && (source[i] == '$' || source[i] == '@'))
        {
            if (source[i] == '$')
            {
                dollars++;
            }
            else
            {
                verbatim = true;
            }

            i++;
        }

        if (i >= source.Length || source[i] != '"')
        {
            return null;
        }

        // A prefix must not be the tail of an identifier (e.g. `name@"x"` cannot occur,
        // but `x$"..."` cannot either — only whitespace, punctuation or nothing precedes).
        if (index > 0 && (char.IsLetterOrDigit(source[index - 1]) || source[index - 1] == '_'))
        {
            return null;
        }

        literalStart = i;
        if (!verbatim && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
        {
            return new LiteralPrefix(LiteralKind.Raw, dollars);
        }

        return new LiteralPrefix(verbatim ? LiteralKind.Verbatim : LiteralKind.Regular, dollars);
    }

    private readonly record struct LiteralRead(IReadOnlyList<TextSegment> Segments, int End, bool Multiline = false);

    private static LiteralRead? ReadLiteral(
        string source, int start, LiteralPrefix prefix, bool preserveLineEndings, ref int line)
        => prefix.Kind switch
        {
            LiteralKind.Raw => ReadRaw(source, start, prefix, preserveLineEndings, ref line),
            LiteralKind.Verbatim => ReadVerbatim(source, start, prefix, ref line),
            _ => ReadRegular(source, start, prefix, ref line),
        };

    private static LiteralRead? ReadRegular(string source, int start, LiteralPrefix prefix, ref int line)
    {
        var segments = new List<TextSegment>();
        var text = new StringBuilder();
        int i = start + 1;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n')
            {
                return null; // A regular literal cannot span lines: the parse is off.
            }

            if (c == '\\' && i + 1 < source.Length)
            {
                var (decoded, width) = Unescape(source, i);
                text.Append(decoded);
                i += width;
                continue;
            }

            if (c == '"')
            {
                Close(segments, text);
                return new LiteralRead(segments, i + 1);
            }

            if (prefix.InterpolationDollars > 0 && IsHoleStart(source, i, prefix.InterpolationDollars))
            {
                i = SkipHole(source, i, prefix.InterpolationDollars, segments, text, ref line);
                continue;
            }

            text.Append(c);
            i++;
        }

        return null;
    }

    private static LiteralRead? ReadVerbatim(string source, int start, LiteralPrefix prefix, ref int line)
    {
        var segments = new List<TextSegment>();
        var text = new StringBuilder();
        bool spansLines = false;
        int i = start + 1;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '"')
            {
                if (i + 1 < source.Length && source[i + 1] == '"')
                {
                    text.Append('"');
                    i += 2;
                    continue;
                }

                Close(segments, text);
                return new LiteralRead(segments, i + 1);
            }

            if (c == '\n')
            {
                line++;
            }

            if (prefix.InterpolationDollars > 0 && IsHoleStart(source, i, prefix.InterpolationDollars))
            {
                i = SkipHole(source, i, prefix.InterpolationDollars, segments, text, ref line);
                continue;
            }

            text.Append(c);
            i++;
        }

        return null;
    }

    private static LiteralRead? ReadRaw(
        string source, int start, LiteralPrefix prefix, bool preserveLineEndings, ref int line)
    {
        int quotes = 0;
        while (start + quotes < source.Length && source[start + quotes] == '"')
        {
            quotes++;
        }

        var terminator = new string('"', quotes);
        int bodyStart = start + quotes;
        int end = source.IndexOf(terminator, bodyStart, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var body = source[bodyStart..end];
        line += body.Count(static c => c == '\n');

        // A multi-line raw literal drops the first and last newline and the
        // indentation carried by the closing delimiter, exactly as the compiler does.
        var multiline = body.Contains('\n');
        if (multiline)
        {
            // Text comparisons do not care about carriage returns, so they are
            // normalized away by default; the line-ending check is the one caller
            // that asks to keep them, because they are what it looks for.
            var lines = body.Split('\n');
            var closing = lines[^1];
            var indent = closing.Length - closing.TrimStart().Length;
            var kept = lines[1..^1]
                .Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())
                .Select(l => preserveLineEndings ? l : l.TrimEnd('\r'))
                .ToArray();
            // Joined with "\n" either way: the carriage returns that were in the
            // file are still on their own lines, and none is invented for a file
            // that did not have them.
            body = string.Join("\n", kept);
        }

        var segments = new List<TextSegment>();
        var text = new StringBuilder();
        for (int i = 0; i < body.Length;)
        {
            if (prefix.InterpolationDollars > 0 && IsHoleStart(body, i, prefix.InterpolationDollars))
            {
                i = SkipHole(body, i, prefix.InterpolationDollars, segments, text, ref line);
                continue;
            }

            text.Append(body[i]);
            i++;
        }

        Close(segments, text);
        return new LiteralRead(segments, end + quotes, multiline);
    }

    private static void Close(List<TextSegment> segments, StringBuilder text)
    {
        if (text.Length > 0)
        {
            segments.Add(new TextSegment(text.ToString(), IsHole: false));
            text.Clear();
        }
    }

    /// <summary>
    /// True when a hole opens here. An interpolated literal marks holes with as
    /// many braces as it has dollars, and doubling that many is the escape.
    /// </summary>
    private static bool IsHoleStart(string source, int index, int dollars)
    {
        for (int i = 0; i < dollars; i++)
        {
            if (index + i >= source.Length || source[index + i] != '{')
            {
                return false;
            }
        }

        return true;
    }

    private static int SkipHole(
        string source, int index, int dollars, List<TextSegment> segments, StringBuilder text, ref int line)
    {
        // `{{` in a $-string (or `{{{{` in a $$-string) is a literal brace.
        if (IsHoleStart(source, index + dollars, dollars))
        {
            text.Append('{');
            return index + (dollars * 2);
        }

        Close(segments, text);
        segments.Add(new TextSegment("", IsHole: true));

        int i = index + dollars;
        int depth = 1;
        while (i < source.Length && depth > 0)
        {
            if (source[i] == '\n')
            {
                line++;
            }
            else if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    // Closing braces come in the same number as the dollars.
                    return i + dollars;
                }
            }
            else if (source[i] == '"')
            {
                // A nested literal inside the hole; skip it so its quotes do not
                // terminate the outer one.
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }
            }

            i++;
        }

        return i;
    }

    private static (string Text, int Width) Unescape(string source, int index)
    {
        char c = source[index + 1];
        switch (c)
        {
            case 'n': return ("\n", 2);
            case 't': return ("\t", 2);
            case 'r': return ("\r", 2);
            case '0': return ("\0", 2);
            case 'a': return ("\a", 2);
            case 'b': return ("\b", 2);
            case 'f': return ("\f", 2);
            case 'v': return ("\v", 2);
            case '\\': return ("\\", 2);
            case '"': return ("\"", 2);
            case '\'': return ("'", 2);
            case 'u' when index + 5 < source.Length:
                return int.TryParse(
                    source.AsSpan(index + 2, 4),
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out var code)
                    ? (((char)code).ToString(), 6)
                    : ("", 2);
            case 'U' when index + 9 < source.Length:
                return int.TryParse(
                    source.AsSpan(index + 2, 8),
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out var wide)
                    ? (char.ConvertFromUtf32(wide), 10)
                    : ("", 2);
            default:
                return (c.ToString(), 2);
        }
    }

    private static int SkipTrivia(string source, int index, ref int line)
    {
        while (index < source.Length)
        {
            char c = source[index];
            if (c == '\n')
            {
                line++;
                index++;
            }
            else if (char.IsWhiteSpace(c))
            {
                index++;
            }
            else if (c == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    index++;
                }
            }
            else if (c == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/'))
                {
                    if (source[index] == '\n')
                    {
                        line++;
                    }

                    index++;
                }

                index += 2;
            }
            else
            {
                break;
            }
        }

        return index;
    }
}
