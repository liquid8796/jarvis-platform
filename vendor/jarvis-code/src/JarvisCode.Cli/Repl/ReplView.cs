using JarvisCode.Cli.Repl.Input;
using JarvisCode.Cli.Repl.Render;

namespace JarvisCode.Cli.Repl;

/// <summary>
/// Everything the live region needs to draw itself. Kept as one record so the
/// renderer below is a pure function of it and the tests can pose any state
/// without a terminal.
/// </summary>
internal sealed record ReplViewState
{
    public required string Text { get; init; }
    public required int Offset { get; init; }
    public ComposerMode Mode { get; init; } = ComposerMode.Prompt;
    public string? VimIndicator { get; init; }
    public Autocomplete? Completion { get; init; }
    public IReadOnlyList<string> Queued { get; init; } = [];
    public string? StatusRow { get; init; }
    /// <summary>Replaceable browser text, confined to the live region and never appended to scrollback.</summary>
    public string? AnswerPreview { get; init; }
    public int PreviewMaxRows { get; init; } = 6;
    public string? SuggestedPrompt { get; init; }
    public IReadOnlyList<string> SessionStrip { get; init; } = [];
    public string Footer { get; init; } = "";
    public IReadOnlyList<string>? Overlay { get; init; }
    public IReadOnlyList<string>? Dialog { get; init; }

    /// <summary>The exit or clear notice a first ctrl+c / esc press puts under the box.</summary>
    public string? PendingNotice { get; init; }

    /// <summary>Set while ctrl+r is searching history.</summary>
    public HistorySearch? Search { get; init; }

    /// <summary>Shown while a placeholder could be expanded by pasting again.</summary>
    public bool ShowExpandPasteHint { get; init; }
}

/// <summary>
/// The live region: the reference's rounded prompt box with the caret inside
/// it, the completion popup under it, the queued-message notice, and the
/// footer. A dialog replaces the box entirely, which is what the reference does
/// while a permission prompt or a plan approval is up.
/// </summary>
internal static class ReplView
{
    public const string TopLeft = "╭";
    public const string TopRight = "╮";
    public const string BottomLeft = "╰";
    public const string BottomRight = "╯";
    public const string Horizontal = "─";
    public const string Vertical = "│";

    /// <summary>The reference's hint once a prompt is queued.</summary>
    public const string QueuedEditHint = "Press up to edit queued messages";

    /// <summary>The reference's hint while a collapsed paste can be expanded.</summary>
    public const string ExpandPasteHint = "paste again to expand";

    /// <summary>The prompt marker inside the box, per composer mode.</summary>
    public static string Marker(ComposerMode mode) => mode switch
    {
        ComposerMode.Bash => "!",
        ComposerMode.Memory => "#",
        _ => ">",
    };

    /// <summary>The border colour token the mode calls for.</summary>
    public static string BorderColor(ComposerMode mode) =>
        mode == ComposerMode.Bash ? "bashBorder" : "promptBorder";

    /// <summary>
    /// The live lines, plus where the caret should rest (row index into the
    /// returned lines, and its column).
    /// </summary>
    public static (IReadOnlyList<string> Lines, (int Row, int Column)? Caret) Render(
        ReplViewState state, Ansi ansi, int columns)
    {
        var lines = new List<string>();
        lines.AddRange(state.SessionStrip.Select(line => TextWidth.Truncate(line, columns, "…")));
        if (state.SuggestedPrompt is { Length: > 0 } suggestion)
            lines.Add(ansi.Dim("Tab to use: " + TextWidth.Truncate(suggestion, Math.Max(1, columns - 12), "…")));
        if (state.StatusRow is { Length: > 0 } status)
        {
            lines.Add(ansi.Color("claude", status));
            lines.Add("");
        }

        if (state.Dialog is { Count: > 0 } dialog)
        {
            lines.AddRange(dialog);
            return (lines, null);
        }

        if (state.AnswerPreview is { Length: > 0 } preview)
        {
            lines.Add(ansi.Dim("Answer preview"));
            lines.AddRange(PreviewLines(preview, columns, state.PreviewMaxRows).Select(ansi.Dim));
            lines.Add("");
        }

        if (state.Overlay is { Count: > 0 } overlay)
        {
            lines.AddRange(overlay.Select(ansi.Dim));
            lines.Add("");
        }

        if (state.Queued.Count > 0)
        {
            // The reference lists the queued prompts above the box and says
            // nothing else about them; the count sentence is the desktop
            // composer's, not this front-end's.
            foreach (var queued in state.Queued)
            {
                lines.Add(ansi.Dim("  " + TextWidth.Truncate(queued.ReplaceLineEndings(" "), columns - 4, "…")));
            }
        }

        int inner = Math.Max(10, columns - 4);
        var border = BorderColor(state.Mode);
        lines.Add(ansi.Color(border, TopLeft + Repeat(Horizontal, columns - 2) + TopRight));

        (int Row, int Column)? caret = null;
        if (state.Search is { } search)
        {
            var scope = HistoryScopes.Label(search.Scope);
            var body = $"(reverse-i-search [{scope}]) `{search.Query}': {search.Current?.Display ?? ""}";
            lines.Add(BoxRow(ansi, border, body, columns));
        }
        else
        {
            // The mode's own character becomes the box's marker, so it is not
            // repeated inside the line the way the raw text carries it.
            var marker = Marker(state.Mode) + " ";
            var (body, offset) = StripModeCharacter(state.Text, state.Offset, state.Mode);
            var rows = ComposerRows(body, marker, inner);
            LocateCaret(body, offset, marker, inner, out int caretRow, out int caretColumn);
            for (int i = 0; i < rows.Count; i++)
            {
                lines.Add(BoxRow(ansi, border, rows[i], columns));
            }

            caret = (lines.Count - rows.Count + caretRow, caretColumn + 2);
        }

        lines.Add(ansi.Color(border, BottomLeft + Repeat(Horizontal, columns - 2) + BottomRight));

        if (state.VimIndicator is { Length: > 0 } vim)
        {
            lines.Add(ansi.Dim(vim));
        }

        if (state.Completion is { Items.Count: > 0 } completion)
        {
            for (int i = 0; i < completion.Items.Count; i++)
            {
                var item = completion.Items[i];
                var pointer = i == completion.Index ? ansi.Color("suggestion", Glyphs.Pointer) : " ";
                var alias = item.Alias is { Length: > 0 } a ? $" ({a})" : "";
                var description = item.Description is { Length: > 0 } d
                    ? "  " + ansi.Dim(TextWidth.Truncate(d, Math.Max(10, columns / 2), "…"))
                    : "";
                lines.Add($"{pointer} {item.Label}{alias}{description}");
            }
        }

        var footer = new List<string>();
        if (state.PendingNotice is { Length: > 0 } notice)
        {
            footer.Add(notice);
        }
        else
        {
            if (state.ShowExpandPasteHint)
            {
                footer.Add(ExpandPasteHint);
            }

            if (state.Queued.Count > 0)
            {
                footer.Add(QueuedEditHint);
            }

            if (state.Footer is { Length: > 0 } text)
            {
                footer.Add(text);
            }
        }

        if (footer.Count > 0)
        {
            lines.Add(ansi.Dim("  " + string.Join(JarvisCode.Cli.Repl.Render.Footer.Separator, footer)));
        }

        return (lines, caret);
    }

    /// <summary>Keep the current end of a draft visible without moving the composer off screen.</summary>
    internal static IReadOnlyList<string> PreviewLines(string text, int columns, int maximumRows)
    {
        var plain = Ansi.Strip(text).ReplaceLineEndings("\n").Replace("\t", "    ", StringComparison.Ordinal);
        plain = new string(plain.Where(character => character == '\n' || !char.IsControl(character)).ToArray());
        return [.. plain.Split('\n').SelectMany(line => TextWidth.Wrap(line, Math.Max(1, columns)))
            .TakeLast(Math.Max(1, maximumRows))];
    }

    /// <summary>
    /// The text the box shows: in shell or memory mode the leading <c>!</c> or
    /// <c>#</c> is the marker rather than content, so it is taken off the line
    /// and the caret moves with it.
    /// </summary>
    internal static (string Text, int Offset) StripModeCharacter(string text, int offset, ComposerMode mode)
    {
        if (mode == ComposerMode.Prompt)
        {
            return (text, offset);
        }

        int index = text.IndexOfAny(['!', '#']);
        if (index < 0)
        {
            return (text, offset);
        }

        return (text.Remove(index, 1), offset > index ? offset - 1 : offset);
    }

    /// <summary>The composer's text as box rows, the first prefixed by the marker.</summary>
    internal static IReadOnlyList<string> ComposerRows(string text, string marker, int inner)
    {
        var rows = new List<string>();
        var indent = new string(' ', marker.Length);
        foreach (var logical in text.Split('\n'))
        {
            var wrapped = TextWidth.Wrap(logical, Math.Max(4, inner - marker.Length));
            foreach (var row in wrapped)
            {
                rows.Add((rows.Count == 0 ? marker : indent) + row);
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(marker);
        }

        return rows;
    }

    /// <summary>Where the caret lands once the text is wrapped into box rows.</summary>
    internal static void LocateCaret(string text, int offset, string marker, int inner, out int row, out int column)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int width = Math.Max(4, inner - marker.Length);
        row = 0;
        column = marker.Length;
        int consumed = 0;
        foreach (var logical in text.Split('\n'))
        {
            if (offset > consumed + logical.Length)
            {
                consumed += logical.Length + 1;
                row += Math.Max(1, TextWidth.Wrap(logical, width).Count);
                continue;
            }

            int within = offset - consumed;
            row += within / width;
            column = marker.Length + within % width;
            return;
        }
    }

    private static string BoxRow(Ansi ansi, string border, string body, int columns)
    {
        int inner = Math.Max(1, columns - 4);
        var text = TextWidth.PadRight(TextWidth.Truncate(body, inner), inner);
        return ansi.Color(border, Vertical) + " " + text + " " + ansi.Color(border, Vertical);
    }

    private static string Repeat(string text, int count) =>
        count <= 0 ? "" : string.Concat(Enumerable.Repeat(text, count));
}
