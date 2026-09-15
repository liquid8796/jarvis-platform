namespace JarvisCode.Cli.Repl.Input;

/// <summary>
/// The reference's <c>gP</c> hook: a key that acts on its second press within
/// 800 ms. The first press arms it (and may run a first-press action — Ctrl+C
/// clearing the input, Esc showing "Esc again to clear"); a second inside the
/// window fires; the window lapsing disarms it.
/// </summary>
internal sealed class DoublePress
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(800);

    private DateTime _lastPress = DateTime.MinValue;
    private DateTime? _pendingUntil;

    public bool Pending => _pendingUntil is not null;

    /// <summary>The first press's arming timestamp, for renderers that show "Press X again".</summary>
    public DateTime? PendingUntil => _pendingUntil;

    /// <summary>Returns true when this press is the second of a pair.</summary>
    public bool Press(DateTime now)
    {
        Expire(now);
        bool second = now - _lastPress <= Window && _pendingUntil is not null;
        if (second)
        {
            _pendingUntil = null;
        }
        else
        {
            _pendingUntil = now + Window;
        }

        _lastPress = now;
        return second;
    }

    /// <summary>Disarms once the window has lapsed; returns true if it was armed and is now clear.</summary>
    public bool Expire(DateTime now)
    {
        if (_pendingUntil is { } until && now >= until)
        {
            _pendingUntil = null;
            return true;
        }

        return false;
    }

    public void Reset()
    {
        _pendingUntil = null;
        _lastPress = DateTime.MinValue;
    }
}
