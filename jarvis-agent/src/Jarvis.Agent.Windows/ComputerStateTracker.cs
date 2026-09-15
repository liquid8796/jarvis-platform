using System.Security.Cryptography;

namespace Jarvis.Agent.Windows;

public sealed record AccessibilityNode(string Role, string? Name, string? AutomationId = null, string? Value = null);
public sealed record FocusedAutomationElement(string Role, string? Name, string? AutomationId = null, string? Value = null);
public sealed record ComputerObservation(
    string? ForegroundTitle,
    int? ForegroundProcessId,
    string? ForegroundProcessName,
    FocusedAutomationElement? Focused,
    IReadOnlyList<AccessibilityNode> Nodes);
public sealed record ComputerStateSnapshot(
    string StateId,
    long Generation,
    string SessionId,
    DateTimeOffset ObservedAt,
    ComputerObservation Observation);

/// <summary>
/// Binds desktop actions to the most recent observation for one session. A new
/// observation or explicit invalidation makes prior state IDs unusable.
/// </summary>
public sealed class ComputerStateTracker
{
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Dictionary<string, ComputerStateSnapshot> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentBySession = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private long _generation;

    public ComputerStateTracker(int capacity = 64)
    {
        if (capacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int StoredStateCount { get { lock (_sync) return _states.Count; } }

    public ComputerStateSnapshot Capture(string sessionId, ComputerObservation observation)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256) throw new ArgumentException("A bounded session ID is required.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(observation);
        lock (_sync)
        {
            var id = NewId();
            var snapshot = new ComputerStateSnapshot(id, checked(++_generation), sessionId, DateTimeOffset.UtcNow, observation);
            _states[id] = snapshot;
            _currentBySession[sessionId] = id;
            _order.Enqueue(id);
            Trim();
            return snapshot;
        }
    }

    public ComputerStateSnapshot Validate(string sessionId, string stateId)
    {
        if (string.IsNullOrWhiteSpace(stateId)) throw new InvalidOperationException("A current computer stateId is required before state-bound input.");
        lock (_sync)
        {
            if (!_states.TryGetValue(stateId, out var snapshot)) throw new InvalidOperationException("Computer state is stale or no longer available; observe again.");
            if (!StringComparer.Ordinal.Equals(snapshot.SessionId, sessionId)) throw new InvalidOperationException("Computer state belongs to a different session.");
            if (!_currentBySession.TryGetValue(sessionId, out var current) || !StringComparer.Ordinal.Equals(current, stateId))
                throw new InvalidOperationException("Computer state is stale; observe again before input.");
            return snapshot;
        }
    }

    public void Invalidate(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        lock (_sync)
        {
            checked { _generation++; }
            _currentBySession.Remove(sessionId);
        }
    }

    public void InvalidateAll()
    {
        lock (_sync)
        {
            checked { _generation++; }
            _currentBySession.Clear();
        }
    }

    private void Trim()
    {
        while (_states.Count > _capacity && _order.Count > 0)
        {
            var id = _order.Dequeue();
            if (!_states.Remove(id, out var removed)) continue;
            if (_currentBySession.TryGetValue(removed.SessionId, out var current) && StringComparer.Ordinal.Equals(current, id))
                _currentBySession.Remove(removed.SessionId);
        }
    }

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
