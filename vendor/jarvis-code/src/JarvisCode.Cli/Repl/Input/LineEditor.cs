using System.Text.RegularExpressions;

namespace JarvisCode.Cli.Repl.Input;

/// <summary>
/// The composer's text and cursor, with the editing operations the reference's
/// cursor class exposes (<c>_i</c> in CLI 2.1.257): character and word
/// movement, logical-line movement, the readline kills into a kill ring with
/// yank/yank-pop, paste-placeholder-aware token deletion, and an undo buffer
/// that coalesces edits landing within a second (its <c>maxBufferSize:50,
/// debounceMs:1000</c>). Pure state: nothing here touches a terminal.
/// </summary>
internal sealed partial class LineEditor
{
    private string _text = "";
    private int _offset;
    private readonly List<(string Text, int Offset)> _undo = [];
    private DateTime _lastUndoPush = DateTime.MinValue;
    private readonly KillRing _kills = new();

    public const int UndoBufferSize = 50;
    public static readonly TimeSpan UndoDebounce = TimeSpan.FromMilliseconds(1000);

    public string Text => _text;
    public int Offset => _offset;
    public bool IsEmpty => _text.Length == 0;
    public bool CanUndo => _undo.Count > 0;
    public KillRing Kills => _kills;

    /// <summary>The paste placeholders the reference's cursor treats as one token (its <c>deleteTokenBefore</c> regex).</summary>
    [GeneratedRegex(@"(^|\s)\[(Pasted text #\d+(?: \+\d+ lines)?|Image #\d+|Audio #\d+|\.\.\.Truncated text #\d+ \+\d+ lines\.\.\.)\]$")]
    private static partial Regex TrailingPlaceholder();

    public void Set(string text, int? offset = null)
    {
        _text = text;
        _offset = Math.Clamp(offset ?? text.Length, 0, text.Length);
    }

    public void Clear()
    {
        _text = "";
        _offset = 0;
    }

    /// <summary>The reference's <c>pushToBuffer</c>: a snapshot before an edit, coalesced within the debounce window.</summary>
    public void PushUndo(DateTime now, bool immediate = false)
    {
        if (!immediate && now - _lastUndoPush < UndoDebounce && _undo.Count > 0)
        {
            return;
        }

        _undo.Add((_text, _offset));
        if (_undo.Count > UndoBufferSize)
        {
            _undo.RemoveAt(0);
        }

        _lastUndoPush = now;
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        var (text, offset) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _text = text;
        _offset = Math.Clamp(offset, 0, text.Length);
        return true;
    }

    public void ClearUndo() => _undo.Clear();

    public void Insert(string text)
    {
        _text = _text[.._offset] + text + _text[_offset..];
        _offset += text.Length;
    }

    public void Backspace()
    {
        if (_offset == 0)
        {
            return;
        }

        int start = PrevOffset(_offset);
        _text = _text[..start] + _text[_offset..];
        _offset = start;
    }

    public void Delete()
    {
        if (_offset >= _text.Length)
        {
            return;
        }

        _text = _text[.._offset] + _text[NextOffset(_offset)..];
    }

    public void Left()
    {
        if (_offset > 0)
        {
            _offset = PrevOffset(_offset);
        }
    }

    public void Right()
    {
        if (_offset < _text.Length)
        {
            _offset = NextOffset(_offset);
        }
    }

    public void Home() => _offset = 0;
    public void End() => _offset = _text.Length;

    public int LineStart(int offset)
    {
        int nl = _text.LastIndexOf('\n', Math.Max(0, offset - 1));
        return offset == 0 ? 0 : nl < 0 ? 0 : (nl < offset ? nl + 1 : 0);
    }

    public int LineEnd(int offset)
    {
        int nl = _text.IndexOf('\n', offset);
        return nl < 0 ? _text.Length : nl;
    }

    public void StartOfLogicalLine() => _offset = LineStart(_offset);
    public void EndOfLogicalLine() => _offset = LineEnd(_offset);

    /// <summary>True when the cursor sits on the first logical line (up arrow means history).</summary>
    public bool OnFirstLine => _text.IndexOf('\n') is var nl && (nl < 0 || _offset <= nl);

    /// <summary>True when the cursor sits on the last logical line (down arrow means history).</summary>
    public bool OnLastLine => _text.LastIndexOf('\n') is var nl && (nl < 0 || _offset > nl);

    public void UpLogicalLine()
    {
        int start = LineStart(_offset);
        if (start == 0)
        {
            _offset = 0;
            return;
        }

        int column = _offset - start;
        int prevStart = LineStart(start - 1);
        int prevEnd = start - 1;
        _offset = Math.Min(prevStart + column, prevEnd);
    }

    public void DownLogicalLine()
    {
        int end = LineEnd(_offset);
        if (end >= _text.Length)
        {
            _offset = _text.Length;
            return;
        }

        int column = _offset - LineStart(_offset);
        int nextStart = end + 1;
        int nextEnd = LineEnd(nextStart);
        _offset = Math.Min(nextStart + column, nextEnd);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The reference's <c>nextWord</c>: past the current run of word chars, then past the separators.</summary>
    public int NextWordOffset(int from)
    {
        int i = from;
        while (i < _text.Length && !IsWordChar(_text[i])) i++;
        while (i < _text.Length && IsWordChar(_text[i])) i++;
        return i;
    }

    /// <summary>
    /// Vim's <c>w</c>: the start of the next word, not the end of this one.
    /// Readline's forward-word (<see cref="NextWordOffset"/>) stops at the end
    /// of the current word, which is a different place and would make <c>dw</c>
    /// leave the separating space behind.
    /// </summary>
    public int NextWordStartOffset(int from)
    {
        int i = from;
        if (i >= _text.Length)
        {
            return _text.Length;
        }

        if (IsWordChar(_text[i]))
        {
            while (i < _text.Length && IsWordChar(_text[i])) i++;
        }
        else if (!char.IsWhiteSpace(_text[i]))
        {
            while (i < _text.Length && !IsWordChar(_text[i]) && !char.IsWhiteSpace(_text[i])) i++;
        }

        while (i < _text.Length && char.IsWhiteSpace(_text[i]) && _text[i] != '\n') i++;
        return i;
    }

    public int PrevWordOffset(int from)
    {
        int i = from;
        while (i > 0 && !IsWordChar(_text[i - 1])) i--;
        while (i > 0 && IsWordChar(_text[i - 1])) i--;
        return i;
    }

    /// <summary>Readline's forward-word: the end of the next word.</summary>
    public void ForwardWord() => _offset = NextWordOffset(_offset);

    public void BackwardWord() => _offset = PrevWordOffset(_offset);

    /// <summary>Emacs-style whitespace-delimited WORD boundary before the cursor.</summary>
    public int PrevWORDOffset(int from)
    {
        int i = from;
        while (i > 0 && char.IsWhiteSpace(_text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(_text[i - 1])) i--;
        return i;
    }

    private string KillRange(int start, int end, KillDirection direction)
    {
        if (end <= start)
        {
            return "";
        }

        var killed = _text[start..end];
        _text = _text[..start] + _text[end..];
        _offset = start;
        _kills.Kill(killed, direction);
        return killed;
    }

    /// <summary>Ctrl+K.</summary>
    public string KillToLineEnd() => KillRange(_offset, LineEnd(_offset) is var e && e == _offset && e < _text.Length ? e + 1 : LineEnd(_offset), KillDirection.Append);

    /// <summary>Ctrl+U.</summary>
    public string KillToLineStart()
    {
        int start = LineStart(_offset);
        return KillRange(start, _offset, KillDirection.Prepend);
    }

    /// <summary>Alt+D.</summary>
    public string KillWord() => KillRange(_offset, NextWordOffset(_offset), KillDirection.Append);

    /// <summary>Ctrl+W under the readline flavor.</summary>
    public string BackwardKillWord() => KillRange(PrevWordOffset(_offset), _offset, KillDirection.Prepend);

    /// <summary>Ctrl+W under the default flavor: a paste placeholder or a word.</summary>
    public string DeleteWordBefore()
    {
        if (DeleteTokenBefore())
        {
            return "";
        }

        return KillRange(PrevWordOffset(_offset), _offset, KillDirection.Prepend);
    }

    /// <summary>Alt+W: whitespace-delimited.</summary>
    public string DeleteWORDBefore() => KillRange(PrevWORDOffset(_offset), _offset, KillDirection.Prepend);

    /// <summary>Alt+D under the default flavor.</summary>
    public void DeleteWordAfter()
    {
        if (_offset >= _text.Length)
        {
            return;
        }

        int end = NextWordOffset(_offset);
        _text = _text[.._offset] + _text[end..];
    }

    /// <summary>
    /// The reference's <c>deleteTokenBefore</c>: a paste placeholder directly
    /// before the cursor (or before the space before the cursor) is removed whole.
    /// </summary>
    public bool DeleteTokenBefore()
    {
        if (_offset == 0)
        {
            return false;
        }

        if (_offset < _text.Length && !char.IsWhiteSpace(_text[_offset]))
        {
            return false;
        }

        var match = TrailingPlaceholder().Match(_text[.._offset]);
        if (!match.Success)
        {
            return false;
        }

        int start = match.Index + match.Groups[1].Length;
        _text = _text[..start] + _text[_offset..];
        _offset = start;
        return true;
    }

    /// <summary>Ctrl+Y.</summary>
    public bool Yank()
    {
        var text = _kills.Top;
        if (text.Length == 0)
        {
            return false;
        }

        int start = _offset;
        Insert(text);
        _kills.NoteYank(start, text.Length);
        return true;
    }

    /// <summary>Alt+Y right after a yank.</summary>
    public bool YankPop()
    {
        if (_kills.LastYank is not { } yank)
        {
            return false;
        }

        var next = _kills.Pop();
        if (next is null)
        {
            return false;
        }

        _text = _text[..yank.Start] + next + _text[(yank.Start + yank.Length)..];
        _offset = yank.Start + next.Length;
        _kills.NoteYank(yank.Start, next.Length);
        return true;
    }

    private int PrevOffset(int offset)
    {
        if (offset >= 2 && char.IsLowSurrogate(_text[offset - 1]) && char.IsHighSurrogate(_text[offset - 2]))
        {
            return offset - 2;
        }

        return offset - 1;
    }

    private int NextOffset(int offset)
    {
        if (offset + 1 < _text.Length && char.IsHighSurrogate(_text[offset]) && char.IsLowSurrogate(_text[offset + 1]))
        {
            return offset + 2;
        }

        return offset + 1;
    }
}

internal enum KillDirection
{
    Append,
    Prepend,
}

/// <summary>
/// Readline's kill ring: consecutive kills grow one entry (appending for a
/// forward kill, prepending for a backward one), a non-kill breaks the run, and
/// yank-pop walks the ring.
/// </summary>
internal sealed class KillRing
{
    private readonly List<string> _ring = [];
    private bool _lastWasKill;
    private int _popIndex;

    public sealed record YankState(int Start, int Length);

    public YankState? LastYank { get; private set; }

    public string Top => _ring.Count == 0 ? "" : _ring[^1];

    public void Kill(string text, KillDirection direction)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (_lastWasKill && _ring.Count > 0)
        {
            _ring[^1] = direction == KillDirection.Append ? _ring[^1] + text : text + _ring[^1];
        }
        else
        {
            _ring.Add(text);
        }

        _lastWasKill = true;
        LastYank = null;
    }

    /// <summary>Anything that is not a kill ends the accumulation run.</summary>
    public void Interrupt()
    {
        _lastWasKill = false;
    }

    public void NoteYank(int start, int length)
    {
        LastYank = new YankState(start, length);
        _lastWasKill = false;
    }

    /// <summary>The previous ring entry, rotating; null when the ring holds fewer than two.</summary>
    public string? Pop()
    {
        if (_ring.Count < 2)
        {
            return null;
        }

        _popIndex = (_popIndex + 1) % _ring.Count;
        var index = _ring.Count - 1 - _popIndex;
        return _ring[index];
    }
}
