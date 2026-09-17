using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Execution;

/// <summary>
/// Bounded round-robin admission. A configuration decrease drains running work without revoking
/// leases; cancelled and expired waiters never consume an execution slot. No thread is blocked.
/// </summary>
public sealed class FairExecutionScheduler : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, SessionQueue> _queues = new(StringComparer.Ordinal);
    private readonly LinkedList<SessionQueue> _roundRobin = [];
    private readonly Dictionary<string, int> _runningBySession = new(StringComparer.Ordinal);
    private int _maximum, _maximumQueued, _running, _queued;
    private TimeSpan _queueTimeout;
    private bool _disposed;

    public FairExecutionScheduler(int maximum, int maximumQueued, TimeSpan queueTimeout)
    {
        Validate(maximum, maximumQueued, queueTimeout);
        _maximum = maximum; _maximumQueued = maximumQueued; _queueTimeout = queueTimeout;
    }
    public int RunningCount { get { lock (_sync) return _running; } }
    public int QueuedCount { get { lock (_sync) return _queued; } }
    public int Maximum { get { lock (_sync) return _maximum; } }

    public void Configure(int maximum, int maximumQueued, TimeSpan queueTimeout)
    {
        Validate(maximum, maximumQueued, queueTimeout);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _maximum = maximum; _maximumQueued = maximumQueued; _queueTimeout = queueTimeout;
            Pump();
        }
    }

    public Task<IDisposable> AcquireAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session identity is required.", nameof(sessionId));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<IDisposable>(cancellationToken);
            if (_queued == 0 && _running < _maximum) return Task.FromResult<IDisposable>(Grant(sessionId));
            if (_queued >= _maximumQueued)
                return Task.FromException<IDisposable>(new AgentRequestException("QUEUE_FULL", "The bounded execution queue is full. No tool was started."));
            if (!_queues.TryGetValue(sessionId, out var queue))
            {
                queue = new SessionQueue(sessionId);
                queue.Rotation = _roundRobin.AddLast(queue);
                _queues.Add(sessionId, queue);
            }
            var waiter = new Waiter(queue, cancellationToken);
            waiter.Node = queue.Waiters.AddLast(waiter);
            _queued++;
            waiter.Registration = cancellationToken.UnsafeRegister(_ => Cancel(waiter, timeout: false), null);
            if (waiter.Node is not null)
                waiter.Timer = new Timer(_ => Cancel(waiter, timeout: true), null, _queueTimeout, Timeout.InfiniteTimeSpan);
            else waiter.Registration.Unregister();
            return waiter.Completion.Task;
        }
    }

    public SessionScheduleCounts GetSessionCounts(string sessionId)
    {
        lock (_sync) return new(_runningBySession.GetValueOrDefault(sessionId), _queues.TryGetValue(sessionId, out var queue) ? queue.Waiters.Count : 0);
    }

    private Lease Grant(string sessionId)
    {
        _running++;
        _runningBySession[sessionId] = _runningBySession.GetValueOrDefault(sessionId) + 1;
        return new Lease(this, sessionId);
    }
    private void Release(string sessionId)
    {
        lock (_sync)
        {
            _running--;
            var count = _runningBySession[sessionId] - 1;
            if (count == 0) _runningBySession.Remove(sessionId); else _runningBySession[sessionId] = count;
            if (!_disposed) Pump();
        }
    }
    private void Pump()
    {
        while (_running < _maximum && _roundRobin.First is { } next)
        {
            var queue = next.Value;
            var waiter = queue.Waiters.First!.Value;
            Remove(waiter);
            if (queue.Rotation is not null)
            {
                _roundRobin.Remove(queue.Rotation);
                queue.Rotation = _roundRobin.AddLast(queue);
            }
            if (waiter.Cancellation.IsCancellationRequested)
                waiter.Completion.TrySetCanceled(waiter.Cancellation);
            else waiter.Completion.TrySetResult(Grant(queue.SessionId));
        }
    }
    private void Cancel(Waiter waiter, bool timeout)
    {
        lock (_sync)
        {
            if (waiter.Node is null) return;
            Remove(waiter);
            if (!timeout || waiter.Cancellation.IsCancellationRequested) waiter.Completion.TrySetCanceled(waiter.Cancellation);
            else waiter.Completion.TrySetException(new AgentRequestException("QUEUE_TIMEOUT", "The request expired while waiting for an execution slot. No tool was started."));
            if (!_disposed) Pump();
        }
    }
    private void Remove(Waiter waiter)
    {
        if (waiter.Node is null) return;
        var queue = waiter.Queue;
        queue.Waiters.Remove(waiter.Node); waiter.Node = null; _queued--;
        waiter.Timer?.Dispose(); waiter.Timer = null;
        // Unregister is deliberately non-blocking: a cancelling callback may be waiting for _sync.
        waiter.Registration.Unregister();
        if (queue.Waiters.Count == 0)
        {
            _queues.Remove(queue.SessionId);
            if (queue.Rotation is not null) _roundRobin.Remove(queue.Rotation);
            queue.Rotation = null;
        }
    }
    private static void Validate(int maximum, int queued, TimeSpan timeout)
    {
        if (maximum < 1 || queued < 0 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(maximum), "Concurrency must be positive, queue capacity nonnegative and queue timeout within one day.");
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var waiter in _queues.Values.SelectMany(q => q.Waiters).ToArray())
            {
                Remove(waiter);
                waiter.Completion.TrySetException(new ObjectDisposedException(nameof(FairExecutionScheduler)));
            }
        }
    }
    private sealed class Lease(FairExecutionScheduler owner, string sessionId) : IDisposable
    {
        private FairExecutionScheduler? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(sessionId);
    }
    private sealed class SessionQueue(string sessionId)
    {
        public string SessionId { get; } = sessionId;
        public LinkedList<Waiter> Waiters { get; } = [];
        public LinkedListNode<SessionQueue>? Rotation;
    }
    private sealed class Waiter(SessionQueue queue, CancellationToken cancellation)
    {
        public SessionQueue Queue { get; } = queue;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
        public CancellationTokenRegistration Registration;
        public Timer? Timer;
    }
}

public sealed record SessionScheduleCounts(int Running, int Queued);
