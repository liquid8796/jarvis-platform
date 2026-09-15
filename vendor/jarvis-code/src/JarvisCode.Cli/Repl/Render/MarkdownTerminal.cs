using System.Text;
using JarvisCode.Core.Markdown;

namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// Markdown as a terminal draws it. The reference lexes the answer with marked
/// and renders each token with ANSI styling; this reuses the engine's own
/// CommonMark+GFM parser (one markdown parser in the repo) in the conversation
/// dialect both reference renderers use — remark-breaks on, so a single newline
/// inside a paragraph is a line break — and paints the tokens the way a
/// terminal can.
/// </summary>
internal sealed class MarkdownTerminal(Ansi ansi, int columns)
{
    private readonly Ansi _ansi = ansi;
    private readonly int _columns = Math.Max(20, columns);

    /// <summary>The indent a quote's body takes, behind the reference's pipe.</summary>
    public const string QuotePrefix = "│ ";

    public string Render(string markdown)
    {
        var blocks = MarkdownParser.Parse(markdown, MarkdownOptions.Conversation);
        var builder = new StringBuilder();
        RenderBlocks(blocks, builder, indent: "");
        return builder.ToString().TrimEnd('\n');
    }

    private void RenderBlocks(IReadOnlyList<MarkdownBlock> blocks, StringBuilder builder, string indent)
    {
        bool first = true;
        foreach (var block in blocks)
        {
            if (!first)
            {
                builder.Append('\n');
            }

            first = false;
            RenderBlock(block, builder, indent);
        }
    }

    private void RenderBlock(MarkdownBlock block, StringBuilder builder, string indent)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AppendWrapped(builder, indent, _ansi.Bold(Inline(heading.Inlines)));
                break;

            case ParagraphBlock paragraph:
                AppendWrapped(builder, indent, Inline(paragraph.Inlines));
                break;

            case CodeFenceBlock fence:
                foreach (var line in fence.Code.TrimEnd('\n').Split('\n'))
                {
                    builder.Append(indent).Append("    ").Append(_ansi.Dim(line)).Append('\n');
                }

                break;

            case QuoteBlock quote:
                var inner = new StringBuilder();
                if (quote.Blocks is { Count: > 0 } nested)
                {
                    RenderBlocks(nested, inner, indent: "");
                }
                else
                {
                    AppendWrapped(inner, "", Inline(quote.Inlines));
                }

                foreach (var line in inner.ToString().TrimEnd('\n').Split('\n'))
                {
                    builder.Append(indent).Append(_ansi.Dim(QuotePrefix)).Append(line).Append('\n');
                }

                break;

            case ListBlock list:
                RenderList(list, builder, indent);
                break;

            case HorizontalRuleBlock:
                builder.Append(indent).Append(_ansi.Dim(new string('─', Math.Max(4, _columns - indent.Length))))
                    .Append('\n');
                break;

            case TableBlock table:
                RenderTable(table, builder, indent);
                break;

            case MathBlock math:
                AppendWrapped(builder, indent, _ansi.Dim(math.Latex));
                break;

            case FootnoteDefinitionBlock footnote:
                AppendWrapped(builder, indent, _ansi.Dim($"[{footnote.Label}] {Inline(footnote.Inlines)}"));
                break;
        }
    }

    private void RenderList(ListBlock list, StringBuilder builder, string indent)
    {
        int number = list.Start;
        foreach (var item in list.Items)
        {
            var marker = list.Ordered ? $"{number++}. " : "- ";
            if (item.IsTask)
            {
                marker += item.IsChecked ? "[x] " : "[ ] ";
            }

            var body = new StringBuilder();
            AppendWrapped(body, "", Inline(item.Inlines), _columns - indent.Length - marker.Length);
            var lines = body.ToString().TrimEnd('\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                builder.Append(indent)
                    .Append(i == 0 ? marker : new string(' ', marker.Length))
                    .Append(lines[i])
                    .Append('\n');
            }

            if (item.ChildBlocks is { Count: > 0 } children)
            {
                RenderBlocks(children, builder, indent + new string(' ', marker.Length));
            }
        }
    }

    private void RenderTable(TableBlock table, StringBuilder builder, string indent)
    {
        var header = table.Header.Select(Inline).ToList();
        var rows = table.Rows.Select(row => row.Select(Inline).ToList()).ToList();
        int count = Math.Max(header.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
        var widths = new int[count];
        for (int i = 0; i < count; i++)
        {
            widths[i] = i < header.Count ? TextWidth.Of(header[i]) : 0;
            foreach (var row in rows)
            {
                if (i < row.Count)
                {
                    widths[i] = Math.Max(widths[i], TextWidth.Of(row[i]));
                }
            }
        }

        void Line(IReadOnlyList<string> cells, bool bold)
        {
            builder.Append(indent);
            for (int i = 0; i < count; i++)
            {
                var cell = i < cells.Count ? cells[i] : "";
                var padded = TextWidth.PadRight(cell, widths[i]);
                builder.Append(bold ? _ansi.Bold(padded) : padded);
                if (i < count - 1)
                {
                    builder.Append("  ");
                }
            }

            builder.Append('\n');
        }

        Line(header, bold: true);
        builder.Append(indent)
            .Append(_ansi.Dim(string.Join("  ", widths.Select(w => new string('─', Math.Max(1, w))))))
            .Append('\n');
        foreach (var row in rows)
        {
            Line(row, bold: false);
        }
    }

    /// <summary>Inline runs as one styled string.</summary>
    public string Inline(IReadOnlyList<InlineRun> runs)
    {
        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.HardBreak)
            {
                builder.Append('\n');
                continue;
            }

            var text = run.Text;
            if (run.ImageUrl is { Length: > 0 } image)
            {
                builder.Append(_ansi.Dim($"[image: {(text.Length > 0 ? text : image)}]"));
                continue;
            }

            if (run.Style.HasFlag(InlineStyle.Code))
            {
                builder.Append(_ansi.Color("suggestion", text));
                continue;
            }

            if (run.Style.HasFlag(InlineStyle.Bold))
            {
                text = _ansi.Bold(text);
            }

            if (run.Style.HasFlag(InlineStyle.Italic))
            {
                text = _ansi.Italic(text);
            }

            if (run.Style.HasFlag(InlineStyle.Strikethrough))
            {
                text = _ansi.Strike(text);
            }

            if (run.Kbd)
            {
                text = _ansi.Inverse(text);
            }

            if (run.LinkUrl is { Length: > 0 } url)
            {
                text = _ansi.Link(url, _ansi.Underline(text));
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    private void AppendWrapped(StringBuilder builder, string indent, string text, int? width = null)
    {
        int wrapAt = Math.Max(10, (width ?? _columns - indent.Length));
        foreach (var line in text.Split('\n'))
        {
            foreach (var row in TextWidth.Wrap(line, wrapAt))
            {
                builder.Append(indent).Append(row).Append('\n');
            }
        }
    }
}
