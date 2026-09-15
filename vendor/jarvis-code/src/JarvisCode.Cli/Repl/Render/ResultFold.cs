namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// The reference's tool-result fold (CLI 2.1.257, its <c>Aun</c>/<c>LNo</c>/
/// <c>bR</c>/<c>NNo</c>): a result is shown three lines deep and the rest is
/// counted into <c>… +N lines (ctrl+o to expand)</c>. The width the lines are
/// measured at is the row width less the ten columns the connector and the
/// indent take, floored at ten.
/// </summary>
internal static class ResultFold
{
    /// <summary>The reference's <c>X1</c>: how many lines stay above the fold.</summary>
    public const int VisibleLines = 3;

    /// <summary>The reference's <c>ZNn</c>: the columns the row's own chrome costs.</summary>
    public const int ChromeColumns = 10;

    /// <summary>The width a result's lines are wrapped at inside a row of this width.</summary>
    public static int ContentWidth(int columns) => Math.Max(columns - ChromeColumns, 10);

    /// <summary>The reference's expand hint, with the chord its keybindings resolve.</summary>
    public static string ExpandHint(string chord = "ctrl+o") => $"({chord} to expand)";

    internal sealed record Folded(string AboveTheFold, int RemainingLines);

    /// <summary>
    /// The reference's <c>LNo</c>: hard-wrap to the width, then keep the first
    /// three lines — unless exactly one would be hidden, in which case the
    /// fourth is shown too rather than spending a line to hide a line.
    /// </summary>
    internal static Folded Wrap(string text, int width)
    {
        var rows = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (TextWidth.Of(line) <= width)
            {
                rows.Add(line.TrimEnd());
                continue;
            }

            int cut = 0;
            int total = TextWidth.Of(line);
            while (cut < total)
            {
                rows.Add(Slice(line, cut, cut + width).TrimEnd());
                cut += width;
            }
        }

        int hidden = rows.Count - VisibleLines;
        if (hidden == 1)
        {
            return new Folded(string.Join('\n', rows.Take(VisibleLines + 1)).TrimEnd(), 0);
        }

        return new Folded(string.Join('\n', rows.Take(VisibleLines)).TrimEnd(), Math.Max(0, hidden));
    }

    /// <summary>
    /// The reference's <c>Aun</c>: the folded body plus its counted tail. The
    /// text is first cut to <c>3 × width × 4</c> characters — past that the
    /// hidden count is estimated from the whole rather than from the cut, so a
    /// megabyte of output is not wrapped line by line to count it.
    /// </summary>
    public static string Render(string text, int columns, bool hideExpandHint = false, string chord = "ctrl+o")
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
        {
            return "";
        }

        int width = ContentWidth(columns);
        int cap = VisibleLines * width * 4;
        bool overCap = trimmed.Length > cap;
        var considered = overCap ? trimmed[..cap] : trimmed;
        var folded = Wrap(considered, width);
        int remaining = folded.RemainingLines;
        if (overCap)
        {
            int newlines = trimmed.Count(c => c == '\n');
            int estimate = Math.Max(newlines + 1, (int)Math.Ceiling(trimmed.Length / (double)width));
            remaining = Math.Max(remaining, estimate - VisibleLines);
        }

        if (remaining <= 0)
        {
            return folded.AboveTheFold;
        }

        var tail = Format.Fold(remaining) + (hideExpandHint ? "" : " " + ExpandHint(chord));
        return folded.AboveTheFold.Length == 0 ? tail : folded.AboveTheFold + "\n" + tail;
    }

    /// <summary>
    /// The reference's <c>bR</c>: whether this result has anything below the
    /// fold at all, which is what decides if the expand hint is offered.
    /// </summary>
    public static bool IsFoldable(string text, int? columns = null)
    {
        var trimmed = text.TrimEnd();
        int offset = 0;
        int newlines = 0;
        for (int i = 0; i <= VisibleLines; i++)
        {
            offset = trimmed.IndexOf('\n', offset);
            if (offset < 0)
            {
                break;
            }

            newlines++;
            offset++;
        }

        if (offset >= 0 && offset < trimmed.Length)
        {
            return true;
        }

        if (columns is not { } cols)
        {
            return false;
        }

        int width = ContentWidth(cols);
        if (trimmed.Length > VisibleLines * width * 4)
        {
            return true;
        }

        if (newlines == 0)
        {
            int budget = (VisibleLines + 1) * width;
            return trimmed.Length > budget && TextWidth.Of(trimmed) > budget;
        }

        int rows = 0;
        foreach (var line in trimmed.Split('\n'))
        {
            rows += Math.Max(1, (int)Math.Ceiling(TextWidth.Of(line) / (double)width));
            if (rows > VisibleLines + 1)
            {
                return true;
            }
        }

        return false;
    }

    private static string Slice(string text, int from, int to)
    {
        var builder = new System.Text.StringBuilder();
        int width = 0;
        foreach (var element in Elements(text))
        {
            int w = TextWidth.Of(element);
            if (width >= to)
            {
                break;
            }

            if (width >= from)
            {
                builder.Append(element);
            }

            width += w;
        }

        return builder.ToString();
    }

    private static IEnumerable<string> Elements(string text)
    {
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return (string)enumerator.Current;
        }
    }
}
