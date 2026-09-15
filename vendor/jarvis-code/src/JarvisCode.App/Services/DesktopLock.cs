using System.Threading;

namespace JarvisCode.App.Services;

/// <summary>
/// One desktop, one driver. The reference refuses a computer-use call made while
/// another session is driving the screen, with the message below — its <c>He</c>
/// in <c>index2.chunk-CEBgETf7.js</c>.
///
/// A session may re-enter (a batch nested inside its own work is still that
/// session's turn); a different one is refused rather than queued, because the
/// coordinates it is about to click were read off a screenshot the other session
/// has already changed.
/// </summary>
internal static class DesktopLock
{
    /// <summary>
    /// The reference's refusal, verbatim — its <c>ir</c> in
    /// <c>index.chunk-U7g3hHKL.js</c>, which is the computer-use server's own
    /// wording. (Its shell carries a longer variant naming a stop button; that
    /// one belongs to a surface this port does not raise.)
    /// </summary>
    public const string InUseMessage =
        "Another Jarvis session is currently using the computer. Wait for that session to finish, or find a " +
        "non-computer-use approach.";

    private static readonly object Gate = new();
    private static string? _owner;
    private static int _depth;
    private static int _leases;

    /// <summary>
    /// Raised when the desktop goes from unheld to held and back. The reference
    /// raises the same edge as `cuLockChanged` and hangs its on-screen indicator
    /// off it; a caller with no session identity counts here even though it takes
    /// no ownership, because the person at the keyboard is owed the indicator
    /// whether or not the driver could be told from another one.
    /// </summary>
    internal static event Action<bool>? HeldChanged;

    /// <summary>
    /// Takes the desktop for this session, or reports that another holds it.
    /// Dispose releases it; a lease that was not held releases nothing.
    /// </summary>
    public static Lease TryAcquire(string? sessionId)
    {
        // A caller with no session identity cannot be told apart from another
        // such caller, so it neither takes the lock nor is refused by it.
        if (string.IsNullOrEmpty(sessionId))
        {
            Take();
            return new Lease(true, null, true);
        }

        lock (Gate)
        {
            if (_owner is not null && !string.Equals(_owner, sessionId, StringComparison.Ordinal))
            {
                return new Lease(false, null, false);
            }

            _owner = sessionId;
            _depth++;
        }

        Take();
        return new Lease(true, sessionId, true);
    }

    /// <summary>The session currently driving the desktop, or null.</summary>
    internal static string? Owner
    {
        get
        {
            lock (Gate)
            {
                return _owner;
            }
        }
    }

    /// <summary>Counts a held lease, raising the edge for the first one.</summary>
    private static void Take()
    {
        bool first;
        lock (Gate)
        {
            first = _leases++ == 0;
        }

        if (first)
        {
            HeldChanged?.Invoke(true);
        }
    }

    /// <summary>Drops a held lease, raising the edge when the last one goes.</summary>
    private static void Drop()
    {
        bool last;
        lock (Gate)
        {
            last = --_leases <= 0;
            if (last)
            {
                _leases = 0;
            }
        }

        if (last)
        {
            HeldChanged?.Invoke(false);
        }
    }

    private static void Release(string sessionId)
    {
        lock (Gate)
        {
            if (!string.Equals(_owner, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            if (--_depth <= 0)
            {
                _depth = 0;
                _owner = null;
            }
        }
    }

    /// <summary>A held (or not held) claim on the desktop.</summary>
    internal readonly struct Lease(bool held, string? sessionId, bool counted) : IDisposable
    {
        public bool Held { get; } = held;

        public void Dispose()
        {
            if (counted)
            {
                Drop();
            }

            if (sessionId is not null)
            {
                Release(sessionId);
            }
        }
    }
}
