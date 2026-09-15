using System.Text;
using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// The Ink model the reference draws with, on a plain console: transcript
/// output is <em>static</em> — printed once and left to scroll — and everything
/// below it (the spinner row, the prompt box, the footer, a dialog) is the
/// <em>live</em> region, erased and redrawn on every change. Rows are wrapped
/// to the width before they are written, so the count of rows to erase is exact.
/// </summary>
internal sealed class Screen
{
    private readonly IConsole _console;
    private int _liveRows;
    private int _caretRowFromBottom;

    public Screen(IConsole console)
    {
        _console = console;
    }

    public int Width => _console.Width;
    public int Height => _console.Height;

    /// <summary>Prints a block into the scrollback; the live region is redrawn after it by the caller.</summary>
    public void AppendStatic(string block)
    {
        var builder = new StringBuilder();
        EraseLive(builder);
        foreach (var line in block.Split('\n'))
        {
            foreach (var row in TextWidth.Wrap(line, Width))
            {
                builder.Append(row).Append("\x1b[K\n");
            }
        }

        _console.Write(builder.ToString());
        _liveRows = 0;
        _caretRowFromBottom = 0;
    }

    /// <summary>Same as <see cref="AppendStatic"/> but with no wrapping and no clear-to-end (for piped output).</summary>
    public void WriteRaw(string text)
    {
        _console.Write(text);
    }

    /// <summary>
    /// Redraws the live region. <paramref name="caret"/> is where the cursor
    /// should rest (row index into the rendered rows, column), or null to leave
    /// it after the last row.
    /// </summary>
    public void SetLive(IReadOnlyList<string> lines, (int Row, int Column)? caret = null)
    {
        var rows = new List<string>();
        var rowOfLine = new List<int>();
        foreach (var line in lines)
        {
            rowOfLine.Add(rows.Count);
            rows.AddRange(TextWidth.Wrap(line, Width));
        }

        if (!_console.IsInteractive)
        {
            return;
        }

        var builder = new StringBuilder();
        EraseLive(builder);
        builder.Append("\x1b[?25l");
        for (int i = 0; i < rows.Count; i++)
        {
            builder.Append(rows[i]).Append("\x1b[K");
            if (i < rows.Count - 1)
            {
                builder.Append('\n');
            }
        }

        _liveRows = rows.Count;
        _caretRowFromBottom = 0;
        if (caret is { } c && rows.Count > 0 && c.Row < lines.Count)
        {
            int row = rowOfLine[c.Row];
            int fromBottom = rows.Count - 1 - row;
            if (fromBottom > 0)
            {
                builder.Append($"\x1b[{fromBottom}A");
            }

            builder.Append($"\x1b[{Math.Max(1, c.Column + 1)}G");
            _caretRowFromBottom = fromBottom;
            builder.Append("\x1b[?25h");
        }

        _console.Write(builder.ToString());
    }

    private void EraseLive(StringBuilder builder)
    {
        if (!_console.IsInteractive)
        {
            return;
        }

        if (_caretRowFromBottom > 0)
        {
            builder.Append($"\x1b[{_caretRowFromBottom}B");
        }

        builder.Append('\r');
        if (_liveRows > 1)
        {
            builder.Append($"\x1b[{_liveRows - 1}A");
        }

        builder.Append("\x1b[J");
        _liveRows = 0;
        _caretRowFromBottom = 0;
    }

    /// <summary>Clears the live region and leaves the cursor at the start of its first row.</summary>
    public void ClearLive()
    {
        var builder = new StringBuilder();
        EraseLive(builder);
        builder.Append("\x1b[?25h");
        _console.Write(builder.ToString());
    }

    /// <summary>
    /// The alternate screen buffer, which is where the reference's /tui
    /// fullscreen presentation draws: entering it keeps the shell's scrollback
    /// intact, leaving it puts the shell back exactly as it was.
    /// </summary>
    public void SetAlternateScreen(bool on)
    {
        _liveRows = 0;
        _caretRowFromBottom = 0;
        _console.Write(on ? "[?1049h[H" : "[?1049l");
    }

    /// <summary>Clears the whole screen (cmd+k / ctrl+l in the reference).</summary>
    public void ClearAll()
    {
        _liveRows = 0;
        _caretRowFromBottom = 0;
        _console.Write("\x1b[2J\x1b[3J\x1b[H");
    }
}
