using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Repl.Keys;

/// <summary>What a key press resolved to.</summary>
internal enum KeyRouteKind
{
    /// <summary>Nothing in this context binds it; the caller may treat it as text.</summary>
    Unbound,

    /// <summary>The first chord of a two-chord sequence; the next press completes it.</summary>
    Pending,

    /// <summary>An action fired.</summary>
    Action,
}

internal readonly record struct KeyRoute(KeyRouteKind Kind, string? Action = null)
{
    public static readonly KeyRoute Unbound = new(KeyRouteKind.Unbound);
    public static readonly KeyRoute Pending = new(KeyRouteKind.Pending);
    public static KeyRoute Fires(string action) => new(KeyRouteKind.Action, action);
}

/// <summary>
/// The reference's key dispatch over a <see cref="KeyMap"/>: a press is
/// normalized to a chord, appended to whatever sequence is already pending, and
/// resolved in the focused context and then in Global. A chord that only starts
/// a longer binding (<c>ctrl+x</c> before <c>ctrl+x ctrl+e</c>) is held; a
/// press that continues nothing clears the pending sequence and is re-resolved
/// on its own, so a mistyped prefix does not swallow the next key.
/// </summary>
internal sealed class KeyRouter(KeyMap map)
{
    private readonly List<string> _pending = [];

    public KeyMap Map { get; } = map;

    /// <summary>The chords held from an incomplete sequence, for the renderer's hint.</summary>
    public IReadOnlyList<string> Pending => _pending;

    public void Reset() => _pending.Clear();

    public KeyRoute Route(string context, KeyPress press)
    {
        var chord = press.Chord;
        var candidate = new List<string>(_pending) { chord };
        if (Map.Resolve(context, candidate) is { } action)
        {
            _pending.Clear();
            return KeyRoute.Fires(action);
        }

        if (Map.IsPrefix(context, candidate))
        {
            _pending.Clear();
            _pending.AddRange(candidate);
            return KeyRoute.Pending;
        }

        if (_pending.Count > 0)
        {
            // The sequence died; the press still gets its own chance.
            _pending.Clear();
            if (Map.Resolve(context, [chord]) is { } alone)
            {
                return KeyRoute.Fires(alone);
            }

            if (Map.IsPrefix(context, [chord]))
            {
                _pending.Add(chord);
                return KeyRoute.Pending;
            }
        }

        return KeyRoute.Unbound;
    }
}
