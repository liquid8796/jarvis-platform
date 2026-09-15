using System.Text;
using System.Text.RegularExpressions;

namespace JarvisCode.Core.Agent;

/// <summary>
/// Decides which parts of an instruction file an <c>@</c>-import may be read
/// out of. The reference lexes the file with marked (gfm off) and scans only
/// the tokens that are neither <c>code</c> nor <c>codespan</c>, skipping an
/// <c>html</c> token outright unless it opens with a comment — in which case the
/// comments are removed and whatever is left is scanned.
///
/// So four kinds of span are not scannable: a fenced block, an indented block,
/// an HTML block, and an inline code span. This walker finds exactly those and
/// blanks them, using marked's own block rules (2.1.251, its <c>Re</c>,
/// <c>Ae</c>, <c>Pe</c>, <c>Q</c> and the codespan rule) so the boundaries are
/// the reference's rather than an approximation of them.
/// </summary>
internal static class InstructionMarkdown
{
    /// <summary>marked's <c>Re</c>: a run of lines indented four spaces or a tab.</summary>
    private static readonly Regex IndentedCode = new(
        @"\G((?: {4}| {0,3}\t)[^\n]+(?:\n(?:[ \t]*(?:\n|$))*)?)+", RegexOptions.Compiled);

    /// <summary>marked's <c>Ae</c>: a fenced block, closed by its own fence or by the end.</summary>
    private static readonly Regex Fences = new(
        @"\G {0,3}(`{3,}(?=[^`\n]*(?:\n|$))|~{3,})([^\n]*)(?:\n|$)(?:|([\s\S]*?)(?:\n|$))(?: {0,3}\1[~`]* *(?=\n|$)|$)",
        RegexOptions.Compiled);

    /// <summary>marked's <c>Q</c>: an HTML comment, closed or running to the end.</summary>
    private static readonly Regex HtmlComment = new(
        @"<!--(?:-?>|[\s\S]*?(?:-->|$))", RegexOptions.Compiled);

    /// <summary>The block-level tag names marked's <c>_</c> lists.</summary>
    private const string BlockTags =
        "address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|" +
        "dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|" +
        "iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|" +
        "section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul";

    private const string HtmlAttribute =
        @" +[a-zA-Z:_][\w.:-]*(?: *= *""[^""\n]*""| *= *'[^'\n]*'| *= *[^\s""'=<>`]+)?";

    /// <summary>marked's <c>Pe</c>, with its two placeholders resolved.</summary>
    private static readonly Regex HtmlBlock = new(
        @"\G {0,3}(?:<(script|pre|style|textarea)[\s>][\s\S]*?(?:</\1>[^\n]*\n+|$)" +
        @"|<!--(?:-?>|[\s\S]*?(?:-->|$))[^\n]*(\n+|$)" +
        @"|<\?[\s\S]*?(?:\?>\n*|$)" +
        @"|<![A-Z][\s\S]*?(?:>\n*|$)" +
        @"|<!\[CDATA\[[\s\S]*?(?:\]\]>\n*|$)" +
        @"|</?(?:" + BlockTags + @")(?: +|\n|/?>)[\s\S]*?(?:(?:\n[ \t]*)+\n|$)" +
        @"|<(?!script|pre|style|textarea)([a-z][\w-]*)(?:" + HtmlAttribute + @")*? */?>(?=[ \t]*(?:\n|$))[\s\S]*?(?:(?:\n[ \t]*)+\n|$)" +
        @"|</(?!script|pre|style|textarea)[a-z][\w-]*\s*>(?=[ \t]*(?:\n|$))[\s\S]*?(?:(?:\n[ \t]*)+\n|$))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>marked's newline rule: one or more blank lines.</summary>
    private static readonly Regex Blank = new(@"\G(?:[ \t]*(?:\n|$))+", RegexOptions.Compiled);

    /// <summary>marked's codespan inline rule.</summary>
    private static readonly Regex CodeSpan = new(
        @"(`+)([^`]|[^`][\s\S]*?[^`])\1(?!`)", RegexOptions.Compiled);

    /// <summary>A blockquote line, whose content marked re-lexes on its own.</summary>
    private static readonly Regex QuoteLine = new(@"^ {0,3}> ?", RegexOptions.Compiled);

    /// <summary>marked's <c>ae</c> bullet, anchored as a list-item opener.</summary>
    private static readonly Regex ListMarker = new(
        @"^( {0,3})((?:[*+-]|\d{1,9}[.)]))([ \t]+|$)", RegexOptions.Compiled);

    /// <summary>
    /// An inline tag, which marked emits as its own <c>html</c> token rather
    /// than as text. Removing them leaves the text tokens the reference
    /// actually scans; without that, "@style.md&lt;/span&gt;" reads as one path.
    /// </summary>
    private static readonly Regex InlineTag = new(
        @"<!--[\s\S]*?(?:-->|$)|</?[a-zA-Z][\w:-]*(?:\s[^<>]*?)?/?>|<\?[\s\S]*?\?>|<![a-zA-Z][\s\S]*?>",
        RegexOptions.Compiled);

    /// <summary>How deep this walker follows lists and quotes before giving up.</summary>
    private const int MaxContainerDepth = 8;

    /// <summary>
    /// The text an import may be read out of: the document with every
    /// unscannable span replaced by nothing. Line structure is not preserved —
    /// the caller scans the result, it does not map positions back.
    /// </summary>
    public static string Scannable(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var scanned = new StringBuilder(normalized.Length);
        ScanBlocks(normalized, scanned, depth: 0);
        var text = CodeSpan.Replace(scanned.ToString(), "\n");
        return InlineTag.Replace(text, " ");
    }

    private static void ScanBlocks(string text, StringBuilder output, int depth)
    {
        int pos = 0;
        bool afterParagraph = false;
        while (pos < text.Length)
        {
            if (Blank.Match(text, pos) is { Success: true, Length: > 0 } blank)
            {
                output.Append('\n');
                pos += blank.Length;
                afterParagraph = false;
                continue;
            }

            // An indented block cannot interrupt a paragraph: marked appends
            // those lines to the paragraph instead, which leaves them scannable.
            if (!afterParagraph && IndentedCode.Match(text, pos) is { Success: true, Length: > 0 } indented)
            {
                output.Append('\n');
                pos += indented.Length;
                continue;
            }

            if (Fences.Match(text, pos) is { Success: true, Length: > 0 } fence)
            {
                output.Append('\n');
                pos += fence.Length;
                afterParagraph = false;
                continue;
            }

            if (HtmlBlock.Match(text, pos) is { Success: true, Length: > 0 } html)
            {
                // The reference scans an HTML token only when it opens with a
                // comment, and then only what survives removing the comments.
                var raw = html.Value;
                if (raw.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
                {
                    var remainder = HtmlComment.Replace(raw, string.Empty);
                    if (remainder.Trim().Length > 0)
                        output.Append(remainder).Append('\n');
                }

                output.Append('\n');
                pos += html.Length;
                afterParagraph = false;
                continue;
            }

            if (depth < MaxContainerDepth && QuoteLine.IsMatch(LineAt(text, pos)))
            {
                int consumed = TakeQuote(text, pos, out var quoted);
                ScanBlocks(quoted, output, depth + 1);
                pos += consumed;
                afterParagraph = false;
                continue;
            }

            if (depth < MaxContainerDepth && ListMarker.IsMatch(LineAt(text, pos)))
            {
                int consumed = TakeList(text, pos, out var items);
                ScanBlocks(items, output, depth + 1);
                pos += consumed;
                afterParagraph = false;
                continue;
            }

            int lineEnd = text.IndexOf('\n', pos);
            if (lineEnd < 0)
                lineEnd = text.Length;
            output.Append(text, pos, lineEnd - pos).Append('\n');
            pos = lineEnd < text.Length ? lineEnd + 1 : text.Length;
            afterParagraph = true;
        }
    }

    /// <summary>
    /// Takes the blockquote starting at <paramref name="start"/> and returns its
    /// content with the "&gt;" markers removed, which is what marked re-lexes.
    /// Lazy continuation lines — non-blank lines that carry no marker — belong to
    /// the quote as well.
    /// </summary>
    private static int TakeQuote(string text, int start, out string content)
    {
        var body = new StringBuilder();
        int pos = start;
        while (pos < text.Length)
        {
            int lineEnd = text.IndexOf('\n', pos);
            if (lineEnd < 0)
                lineEnd = text.Length;
            var line = text[pos..lineEnd];
            var marker = QuoteLine.Match(line);
            if (marker.Success)
                body.Append(line, marker.Length, line.Length - marker.Length).Append('\n');
            else if (line.Trim().Length == 0)
                break;
            else
                body.Append(line).Append('\n');
            pos = lineEnd < text.Length ? lineEnd + 1 : text.Length;
        }

        content = body.ToString();
        return pos - start;
    }

    /// <summary>
    /// Takes the list starting at <paramref name="start"/> and returns its items
    /// dedented to the column their content sits at. Dedenting is what makes a
    /// fenced or indented block inside an item read as one: marked lexes item
    /// content on its own, where that block is no longer indented by the list.
    /// </summary>
    private static int TakeList(string text, int start, out string content)
    {
        var opener = ListMarker.Match(LineAt(text, start));
        int contentIndent = opener.Groups[1].Length + opener.Groups[2].Length +
                            Math.Max(1, opener.Groups[3].Length);
        var body = new StringBuilder();
        int pos = start;
        while (pos < text.Length)
        {
            int lineEnd = text.IndexOf('\n', pos);
            if (lineEnd < 0)
                lineEnd = text.Length;
            var line = text[pos..lineEnd];

            if (line.Trim().Length == 0)
            {
                // A blank line ends the list only when what follows is neither
                // item content nor another item.
                int next = lineEnd < text.Length ? lineEnd + 1 : text.Length;
                if (!ContinuesList(text, next, contentIndent))
                    break;
                body.Append('\n');
                pos = next;
                continue;
            }

            var marker = ListMarker.Match(line);
            if (marker.Success && marker.Groups[1].Length < contentIndent)
            {
                // Keep the item's own text; the marker itself carries none.
                body.Append(line, marker.Length, line.Length - marker.Length).Append('\n');
            }
            else if (Indent(line) >= contentIndent)
            {
                body.Append(line, contentIndent, line.Length - contentIndent).Append('\n');
            }
            else
            {
                break;
            }

            pos = lineEnd < text.Length ? lineEnd + 1 : text.Length;
        }

        content = body.ToString();
        return pos - start;
    }

    private static bool ContinuesList(string text, int pos, int contentIndent)
    {
        while (pos < text.Length)
        {
            int lineEnd = text.IndexOf('\n', pos);
            if (lineEnd < 0)
                lineEnd = text.Length;
            var line = text[pos..lineEnd];
            if (line.Trim().Length > 0)
            {
                var marker = ListMarker.Match(line);
                return (marker.Success && marker.Groups[1].Length < contentIndent) || Indent(line) >= contentIndent;
            }

            pos = lineEnd < text.Length ? lineEnd + 1 : text.Length;
        }

        return false;
    }

    /// <summary>The one line at <paramref name="pos"/>, without copying the rest of the document.</summary>
    private static string LineAt(string text, int pos)
    {
        int end = text.IndexOf('\n', pos);
        return end < 0 ? text[pos..] : text[pos..end];
    }

    /// <summary>Leading spaces, counting a tab as four as marked's rules do.</summary>
    private static int Indent(string line)
    {
        int indent = 0;
        foreach (var c in line)
        {
            if (c == ' ')
                indent++;
            else if (c == '\t')
                indent += 4;
            else
                break;
        }

        return indent;
    }
}
