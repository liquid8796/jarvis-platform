using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// Minimal VT interpreter for the integrated terminal: keeps a plain-text line
/// buffer, honoring CR overwrites, backspace, tabs, erase-line, clear-screen,
/// and swallowing color/OSC sequences. Not a full emulator — enough for
/// shells, build output, git, and dev servers to read correctly.
/// </summary>
public sealed class TerminalScreen
{
    private const int MaxLines = 600;

    private readonly List<StringBuilder> _lines = [new()];
    private int _row;
    private int _column;

    /// <summary>Buffer line that CUP addresses as row 1; rebased by clear-screen.</summary>
    private int _viewportTop;
    private string _pending = "";

    public event Action? Changed;

    public string GetText()
    {
        lock (_lines)
        {
            return string.Join('\n', _lines.Select(static l => l.ToString()));
        }
    }

    public void Clear()
    {
        lock (_lines)
        {
            _lines.Clear();
            _lines.Add(new StringBuilder());
            _row = 0;
            _column = 0;
            _viewportTop = 0;
        }

        Changed?.Invoke();
    }

    public void Append(string chunk)
    {
        lock (_lines)
        {
            var text = _pending + chunk;
            _pending = "";
            var i = 0;
            while (i < text.Length)
            {
                var ch = text[i];
                if (ch == '\x1b')
                {
                    var consumed = ConsumeEscape(text, i);
                    if (consumed == 0)
                    {
                        // Incomplete sequence at the end of the chunk; keep it for next time.
                        _pending = text[i..];
                        break;
                    }

                    i += consumed;
                    continue;
                }

                switch (ch)
                {
                    case '\r':
                        _column = 0;
                        break;
                    case '\n':
                        NewLine();
                        break;
                    case '\b':
                        _column = Math.Max(0, _column - 1);
                        break;
                    case '\t':
                        WriteChar(' ');
                        while (_column % 8 != 0)
                        {
                            WriteChar(' ');
                        }

                        break;
                    case '\a':
                        break;
                    default:
                        if (!char.IsControl(ch))
                        {
                            WriteChar(ch);
                        }

                        break;
                }

                i++;
            }
        }

        Changed?.Invoke();
    }

    private void WriteChar(char ch)
    {
        var line = _lines[_row];
        while (line.Length < _column)
        {
            line.Append(' ');
        }

        if (_column < line.Length)
        {
            line[_column] = ch;
        }
        else
        {
            line.Append(ch);
        }

        _column++;
    }

    private void NewLine()
    {
        _row++;
        if (_row >= _lines.Count)
        {
            _lines.Add(new StringBuilder());
        }

        if (_lines.Count > MaxLines)
        {
            var drop = _lines.Count - MaxLines;
            _lines.RemoveRange(0, drop);
            _row = Math.Max(0, _row - drop);
        }

        // Column position survives LF (VT semantics); CR resets it.
    }

    /// <summary>Returns consumed char count, or 0 when the sequence is incomplete.</summary>
    private int ConsumeEscape(string text, int start)
    {
        if (start + 1 >= text.Length)
        {
            return 0;
        }

        var kind = text[start + 1];
        if (kind == '[')
        {
            // CSI: ESC [ parameters final-byte(0x40–0x7E)
            var i = start + 2;
            while (i < text.Length && (text[i] < 0x40 || text[i] > 0x7E))
            {
                i++;
            }

            if (i >= text.Length)
            {
                return 0;
            }

            HandleCsi(text[(start + 2)..i], text[i]);
            return i - start + 1;
        }

        if (kind == ']')
        {
            // OSC: ESC ] ... (BEL or ESC \)
            var i = start + 2;
            while (i < text.Length)
            {
                if (text[i] == '\a')
                {
                    return i - start + 1;
                }

                if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\')
                {
                    return i - start + 2;
                }

                i++;
            }

            return 0;
        }

        // Two-character escapes (ESC =, ESC >, ESC 7…) and charset selects (ESC ( B).
        if (kind is '(' or ')' or '#')
        {
            return start + 2 < text.Length ? 3 : 0;
        }

        return 2;
    }

    /// <summary>CUP: ESC[row;colH — 1-based, relative to the current viewport top.</summary>
    private void MoveCursor(string parameters)
    {
        int row = 1, column = 1;
        var parts = parameters.Split(';');
        if (parts.Length > 0 && int.TryParse(parts[0], out var parsedRow) && parsedRow > 0)
        {
            row = parsedRow;
        }

        if (parts.Length > 1 && int.TryParse(parts[1], out var parsedColumn) && parsedColumn > 0)
        {
            column = parsedColumn;
        }

        _row = _viewportTop + row - 1;
        while (_lines.Count <= _row)
        {
            _lines.Add(new StringBuilder());
        }

        _column = column - 1;
    }

    private void HandleCsi(string parameters, char final)
    {
        switch (final)
        {
            case 'K':
                // Erase in line (default: cursor to end).
                var line = _lines[_row];
                if (_column < line.Length)
                {
                    line.Length = _column;
                }

                break;
            case 'J':
                if (parameters is "2" or "3")
                {
                    // Erase display: "cls" must actually clear, and row addressing
                    // restarts from the fresh screen.
                    _lines.Clear();
                    _lines.Add(new StringBuilder());
                    _viewportTop = 0;
                    _row = 0;
                    _column = 0;
                }

                break;
            case 'H' or 'f':
                MoveCursor(parameters);
                break;
            case 'C':
                // Cursor forward moves without overwriting.
                _column += int.TryParse(parameters, out var n) && n > 0 ? n : 1;
                break;
            case 'G':
                _column = int.TryParse(parameters, out var col) && col > 0 ? col - 1 : 0;
                break;
            // Colors ('m'), cursor show/hide, modes, titles… are display-only; drop.
        }
    }
}
