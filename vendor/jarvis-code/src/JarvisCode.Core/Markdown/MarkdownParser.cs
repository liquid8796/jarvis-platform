using System.Text;

namespace JarvisCode.Core.Markdown;

/// <summary>
/// CommonMark-with-GFM parser for assistant output: headings (ATX and setext),
/// paragraphs, fenced code, nested quotes and lists, pipe tables with
/// alignment, rules, images, autolinks, and the inline styles
/// bold/italic/code/strikethrough/links/math. Unterminated constructs are
/// treated as their opened form so partially streamed responses render
/// sensibly. Anything unrecognized degrades to a plain paragraph — never an
/// exception.
/// </summary>
public static class MarkdownParser
{
    /// <summary>Nesting cap; assistant output never legitimately goes this deep.</summary>
    private const int MaxDepth = 12;

    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
        => Parse(markdown, MarkdownOptions.Default);

    public static IReadOnlyList<MarkdownBlock> Parse(string markdown, MarkdownOptions options)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return ParseBlocks(lines, lines.Length, options, depth: 0);
    }

    private static List<MarkdownBlock> ParseBlocks(
        string[] lines, int to, MarkdownOptions options, int depth)
    {
        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();
        int i = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            blocks.Add(new ParagraphBlock(ParseInlines(string.Join('\n', paragraph), options)));
            paragraph.Clear();
        }

        while (i < to)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(ReadFence(lines, ref i, to, line.Length - trimmed.Length));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                i++;
                continue;
            }

            // A setext underline closes the paragraph above it as a heading. It is
            // tested before the rule, which is what "---" means with nothing above.
            if (paragraph.Count > 0 && SetextLevel(trimmed) is { } setext)
            {
                blocks.Add(new HeadingBlock(setext, ParseInlines(string.Join('\n', paragraph), options)));
                paragraph.Clear();
                i++;
                continue;
            }

            if (TryParseMathBlock(lines, ref i, to) is { } math)
            {
                FlushParagraph();
                blocks.Add(math);
                continue;
            }

            if (TryParseHeading(trimmed, options) is { } heading)
            {
                FlushParagraph();
                blocks.Add(heading);
                i++;
                continue;
            }

            if (IsHorizontalRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(new HorizontalRuleBlock());
                i++;
                continue;
            }

            if (options.GithubFlavored && TryReadFootnoteLabel(trimmed) is { } footnote)
            {
                FlushParagraph();
                blocks.Add(ReadFootnoteDefinition(lines, ref i, to, footnote, options));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(ReadQuote(lines, ref i, to, options, depth));
                continue;
            }

            if (TryParseListMarker(line) is not null)
            {
                FlushParagraph();
                blocks.Add(ReadList(lines, ref i, to, options, depth));
                continue;
            }

            if (options.GithubFlavored && trimmed.StartsWith('|') &&
                i + 1 < to && TableAlignments(lines[i + 1]) is { } alignments)
            {
                FlushParagraph();
                blocks.Add(ReadTable(lines, ref i, to, alignments, options));
                continue;
            }

            paragraph.Add(trimmed);
            i++;
        }

        FlushParagraph();
        return blocks;
    }

    // ---- fenced code ----

    private static CodeFenceBlock ReadFence(string[] lines, ref int i, int to, int indent)
    {
        var trimmed = lines[i].TrimStart();
        var fence = trimmed[..3];
        var language = trimmed[3..].Trim();
        var code = new StringBuilder();
        i++;
        while (i < to && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
        {
            code.Append(Dedent(lines[i], indent)).Append('\n');
            i++;
        }

        i++; // Skip the closing fence (or run past the end for an open fence).
        return new CodeFenceBlock(language.Length == 0 ? null : language, code.ToString().TrimEnd('\n'));
    }

    /// <summary>Removes up to <paramref name="indent"/> leading spaces, keeping deeper indentation.</summary>
    private static string Dedent(string line, int indent)
    {
        int strip = 0;
        while (strip < indent && strip < line.Length && line[strip] is ' ' or '\t')
            strip++;
        return line[strip..];
    }

    // ---- quotes ----

    private static QuoteBlock ReadQuote(string[] lines, ref int i, int to, MarkdownOptions options, int depth)
    {
        var inner = new List<string>();
        bool paragraphOpen = false;
        while (i < to)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('>'))
            {
                var content = trimmed[1..];
                inner.Add(content.StartsWith(' ') ? content[1..] : content);
                paragraphOpen = content.Trim().Length > 0;
                i++;
                continue;
            }

            // Lazy continuation: a bare line keeps the quote's paragraph going,
            // but only while that paragraph is open and the line starts nothing new.
            if (!paragraphOpen || string.IsNullOrWhiteSpace(lines[i]) || StartsNewBlock(trimmed))
                break;

            inner.Add(trimmed);
            i++;
        }

        var blocks = ParseNested(inner, options, depth);
        var first = blocks.OfType<ParagraphBlock>().FirstOrDefault();
        return new QuoteBlock(first?.Inlines ?? [], blocks);
    }

    private static List<MarkdownBlock> ParseNested(List<string> lines, MarkdownOptions options, int depth)
        => depth >= MaxDepth
            ? [new ParagraphBlock(ParseInlines(string.Join('\n', lines), options))]
            : ParseBlocks([.. lines], lines.Count, options, depth + 1);

    private static bool StartsNewBlock(string trimmed)
        => trimmed.StartsWith("```", StringComparison.Ordinal)
           || trimmed.StartsWith("~~~", StringComparison.Ordinal)
           || trimmed.StartsWith('#')
           || trimmed.StartsWith('|')
           || trimmed.StartsWith('>')
           || IsHorizontalRule(trimmed)
           || TryParseListMarker(trimmed) is not null;

    /// <summary>The label of a "[^label]:" definition line, or null.</summary>
    private static string? TryReadFootnoteLabel(string trimmed)
    {
        if (!trimmed.StartsWith("[^", StringComparison.Ordinal))
            return null;
        int close = trimmed.IndexOf("]:", 2, StringComparison.Ordinal);
        if (close < 3)
            return null;
        var label = trimmed[2..close];
        return label.Length > 0 && !label.Any(char.IsWhiteSpace) ? label : null;
    }

    private static FootnoteDefinitionBlock ReadFootnoteDefinition(
        string[] lines, ref int i, int to, string label, MarkdownOptions options)
    {
        var first = lines[i].TrimStart();
        var body = new List<string> { first[(first.IndexOf("]:", StringComparison.Ordinal) + 2)..].TrimStart() };
        i++;

        // A definition keeps the indented lines under it, as CommonMark's
        // footnote extension does; a flush-left line ends it.
        while (i < to)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                if (i + 1 >= to || lines[i + 1].Length - lines[i + 1].TrimStart().Length < 2)
                    break;
                body.Add("");
                i++;
                continue;
            }

            if (lines[i].Length - lines[i].TrimStart().Length < 2)
                break;
            body.Add(lines[i].TrimStart());
            i++;
        }

        return new FootnoteDefinitionBlock(label, ParseInlines(string.Join('\n', body), options));
    }

    // ---- lists ----

    private readonly record struct ListMarker(int Indent, string Text, bool Ordered, int Number);

    private static ListMarker? TryParseListMarker(string line)
    {
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ')
            indent++;
        var trimmed = line[indent..];
        if (trimmed.Length == 0 || IsHorizontalRule(trimmed))
            return null;

        if (trimmed[0] is '-' or '*' or '+' && (trimmed.Length == 1 || trimmed[1] == ' '))
            return new ListMarker(indent, trimmed[..1], Ordered: false, Number: 0);

        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
            digits++;
        if (digits is > 0 and <= 9 && digits < trimmed.Length && trimmed[digits] is '.' or ')' &&
            (digits + 1 == trimmed.Length || trimmed[digits + 1] == ' '))
        {
            return new ListMarker(indent, trimmed[..(digits + 1)], Ordered: true, int.Parse(trimmed[..digits]));
        }

        return null;
    }

    private static ListBlock ReadList(string[] lines, ref int i, int to, MarkdownOptions options, int depth)
    {
        var first = TryParseListMarker(lines[i])!.Value;
        int baseIndent = first.Indent;
        var items = new List<ListItem>();
        var body = new List<string>();
        var current = first;
        int contentIndent = baseIndent + current.Text.Length + 1;

        void CloseItem()
        {
            items.Add(BuildItem(current, body, options, depth));
            body.Clear();
        }

        body.Add(lines[i][(first.Indent + first.Text.Length)..].TrimStart());
        i++;

        while (i < to)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                // A blank line stays inside the list only while indented content
                // or another item follows it; otherwise the list ends here.
                int look = i;
                while (look < to && string.IsNullOrWhiteSpace(lines[look]))
                    look++;
                if (look >= to)
                    break;
                var upcoming = TryParseListMarker(lines[look]);
                int upcomingIndent = lines[look].Length - lines[look].TrimStart().Length;
                if ((upcoming is null || upcoming.Value.Indent < baseIndent) && upcomingIndent < contentIndent)
                    break;
                body.Add("");
                i++;
                continue;
            }

            var marker = TryParseListMarker(line);
            int indent = line.Length - line.TrimStart().Length;

            if (marker is { } m && m.Indent < contentIndent)
            {
                // A change of marker type starts a new list, as CommonMark says:
                // bullets and numbers are different lists even when adjacent.
                if (m.Indent < baseIndent || m.Ordered != first.Ordered)
                    break;
                CloseItem();
                current = m;
                contentIndent = m.Indent + m.Text.Length + 1;
                body.Add(line[(m.Indent + m.Text.Length)..].TrimStart());
                i++;
                continue;
            }

            if (indent >= contentIndent || marker is not null)
            {
                body.Add(Dedent(line, contentIndent));
                i++;
                continue;
            }

            // Lazy continuation of the item's own paragraph.
            if (StartsNewBlock(line.TrimStart()))
                break;
            body.Add(line.TrimStart());
            i++;
        }

        CloseItem();
        return new ListBlock(first.Ordered, items, first.Ordered ? first.Number : 1);
    }

    private static ListItem BuildItem(
        ListMarker marker, List<string> body, MarkdownOptions options, int depth)
    {
        bool isTask = false, isChecked = false;
        if (body.Count > 0 && !marker.Ordered && options.GithubFlavored)
        {
            var (state, rest) = TryReadTask(body[0]);
            if (state is not null)
            {
                isTask = true;
                isChecked = state.Value;
                body[0] = rest;
            }
        }

        var blocks = ParseNested(body, options, depth);
        IReadOnlyList<InlineRun> inlines = [];
        IReadOnlyList<MarkdownBlock>? children = null;
        if (blocks.Count > 0 && blocks[0] is ParagraphBlock lead)
        {
            inlines = lead.Inlines;
            if (blocks.Count > 1)
                children = blocks.GetRange(1, blocks.Count - 1);
        }
        else if (blocks.Count > 0)
        {
            children = blocks;
        }

        return new ListItem(marker.Indent / 2, marker.Text, inlines, isTask, isChecked, children);
    }

    /// <summary>Reads a "[ ]"/"[x]" task prefix, returning the state and the remaining text.</summary>
    private static (bool? Checked, string Remainder) TryReadTask(string content)
    {
        if (content.Length >= 3 && content[0] == '[' && content[2] == ']' &&
            content[1] is ' ' or 'x' or 'X' &&
            (content.Length == 3 || content[3] == ' '))
        {
            return (content[1] is 'x' or 'X', content.Length > 4 ? content[4..] : "");
        }

        return (null, content);
    }

    // ---- tables ----

    private static TableBlock ReadTable(
        string[] lines, ref int i, int to, IReadOnlyList<ColumnAlignment> alignments, MarkdownOptions options)
    {
        var header = ParseTableRow(lines[i], options);
        i += 2;
        var rows = new List<IReadOnlyList<IReadOnlyList<InlineRun>>>();
        while (i < to && lines[i].TrimStart().StartsWith('|'))
        {
            rows.Add(ParseTableRow(lines[i], options));
            i++;
        }

        return new TableBlock(header, rows, alignments);
    }

    /// <summary>
    /// Reads a delimiter row's alignments, or null when the line is not one.
    /// Every cell must be dashes with optional end colons, as GFM requires.
    /// </summary>
    private static IReadOnlyList<ColumnAlignment>? TableAlignments(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|'))
            return null;

        var result = new List<ColumnAlignment>();
        foreach (var raw in SplitRow(trimmed))
        {
            var cell = raw.Trim();
            if (cell.Length == 0)
                return null;
            bool left = cell[0] == ':';
            bool right = cell.Length > 1 && cell[^1] == ':';
            var dashes = cell[(left ? 1 : 0)..(cell.Length - (right ? 1 : 0))];
            if (dashes.Length == 0 || dashes.Any(static c => c != '-'))
                return null;
            result.Add((left, right) switch
            {
                (true, true) => ColumnAlignment.Center,
                (true, false) => ColumnAlignment.Left,
                (false, true) => ColumnAlignment.Right,
                _ => ColumnAlignment.None,
            });
        }

        return result.Count > 0 ? result : null;
    }

    private static IReadOnlyList<IReadOnlyList<InlineRun>> ParseTableRow(string line, MarkdownOptions options)
        => [.. SplitRow(line.Trim()).Select(cell => ParseInlines(cell.Trim(), options))];

    /// <summary>
    /// Splits a pipe-table row on unescaped pipes outside a code span, then drops
    /// the leading and trailing empty cells the outer pipes make.
    /// </summary>
    private static List<string> SplitRow(string row)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool inCode = false;
        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];
            if (c == '\\' && i + 1 < row.Length && row[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (c == '`')
            {
                inCode = !inCode;
                cell.Append(c);
            }
            else if (c == '|' && !inCode)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        if (cells.Count > 0 && cells[0].Trim().Length == 0)
            cells.RemoveAt(0);
        if (cells.Count > 0 && cells[^1].Trim().Length == 0)
            cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    // ---- leaf blocks ----

    private static MathBlock? TryParseMathBlock(string[] lines, ref int i, int to)
    {
        var trimmed = lines[i].Trim();
        string? open = trimmed.StartsWith("$$", StringComparison.Ordinal) ? "$$"
            : trimmed.StartsWith("\\[", StringComparison.Ordinal) ? "\\[" : null;
        if (open is null)
            return null;

        var close = open == "$$" ? "$$" : "\\]";
        var rest = trimmed[2..];
        var closeAt = rest.IndexOf(close, StringComparison.Ordinal);
        if (closeAt >= 0 && rest[(closeAt + 2)..].Trim().Length == 0)
        {
            var latex = rest[..closeAt].Trim();
            if (latex.Length == 0)
                return null;
            i++;
            return new MathBlock(latex);
        }

        var body = new StringBuilder(rest.Trim());
        for (int j = i + 1; j < to; j++)
        {
            var lineTrimmed = lines[j].Trim();
            if (lineTrimmed.EndsWith(close, StringComparison.Ordinal))
            {
                var head = lineTrimmed[..^2].Trim();
                if (head.Length > 0)
                {
                    if (body.Length > 0)
                        body.Append('\n');
                    body.Append(head);
                }

                i = j + 1;
                return body.Length == 0 ? null : new MathBlock(body.ToString());
            }

            if (body.Length > 0)
                body.Append('\n');
            body.Append(lineTrimmed);
        }

        return null;
    }

    private static HeadingBlock? TryParseHeading(string trimmed, MarkdownOptions options)
    {
        int level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
            level++;
        if (level is 0 or > 6 || level >= trimmed.Length || trimmed[level] != ' ')
            return null;

        // An ATX closing sequence names the same heading: "## Title ##" is "Title".
        var text = trimmed[(level + 1)..].Trim();
        int trail = text.Length;
        while (trail > 0 && text[trail - 1] == '#')
            trail--;
        if (trail < text.Length && (trail == 0 || text[trail - 1] == ' '))
            text = text[..trail].TrimEnd();
        return new HeadingBlock(level, ParseInlines(text, options));
    }

    /// <summary>A setext underline: "===" makes an h1 of the paragraph above, "---" an h2.</summary>
    private static int? SetextLevel(string trimmed)
    {
        char c = trimmed[0];
        if (c is not ('=' or '-'))
            return null;
        return trimmed.All(ch => ch == c) ? c == '=' ? 1 : 2 : null;
    }

    private static bool IsHorizontalRule(string trimmed)
    {
        if (trimmed.Length < 3)
            return false;
        char c = trimmed[0];
        if (c is not ('-' or '*' or '_'))
            return false;
        int count = 0;
        foreach (var ch in trimmed)
        {
            if (ch == c)
                count++;
            else if (ch is not (' ' or '\t'))
                return false;
        }

        return count >= 3;
    }

    // ---- inlines ----

    /// <summary>Parses inline markdown into a flat list of styled runs.</summary>
    public static IReadOnlyList<InlineRun> ParseInlines(string text)
        => ParseInlines(text, MarkdownOptions.Default);

    public static IReadOnlyList<InlineRun> ParseInlines(string text, MarkdownOptions options)
    {
        var runs = new List<InlineRun>();
        ParseInto(runs, text, InlineStyle.None, null, options, depth: 0);
        return runs;
    }

    private static void ParseInto(
        List<InlineRun> runs, string text, InlineStyle style, string? link, MarkdownOptions options, int depth)
    {
        var literal = new StringBuilder();

        void Flush()
        {
            if (literal.Length == 0)
                return;
            runs.Add(new InlineRun(literal.ToString(), style, link));
            literal.Clear();
        }

        void Break()
        {
            // Trailing spaces belong to the break, not to the text before it.
            while (literal.Length > 0 && literal[^1] == ' ')
                literal.Length--;
            Flush();
            runs.Add(new InlineRun("", style, link, HardBreak: true));
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                Break();
                i += 2;
                continue;
            }

            // A backslash escape makes the next punctuation literal. "\(" and "\["
            // are math delimiters and are left for the branches below.
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] is not ('(' or '[') || link is not null) &&
                char.IsPunctuation(text[i + 1]) | char.IsSymbol(text[i + 1]))
            {
                literal.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '\n')
            {
                if (options.SoftBreaks || (literal.Length >= 2 && literal[^1] == ' ' && literal[^2] == ' '))
                {
                    Break();
                }
                else
                {
                    while (literal.Length > 0 && literal[^1] == ' ')
                        literal.Length--;
                    literal.Append(' ');
                }

                i++;
                continue;
            }

            if (c == '<' && IsBreakTag(text, i, out int brLength))
            {
                Break();
                i += brLength;
                continue;
            }

            if (c == '<' && options.KbdTag && TryReadKbd(text, i, out var keys, out int afterKbd))
            {
                Flush();
                runs.Add(new InlineRun(keys, style, link, Kbd: true));
                i = afterKbd;
                continue;
            }

            if (c == '`')
            {
                int open = RunLength(text, i, '`');
                int close = FindClosingRun(text, i + open, '`', open);
                if (close >= 0)
                {
                    Flush();
                    runs.Add(new InlineRun(TrimCodeSpan(text[(i + open)..close]), style | InlineStyle.Code, link));
                    i = close + open;
                    continue;
                }
                if (options.Streaming)
                {
                    Flush();
                    runs.Add(new InlineRun(TrimCodeSpan(text[(i + open)..]), style | InlineStyle.Code, link, Incomplete: true));
                    return;
                }
            }
            else if (c == '\\' && i + 1 < text.Length && text[i + 1] == '(')
            {
                int close = text.IndexOf("\\)", i + 2, StringComparison.Ordinal);
                if (close > i)
                {
                    Flush();
                    runs.Add(new InlineRun(text[(i + 2)..close].Trim(), style, link, Math: true));
                    i = close + 2;
                    continue;
                }
            }
            else if (c == '$' && i + 1 < text.Length && text[i + 1] == '$')
            {
                int close = text.IndexOf("$$", i + 2, StringComparison.Ordinal);
                if (close > i)
                {
                    Flush();
                    runs.Add(new InlineRun(text[(i + 2)..close].Trim(), style, link, Math: true));
                    i = close + 2;
                    continue;
                }
            }
            else if (c == '[' && options.GithubFlavored && link is null &&
                     TryReadFootnoteReference(text, i, out var note, out int afterNote))
            {
                Flush();
                runs.Add(new InlineRun(note, style, link, FootnoteLabel: note));
                i = afterNote;
                continue;
            }
            else if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' &&
                     TryReadLink(text, i + 1, out var alt, out var src, out int afterImage))
            {
                Flush();
                runs.Add(new InlineRun(alt, style, link, ImageUrl: src));
                i = afterImage;
                continue;
            }
            else if (c == '[' && link is null &&
                     TryReadLink(text, i, out var label, out var url, out int afterLink))
            {
                Flush();
                if (label.Length == 0)
                    runs.Add(new InlineRun(url, style, url));
                else
                    Recurse(runs, label, style, url, options, depth);
                i = afterLink;
                continue;
            }
            else if (c == '<' && link is null && TryReadAngleAutolink(text, i, out var angle, out int afterAngle))
            {
                Flush();
                runs.Add(new InlineRun(angle, style, angle));
                i = afterAngle;
                continue;
            }
            else if (options.Streaming && c == '[' && link is null && TryReadPartialLink(text, i, out var partialLabel))
            {
                Flush();
                runs.Add(new InlineRun(partialLabel, style, Incomplete: true));
                return;
            }
            else if (c is '*' or '_' &&
                     TryReadEmphasis(text, i, out var inner, out var added, out int afterEmphasis))
            {
                Flush();
                Recurse(runs, inner, style | added, link, options, depth);
                i = afterEmphasis;
                continue;
            }
            else if (options.Streaming && c is '*' or '_' && TryReadPartialEmphasis(text, i, out var partial, out var partialStyle))
            {
                Flush();
                var first = runs.Count;
                Recurse(runs, partial, style | partialStyle, link, options, depth);
                for (var index = first; index < runs.Count; index++) runs[index] = runs[index] with { Incomplete = true };
                return;
            }
            else if (c == '~')
            {
                int open = RunLength(text, i, '~');
                int want = open >= 2 ? 2 : 1;
                if (options.GithubFlavored && (open >= 2 || options.SingleTildeStrikethrough))
                {
                    int close = FindClosingRun(text, i + want, '~', want);
                    if (close >= 0)
                    {
                        Flush();
                        Recurse(runs, text[(i + want)..close], style | InlineStyle.Strikethrough, link, options, depth);
                        i = close + want;
                        continue;
                    }
                    if (options.Streaming)
                    {
                        Flush();
                        runs.Add(new InlineRun(text[(i + want)..], style | InlineStyle.Strikethrough, link, Incomplete: true));
                        return;
                    }
                }
            }
            else if (link is null && options.GithubFlavored && IsAutolinkStart(text, i) &&
                     TryReadBareAutolink(text, i, out var bare, out var href, out int afterBare))
            {
                Flush();
                runs.Add(new InlineRun(bare, style, href));
                i = afterBare;
                continue;
            }

            literal.Append(c);
            i++;
        }

        Flush();
    }

    private static void Recurse(
        List<InlineRun> runs, string text, InlineStyle style, string? link, MarkdownOptions options, int depth)
    {
        if (depth >= MaxDepth)
        {
            runs.Add(new InlineRun(text, style, link));
            return;
        }

        ParseInto(runs, text, style, link, options, depth + 1);
    }

    private static bool TryReadPartialLink(string text, int start, out string label)
    {
        label = "";
        var closing = text.IndexOf(']', start + 1);
        if (closing < 0) { label = text[(start + 1)..]; return true; }
        if (closing + 1 < text.Length && text[closing + 1] == '(' && text.IndexOf(')', closing + 2) < 0)
        { label = text[(start + 1)..closing]; return true; }
        return false;
    }

    private static bool TryReadPartialEmphasis(string text, int start, out string inner, out InlineStyle style)
    {
        inner = ""; style = InlineStyle.None;
        var delimiter = text[start];
        var length = Math.Min(RunLength(text, start, delimiter), 3);
        var content = start + length;
        if (content >= text.Length || char.IsWhiteSpace(text[content]) ||
            delimiter == '_' && start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) return false;
        inner = text[content..];
        style = length switch { 1 => InlineStyle.Italic, 2 => InlineStyle.Bold, _ => InlineStyle.Bold | InlineStyle.Italic };
        return true;
    }

    /// <summary>Matches &lt;br&gt;, &lt;br/&gt; and &lt;br /&gt;, the break spelling the reference splits cells on.</summary>
    private static bool IsBreakTag(string text, int index, out int length)
    {
        length = 0;
        if (!text.AsSpan(index).StartsWith("<br", StringComparison.OrdinalIgnoreCase))
            return false;
        int j = index + 3;
        while (j < text.Length && text[j] == ' ')
            j++;
        if (j < text.Length && text[j] == '/')
            j++;
        if (j >= text.Length || text[j] != '>')
            return false;
        length = j + 1 - index;
        return true;
    }

    /// <summary>
    /// Reads a literal &lt;kbd&gt; tag. The reference's scanner looks no further
    /// than <see cref="MarkdownOptions.MaxKbdLength"/> characters for the close,
    /// so a runaway tag stays text instead of swallowing the rest of the line.
    /// </summary>
    private static bool TryReadKbd(string text, int i, out string keys, out int after)
    {
        keys = "";
        after = i;
        const string open = "<kbd>";
        const string close = "</kbd>";
        if (!text.AsSpan(i).StartsWith(open, StringComparison.OrdinalIgnoreCase))
            return false;

        int from = i + open.Length;
        int window = Math.Min(text.Length, from + MarkdownOptions.MaxKbdLength + close.Length);
        int end = text.IndexOf(close, from, window - from, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return false;

        keys = text[from..end];
        after = end + close.Length;
        return true;
    }

    /// <summary>Reads a "[^label]" footnote reference.</summary>
    private static bool TryReadFootnoteReference(string text, int i, out string label, out int after)
    {
        label = "";
        after = i;
        if (i + 2 >= text.Length || text[i + 1] != '^')
            return false;
        int close = text.IndexOf(']', i + 2);
        if (close < 0)
            return false;
        var body = text[(i + 2)..close];
        if (body.Length == 0 || body.Any(char.IsWhiteSpace))
            return false;

        // "[^a]: text" is a definition, which the block reader owns.
        if (close + 1 < text.Length && text[close + 1] == ':')
            return false;

        label = body;
        after = close + 1;
        return true;
    }

    private static int RunLength(string text, int index, char c)
    {
        int n = 0;
        while (index + n < text.Length && text[index + n] == c)
            n++;
        return n;
    }

    /// <summary>Finds a closing delimiter run of exactly <paramref name="want"/> characters.</summary>
    private static int FindClosingRun(string text, int from, char c, int want)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] != c)
                continue;
            int n = RunLength(text, i, c);
            if (n == want)
                return i;
            i += n - 1;
        }

        return -1;
    }

    /// <summary>CommonMark strips one space from each end of a code span that has both.</summary>
    private static string TrimCodeSpan(string content)
        => content.Length > 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim().Length > 0
            ? content[1..^1]
            : content;

    /// <summary>
    /// Emphasis with CommonMark's flanking rules — enough of them that
    /// <c>snake_case_name</c> stays literal while <c>***both***</c> nests.
    /// </summary>
    private static bool TryReadEmphasis(string text, int i, out string inner, out InlineStyle style, out int next)
    {
        inner = "";
        style = InlineStyle.None;
        next = i;
        char c = text[i];
        int open = Math.Min(RunLength(text, i, c), 3);
        int contentStart = i + open;
        if (contentStart >= text.Length || char.IsWhiteSpace(text[contentStart]))
            return false;

        // "_" may not open inside a word; "*" may.
        if (c == '_' && i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'))
            return false;

        for (int j = contentStart; j < text.Length; j++)
        {
            if (text[j] != c)
                continue;
            int run = RunLength(text, j, c);
            if (run < open || char.IsWhiteSpace(text[j - 1]))
            {
                j += run - 1;
                continue;
            }

            int after = j + open;
            if (c == '_' && after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
            {
                j += run - 1;
                continue;
            }

            inner = text[contentStart..j];
            style = open switch
            {
                1 => InlineStyle.Italic,
                2 => InlineStyle.Bold,
                _ => InlineStyle.Bold | InlineStyle.Italic,
            };
            next = after;
            return true;
        }

        return false;
    }

    /// <summary>Reads "[label](url)" or "[label](url \"title\")" starting at the bracket.</summary>
    private static bool TryReadLink(string text, int i, out string label, out string url, out int after)
    {
        label = "";
        url = "";
        after = i;
        int depth = 0;
        int close = -1;
        for (int j = i; j < text.Length; j++)
        {
            if (text[j] == '\\')
            {
                j++;
                continue;
            }

            if (text[j] == '[')
            {
                depth++;
            }
            else if (text[j] == ']' && --depth == 0)
            {
                close = j;
                break;
            }
        }

        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
            return false;

        int paren = 1;
        int end = -1;
        for (int j = close + 2; j < text.Length; j++)
        {
            if (text[j] == '\\')
            {
                j++;
                continue;
            }

            if (text[j] == '(')
            {
                paren++;
            }
            else if (text[j] == ')' && --paren == 0)
            {
                end = j;
                break;
            }
        }

        if (end < 0)
            return false;

        label = text[(i + 1)..close];
        var target = text[(close + 2)..end].Trim();
        int quote = target.IndexOf(" \"", StringComparison.Ordinal);
        if (quote > 0)
            target = target[..quote].Trim();
        if (target.StartsWith('<') && target.EndsWith('>'))
            target = target[1..^1];
        url = target;
        after = end + 1;
        return url.Length > 0 || label.Length > 0;
    }

    private static bool TryReadAngleAutolink(string text, int i, out string url, out int after)
    {
        url = "";
        after = i;
        int close = text.IndexOf('>', i + 1);
        if (close < 0)
            return false;
        var body = text[(i + 1)..close];
        if (body.Length == 0 || body.Any(char.IsWhiteSpace) ||
            !(body.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              body.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        url = body;
        after = close + 1;
        return true;
    }

    private static bool IsAutolinkStart(string text, int i)
    {
        if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] is '/' or '@' or '.'))
            return false;
        var span = text.AsSpan(i);
        return span.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || span.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || span.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// GFM's literal autolink: runs to whitespace, then gives back trailing
    /// punctuation and any closing paren that was never opened inside the link.
    /// </summary>
    private static bool TryReadBareAutolink(string text, int i, out string display, out string href, out int after)
    {
        int end = i;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '<')
            end++;

        while (end > i)
        {
            char last = text[end - 1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '"' or '\'' or '*' or '_' or '~')
            {
                end--;
                continue;
            }

            if (last == ')')
            {
                var slice = text[i..end];
                if (slice.Count(static ch => ch == ')') > slice.Count(static ch => ch == '('))
                {
                    end--;
                    continue;
                }
            }

            break;
        }

        display = text[i..end];
        href = display.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + display : display;
        after = end;

        // "www." alone is not a link; the shortest real one is "www.a.bc".
        return display.Length > 7 && display.Contains('.');
    }
}
