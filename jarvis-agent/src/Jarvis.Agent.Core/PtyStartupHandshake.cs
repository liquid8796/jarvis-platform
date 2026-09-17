namespace Jarvis.Agent.Core;

/// <summary>Answers the native host's first DA1 startup query without waiting for a whole terminal frame.</summary>
internal sealed class PtyStartupHandshake
{
    // A conservative primary-attributes reply: no advertised extension capabilities.
    internal const string Response = "\u001b[?1;0c";
    private int _state;
    private bool _answered;
    public bool Observe(ReadOnlySpan<char> text)
    {
        if (_answered) return false;
        foreach (var ch in text)
        {
            switch (_state)
            {
                case 0: _state = ch == '\u001b' ? 1 : 0; break;
                case 1: _state = ch == '[' ? 2 : ch == '\u001b' ? 1 : 0; break;
                case 2:
                    if (ch == 'c') { _answered = true; return true; }
                    _state = ch == '0' ? 3 : ch == '\u001b' ? 1 : 0; break;
                case 3:
                    if (ch == 'c') { _answered = true; return true; }
                    _state = ch == '\u001b' ? 1 : 0; break;
            }
        }
        return false;
    }
}
