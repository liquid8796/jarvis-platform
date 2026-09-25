using System.Drawing;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows;

public sealed record ComputerUseObservation<TElement>(
    string ObservationId,
    string? ScreenshotId,
    long Generation,
    string Scope,
    nint Window,
    Rectangle Bounds,
    IReadOnlyList<TElement> Elements,
    DateTimeOffset ObservedAt);

/// <summary>
/// Binds desktop inputs to one exact observation generation. A new observation for the same
/// session/window or the first attempted input invalidates every prior element index and coordinate.
/// </summary>
public sealed class ComputerUseObservationStore<TElement>
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ComputerUseObservation<TElement>> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Scope, nint Window), string> _current = new();
    private readonly Queue<string> _order = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private long _generation;

    public ComputerUseObservationStore(int capacity = 512, TimeSpan? ttl = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ttl = ttl ?? TimeSpan.FromMinutes(2);
        if (_ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
    }

    public int Count { get { lock (_sync) return _observations.Count; } }

    public ComputerUseObservation<TElement> Capture(string scope, nint window, Rectangle bounds,
        IReadOnlyList<TElement> elements, bool hasScreenshot, DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("Observation scope is required.", nameof(scope));
        if (window == nint.Zero) throw new ArgumentException("Window is required.", nameof(window));
        ArgumentNullException.ThrowIfNull(elements);
        var now = observedAt ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            RemoveExpiredUnsafe(now);
            var key = (scope, window);
            if (_current.Remove(key, out var previous)) _observations.Remove(previous);
            var observation = new ComputerUseObservation<TElement>(
                "obs_" + Guid.NewGuid().ToString("N"),
                hasScreenshot ? "shot_" + Guid.NewGuid().ToString("N") : null,
                checked(++_generation), scope, window, bounds, elements, now);
            _observations[observation.ObservationId] = observation;
            _current[key] = observation.ObservationId;
            _order.Enqueue(observation.ObservationId);
            TrimUnsafe();
            return observation;
        }
    }

    public ComputerUseObservation<TElement> RequireCurrent(string scope, nint window, string observationId,
        Rectangle currentBounds, string? screenshotId = null, bool requireScreenshot = false)
    {
        if (string.IsNullOrWhiteSpace(observationId))
            throw Stale("observation_id is required. Call get_window_state and use its current observation_id.");
        lock (_sync)
        {
            RemoveExpiredUnsafe(DateTimeOffset.UtcNow);
            if (!_observations.TryGetValue(observationId, out var observation) ||
                !StringComparer.Ordinal.Equals(observation.Scope, scope) || observation.Window != window ||
                !_current.TryGetValue((scope, window), out var current) ||
                !StringComparer.Ordinal.Equals(current, observationId))
                throw Stale("The observation is stale, belongs to another session/window, or has already been consumed. Call get_window_state again.");
            if (observation.Bounds != currentBounds)
            {
                RemoveUnsafe(observation);
                throw Stale("The window moved or changed size after the observation. Call get_window_state again.");
            }
            if (requireScreenshot)
            {
                if (observation.ScreenshotId is null || string.IsNullOrWhiteSpace(screenshotId) ||
                    !StringComparer.Ordinal.Equals(observation.ScreenshotId, screenshotId))
                    throw Stale("A coordinate action requires the matching screenshot_id from the same observation.");
            }
            else if (screenshotId is not null &&
                     !StringComparer.Ordinal.Equals(observation.ScreenshotId, screenshotId))
                throw Stale("screenshot_id does not belong to the requested observation.");
            return observation;
        }
    }

    public void Consume(ComputerUseObservation<TElement> observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_sync) RemoveUnsafe(observation);
    }

    public void Invalidate(string scope, nint window)
    {
        lock (_sync)
        {
            if (_current.Remove((scope, window), out var id)) _observations.Remove(id);
        }
    }

    public void InvalidateScope(string scope)
    {
        lock (_sync)
        {
            foreach (var pair in _current.Where(pair => StringComparer.Ordinal.Equals(pair.Key.Scope, scope)).ToArray())
            {
                _current.Remove(pair.Key);
                _observations.Remove(pair.Value);
            }
        }
    }

    private void RemoveUnsafe(ComputerUseObservation<TElement> observation)
    {
        _observations.Remove(observation.ObservationId);
        if (_current.TryGetValue((observation.Scope, observation.Window), out var current) &&
            StringComparer.Ordinal.Equals(current, observation.ObservationId))
            _current.Remove((observation.Scope, observation.Window));
    }

    private void RemoveExpiredUnsafe(DateTimeOffset now)
    {
        foreach (var observation in _observations.Values.Where(value => now - value.ObservedAt > _ttl).ToArray())
            RemoveUnsafe(observation);
    }

    private void TrimUnsafe()
    {
        while (_observations.Count > _capacity && _order.TryDequeue(out var id))
            if (_observations.TryGetValue(id, out var observation)) RemoveUnsafe(observation);
        while (_order.Count > _capacity * 4 && _order.TryDequeue(out _)) { }
    }

    private static AgentRequestException Stale(string message) => new("STALE_OBSERVATION", message);
}
