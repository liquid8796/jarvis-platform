using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Repl.Input;

/// <summary>The vim editing modes the reference's input reports (its <c>-- INSERT --</c> / <c>-- VISUAL --</c> indicator).</summary>
internal enum VimState
{
    Normal,
    Insert,
    Visual,
    VisualLine,
}

/// <summary>
/// The vim layer over <see cref="LineEditor"/> for <c>editorMode: "vim"</c>:
/// the motions the reference's <c>aa</c> table dispatches (h l j k gj gk w b e
/// 0 ^ $ gg G), counts, the operators d/c/y with dd/cc/yy and motions, x/X, i a
/// I A o O, p/P, u, r, ~, v/V, and Escape back to normal. The composer's
/// insert-mode keys are handled by the editor itself.
/// </summary>
internal sealed class VimMode
{
    private readonly LineEditor _editor;
    private string _pendingOperator = "";
    private string _pendingPrefix = "";
    private int _count;
    private string _register = "";
    private bool _registerLinewise;
    private int _visualAnchor;

    public VimMode(LineEditor editor)
    {
        _editor = editor;
    }

    public VimState State { get; private set; } = VimState.Insert;

    /// <summary>The indicator the footer shows under the prompt.</summary>
    public string? Indicator => State switch
    {
        VimState.Insert => "-- INSERT --",
        VimState.Visual => "-- VISUAL --",
        VimState.VisualLine => "-- VISUAL LINE --",
        _ => null,
    };

    public string ModeName => State switch
    {
        VimState.Insert => "INSERT",
        VimState.Visual => "VISUAL",
        VimState.VisualLine => "VISUAL LINE",
        _ => "NORMAL",
    };

    /// <summary>Handles a key in normal or visual mode; false means the editor should treat it normally (insert mode).</summary>
    public bool Handle(KeyPress press, DateTime now)
    {
        if (press.Key == "escape" && !press.Ctrl && !press.Alt)
        {
            if (State == VimState.Insert)
            {
                State = VimState.Normal;
                _editor.Left();
            }
            else
            {
                State = VimState.Normal;
            }

            ResetPending();
            return true;
        }

        if (State == VimState.Insert)
        {
            return false;
        }

        if (press.Text is not { Length: 1 } ch)
        {
            switch (press.Key)
            {
                case "left": Move(_editor.Left); return true;
                case "right": Move(_editor.Right); return true;
                case "up": Move(_editor.UpLogicalLine); return true;
                case "down": Move(_editor.DownLogicalLine); return true;
                case "home": _editor.StartOfLogicalLine(); return true;
                case "end": _editor.EndOfLogicalLine(); return true;
                case "backspace": Move(_editor.Left); return true;
                case "delete": DeleteChars(Count()); return true;
                default: return true;
            }
        }

        char c = ch[0];
        if (char.IsAsciiDigit(c) && (c != '0' || _count > 0))
        {
            _count = _count * 10 + (c - '0');
            return true;
        }

        if (_pendingPrefix == "g")
        {
            _pendingPrefix = "";
            switch (c)
            {
                case 'g': _editor.Home(); break;
                case 'j': Move(_editor.DownLogicalLine); break;
                case 'k': Move(_editor.UpLogicalLine); break;
            }

            ResetCount();
            return true;
        }

        if (_pendingPrefix == "r")
        {
            _pendingPrefix = "";
            _editor.PushUndo(now, immediate: true);
            ReplaceChar(c);
            ResetCount();
            return true;
        }

        if (_pendingOperator.Length > 0)
        {
            ApplyOperator(c, now);
            return true;
        }

        switch (c)
        {
            case 'h': Move(_editor.Left); break;
            case 'l': case ' ': Move(_editor.Right); break;
            case 'j': Move(_editor.DownLogicalLine); break;
            case 'k': Move(_editor.UpLogicalLine); break;
            case 'w': Move(NextWordStart); break;
            case 'b': Move(_editor.BackwardWord); break;
            case 'e': Move(EndOfWord); break;
            case '0': _editor.StartOfLogicalLine(); break;
            case '^': _editor.StartOfLogicalLine(); SkipSpaces(); break;
            case '$': _editor.EndOfLogicalLine(); break;
            case 'G': _editor.End(); break;
            case 'g': _pendingPrefix = "g"; return true;
            case 'r': _pendingPrefix = "r"; return true;
            case 'x': _editor.PushUndo(now, immediate: true); DeleteChars(Count()); break;
            case 'X': _editor.PushUndo(now, immediate: true); for (int i = 0; i < Count(); i++) _editor.Backspace(); break;
            case 'i': State = VimState.Insert; break;
            case 'a': _editor.Right(); State = VimState.Insert; break;
            case 'I': _editor.StartOfLogicalLine(); State = VimState.Insert; break;
            case 'A': _editor.EndOfLogicalLine(); State = VimState.Insert; break;
            case 'o': _editor.PushUndo(now, immediate: true); _editor.EndOfLogicalLine(); _editor.Insert("\n"); State = VimState.Insert; break;
            case 'O': _editor.PushUndo(now, immediate: true); _editor.StartOfLogicalLine(); _editor.Insert("\n"); _editor.Left(); State = VimState.Insert; break;
            case 'd': case 'c': case 'y':
                if (State is VimState.Visual or VimState.VisualLine)
                {
                    ApplyVisualOperator(c, now);
                }
                else
                {
                    _pendingOperator = c.ToString();
                    return true;
                }

                break;
            case 'D': _editor.PushUndo(now, immediate: true); Yank(_editor.KillToLineEnd(), false); break;
            case 'C': _editor.PushUndo(now, immediate: true); Yank(_editor.KillToLineEnd(), false); State = VimState.Insert; break;
            case 'p': _editor.PushUndo(now, immediate: true); Put(after: true); break;
            case 'P': _editor.PushUndo(now, immediate: true); Put(after: false); break;
            case 'u': _editor.Undo(); break;
            case '~': _editor.PushUndo(now, immediate: true); ToggleCase(); break;
            case 'v': _visualAnchor = _editor.Offset; State = State == VimState.Visual ? VimState.Normal : VimState.Visual; break;
            case 'V': _visualAnchor = _editor.Offset; State = State == VimState.VisualLine ? VimState.Normal : VimState.VisualLine; break;
        }

        ResetCount();
        return true;
    }

    private int Count() => Math.Max(1, _count);

    private void ResetCount() => _count = 0;

    private void ResetPending()
    {
        _pendingOperator = "";
        _pendingPrefix = "";
        _count = 0;
    }

    private void Move(Action step)
    {
        for (int i = 0; i < Count(); i++)
        {
            step();
        }
    }

    private void NextWordStart() => _editor.Set(_editor.Text, _editor.NextWordStartOffset(_editor.Offset));

    private void SkipSpaces()
    {
        var text = _editor.Text;
        int i = _editor.Offset;
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        _editor.Set(text, i);
    }

    private void EndOfWord()
    {
        var text = _editor.Text;
        int i = _editor.Offset + 1;
        while (i < text.Length && !IsWord(text[i])) i++;
        while (i + 1 < text.Length && IsWord(text[i + 1])) i++;
        _editor.Set(text, Math.Min(i, Math.Max(0, text.Length - 1)));
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void DeleteChars(int count)
    {
        var deleted = new System.Text.StringBuilder();
        for (int i = 0; i < count && _editor.Offset < _editor.Text.Length; i++)
        {
            deleted.Append(_editor.Text[_editor.Offset]);
            _editor.Delete();
        }

        Yank(deleted.ToString(), false);
    }

    private void ReplaceChar(char c)
    {
        if (_editor.Offset >= _editor.Text.Length)
        {
            return;
        }

        var text = _editor.Text;
        _editor.Set(text[.._editor.Offset] + c + text[(_editor.Offset + 1)..], _editor.Offset);
    }

    private void ToggleCase()
    {
        if (_editor.Offset >= _editor.Text.Length)
        {
            return;
        }

        var text = _editor.Text;
        char c = text[_editor.Offset];
        c = char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
        _editor.Set(text[.._editor.Offset] + c + text[(_editor.Offset + 1)..], _editor.Offset + 1);
    }

    private void Yank(string text, bool linewise)
    {
        if (text.Length > 0)
        {
            _register = text;
            _registerLinewise = linewise;
        }
    }

    private void Put(bool after)
    {
        if (_register.Length == 0)
        {
            return;
        }

        if (_registerLinewise)
        {
            if (after)
            {
                _editor.EndOfLogicalLine();
                _editor.Insert("\n" + _register.TrimEnd('\n'));
            }
            else
            {
                _editor.StartOfLogicalLine();
                _editor.Insert(_register.TrimEnd('\n') + "\n");
            }
        }
        else
        {
            if (after && _editor.Offset < _editor.Text.Length)
            {
                _editor.Right();
            }

            _editor.Insert(_register);
            _editor.Left();
        }
    }

    private void ApplyOperator(char motion, DateTime now)
    {
        var op = _pendingOperator;
        _pendingOperator = "";
        int count = Count();
        ResetCount();
        var text = _editor.Text;
        int start = _editor.Offset;
        int end;
        bool linewise = false;
        if (motion == op[0])
        {
            // dd / cc / yy: whole logical lines.
            start = _editor.LineStart(start);
            end = start;
            for (int i = 0; i < count; i++)
            {
                end = _editor.LineEnd(end);
                if (end < text.Length) end++;
            }

            linewise = true;
        }
        else
        {
            switch (motion)
            {
                case 'w': end = start; for (int i = 0; i < count; i++) end = _editor.NextWordStartOffset(end); break;
                case 'b': end = start; for (int i = 0; i < count; i++) end = _editor.PrevWordOffset(end); break;
                case 'e': end = start; for (int i = 0; i < count; i++) { _editor.Set(text, end); EndOfWord(); end = Math.Min(text.Length, _editor.Offset + 1); } _editor.Set(text, start); break;
                case '$': end = _editor.LineEnd(start); break;
                case '0': end = _editor.LineStart(start); break;
                case 'h': end = Math.Max(0, start - count); break;
                case 'l': end = Math.Min(text.Length, start + count); break;
                default: return;
            }
        }

        int from = Math.Min(start, end), to = Math.Max(start, end);
        if (op == "y")
        {
            Yank(text[from..to], linewise);
            _editor.Set(text, from);
            return;
        }

        _editor.PushUndo(now, immediate: true);
        Yank(text[from..to], linewise);
        _editor.Set(text[..from] + text[to..], from);
        if (op == "c")
        {
            State = VimState.Insert;
        }
    }

    private void ApplyVisualOperator(char op, DateTime now)
    {
        var text = _editor.Text;
        int from = Math.Min(_visualAnchor, _editor.Offset);
        int to = Math.Max(_visualAnchor, _editor.Offset);
        bool linewise = State == VimState.VisualLine;
        if (linewise)
        {
            from = _editor.LineStart(from);
            to = _editor.LineEnd(to);
            if (to < text.Length) to++;
        }
        else
        {
            to = Math.Min(text.Length, to + 1);
        }

        State = VimState.Normal;
        if (op == 'y')
        {
            Yank(text[from..to], linewise);
            _editor.Set(text, from);
            return;
        }

        _editor.PushUndo(now, immediate: true);
        Yank(text[from..to], linewise);
        _editor.Set(text[..from] + text[to..], from);
        if (op == 'c')
        {
            State = VimState.Insert;
        }
    }

    /// <summary>Visual selection bounds for the renderer, when a visual mode is active.</summary>
    public (int Start, int End)? Selection
    {
        get
        {
            if (State is not (VimState.Visual or VimState.VisualLine))
            {
                return null;
            }

            int from = Math.Min(_visualAnchor, _editor.Offset);
            int to = Math.Max(_visualAnchor, _editor.Offset);
            if (State == VimState.VisualLine)
            {
                from = _editor.LineStart(from);
                to = _editor.LineEnd(to);
            }
            else
            {
                to = Math.Min(_editor.Text.Length, to + 1);
            }

            return (from, to);
        }
    }
}
