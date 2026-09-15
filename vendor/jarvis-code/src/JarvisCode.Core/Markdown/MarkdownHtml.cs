using System.Text;

namespace JarvisCode.Core.Markdown;

/// <summary>
/// Serialises a parsed message back to HTML, which is the second flavour the
/// reference puts on the clipboard beside the plain text (its copy action asks
/// for "plain_html"). It exists so a message pasted into a rich editor keeps its
/// headings, lists, links and code rather than arriving as one run of text.
///
/// <para>
/// This is deliberately plain HTML with no classes or inline styles: what the
/// receiving editor should keep is the structure, and a colour or a font size
/// taken from this app's theme would follow the text into a document that has
/// its own.
/// </para>
/// </summary>
public static class MarkdownHtml
{
    /// <summary>The message as an HTML fragment - no html or body wrapper.</summary>
    public static string Render(IReadOnlyList<MarkdownBlock> blocks)
    {
        var html = new StringBuilder();
        foreach (var block in blocks)
        {
            Block(html, block);
        }

        return html.ToString();
    }

    /// <summary>The markdown source as an HTML fragment.</summary>
    public static string RenderMarkdown(string markdown, MarkdownOptions? options = null) =>
        Render(MarkdownParser.Parse(markdown, options ?? MarkdownOptions.Conversation));

    private static void Block(StringBuilder html, MarkdownBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var level = Math.Clamp(heading.Level, 1, 6);
                html.Append("<h").Append(level).Append('>');
                Inlines(html, heading.Inlines);
                html.Append("</h").Append(level).Append('>');
                break;

            case ParagraphBlock paragraph:
                html.Append("<p>");
                Inlines(html, paragraph.Inlines);
                html.Append("</p>");
                break;

            case CodeFenceBlock fence:
                // The language rides the class the way every markdown-to-HTML
                // renderer writes it, so an editor that highlights can.
                html.Append("<pre><code");
                if (!string.IsNullOrWhiteSpace(fence.Language))
                {
                    html.Append(" class=\"language-").Append(Escape(fence.Language)).Append('"');
                }

                html.Append('>').Append(Escape(fence.Code)).Append("</code></pre>");
                break;

            case QuoteBlock quote:
                html.Append("<blockquote>");
                if (quote.Blocks is { Count: > 0 } nested)
                {
                    foreach (var child in nested)
                    {
                        Block(html, child);
                    }
                }
                else
                {
                    html.Append("<p>");
                    Inlines(html, quote.Inlines);
                    html.Append("</p>");
                }

                html.Append("</blockquote>");
                break;

            case ListBlock list:
                var tag = list.Ordered ? "ol" : "ul";
                html.Append('<').Append(tag);
                if (list.Ordered && list.Start != 1)
                {
                    html.Append(" start=\"").Append(list.Start).Append('"');
                }

                html.Append('>');
                foreach (var item in list.Items)
                {
                    html.Append("<li>");

                    // A task item keeps its box, disabled as GFM renders it.
                    if (item.IsTask)
                    {
                        html.Append("<input type=\"checkbox\" disabled");
                        if (item.IsChecked)
                        {
                            html.Append(" checked");
                        }

                        html.Append("> ");
                    }

                    Inlines(html, item.Inlines);
                    foreach (var child in item.ChildBlocks ?? [])
                    {
                        Block(html, child);
                    }

                    html.Append("</li>");
                }

                html.Append("</").Append(tag).Append('>');
                break;

            case TableBlock table:
                html.Append("<table><thead><tr>");
                for (var i = 0; i < table.Header.Count; i++)
                {
                    Cell(html, "th", table.Header[i], Alignment(table, i));
                }

                html.Append("</tr></thead><tbody>");
                foreach (var row in table.Rows)
                {
                    html.Append("<tr>");
                    for (var i = 0; i < row.Count; i++)
                    {
                        Cell(html, "td", row[i], Alignment(table, i));
                    }

                    html.Append("</tr>");
                }

                html.Append("</tbody></table>");
                break;

            case FootnoteDefinitionBlock footnote:
                html.Append("<p>[").Append(Escape(footnote.Label)).Append("] ");
                Inlines(html, footnote.Inlines);
                html.Append("</p>");
                break;

            case HorizontalRuleBlock:
                html.Append("<hr>");
                break;

            // Math has no HTML of its own here: WpfMath draws it on screen and
            // the receiving editor has no renderer, so the LaTeX travels as the
            // code it is rather than as a picture that would not paste.
            case MathBlock math:
                html.Append("<pre><code>").Append(Escape(math.Latex)).Append("</code></pre>");
                break;
        }
    }

    private static ColumnAlignment Alignment(TableBlock table, int column) =>
        table.Alignments is { } alignments && column < alignments.Count
            ? alignments[column]
            : ColumnAlignment.Left;

    private static void Cell(
        StringBuilder html, string tag, IReadOnlyList<InlineRun> inlines, ColumnAlignment alignment)
    {
        html.Append('<').Append(tag);
        if (alignment != ColumnAlignment.Left)
        {
            html.Append(" align=\"")
                .Append(alignment == ColumnAlignment.Center ? "center" : "right")
                .Append('"');
        }

        html.Append('>');
        Inlines(html, inlines);
        html.Append("</").Append(tag).Append('>');
    }

    private static void Inlines(StringBuilder html, IReadOnlyList<InlineRun> inlines)
    {
        foreach (var run in inlines)
        {
            if (run.HardBreak)
            {
                html.Append("<br>");
                continue;
            }

            if (run.ImageUrl is { Length: > 0 } image)
            {
                html.Append("<img src=\"").Append(Escape(image))
                    .Append("\" alt=\"").Append(Escape(run.Text)).Append("\">");
                continue;
            }

            var close = new Stack<string>();
            if (run.LinkUrl is { Length: > 0 } link)
            {
                html.Append("<a href=\"").Append(Escape(link)).Append("\">");
                close.Push("</a>");
            }

            // Code wins the innermost slot, as it does in the renderers: an
            // emphasised code span is emphasis around code, never the reverse.
            foreach (var (style, open, shut) in Wrappers)
            {
                if (run.Style.HasFlag(style))
                {
                    html.Append(open);
                    close.Push(shut);
                }
            }

            if (run.Kbd)
            {
                html.Append("<kbd>");
                close.Push("</kbd>");
            }

            // Inline math travels as its source too, for the reason above.
            html.Append(Escape(run.Text));
            while (close.Count > 0)
            {
                html.Append(close.Pop());
            }
        }
    }

    private static readonly (InlineStyle Style, string Open, string Close)[] Wrappers =
    [
        (InlineStyle.Bold, "<strong>", "</strong>"),
        (InlineStyle.Italic, "<em>", "</em>"),
        (InlineStyle.Strikethrough, "<del>", "</del>"),
        (InlineStyle.Code, "<code>", "</code>"),
    ];

    private static string Escape(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}
