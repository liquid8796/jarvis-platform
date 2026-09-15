namespace Jarvis.Agent.Core;

public enum AgentLifecycleKind
{
    Interrupt,
    Stop,
    SubagentStop
}

public sealed record AgentLifecycleEvent(
    AgentLifecycleKind Kind,
    string? ThreadId = null,
    string? TurnId = null,
    string? CallId = null,
    string? Reason = null);

/// <summary>Local lifecycle fan-out. Listener failures never block cleanup in other components.</summary>
public sealed class AgentLifecycleHub
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Func<AgentLifecycleEvent, CancellationToken, Task>> _subscribers = [];

    public IDisposable Subscribe(Func<AgentLifecycleEvent, CancellationToken, Task> subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        var id = Guid.NewGuid();
        lock (_sync) _subscribers.Add(id, subscriber);
        return new Subscription(this, id);
    }

    public async Task NotifyAsync(AgentLifecycleEvent evt, CancellationToken cancellationToken = default)
    {
        Func<AgentLifecycleEvent, CancellationToken, Task>[] subscribers;
        lock (_sync) subscribers = [.. _subscribers.Values];
        foreach (var subscriber in subscribers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await subscriber(evt, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* Lifecycle cleanup is best-effort per subscriber; continue fan-out. */ }
        }
    }

    private void Unsubscribe(Guid id)
    {
        lock (_sync) _subscribers.Remove(id);
    }

    private sealed class Subscription(AgentLifecycleHub owner, Guid id) : IDisposable
    {
        private AgentLifecycleHub? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
    }
}
