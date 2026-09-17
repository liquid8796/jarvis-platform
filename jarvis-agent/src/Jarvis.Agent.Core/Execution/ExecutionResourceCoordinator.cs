using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Execution;

public interface IExecutionResourceLease : IDisposable
{
    IDisposable Retain();
}

/// <summary>
/// Cooperative exclusion between Jarvis operations, not an OS sandbox. Known file subtrees have
/// shared reads/exclusive writes. Unknown command effects exclude machine resources conservatively;
/// owned job I/O and the separate session/cancellation control lane remain reachable.
/// </summary>
public sealed class ExecutionResourceCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly List<Held> _held = [];
    private readonly LinkedList<Waiter> _waiting = [];
    private int _maximumQueued;
    private bool _disposed;
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public ExecutionResourceCoordinator(int maximumQueued = 100)
    {
        if (maximumQueued < 0) throw new ArgumentOutOfRangeException(nameof(maximumQueued));
        _maximumQueued = maximumQueued;
    }
    public void Configure(int maximumQueued)
    {
        if (maximumQueued < 0) throw new ArgumentOutOfRangeException(nameof(maximumQueued));
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); _maximumQueued = maximumQueued; }
    }

    public Task<IDisposable> AcquireAsync(string owner, IReadOnlyList<string> resources, bool exclusive, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("An execution owner is required.");
        if (resources.Count > 32 || resources.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("At most 32 nonempty resource claims are allowed.");
        var claims = resources.Select(Normalize).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<IDisposable>(cancellationToken);
            if (!_held.Any(held => Conflicts(held.Resources, held.Exclusive, claims, exclusive)) &&
                !_waiting.Any(waiter => Conflicts(waiter.Resources, waiter.Exclusive, claims, exclusive)))
                return Task.FromResult<IDisposable>(Grant(owner, claims, exclusive));
            if (_waiting.Count >= _maximumQueued)
                return Task.FromException<IDisposable>(new AgentRequestException("RESOURCE_BUSY", "The requested resource is held by other work and the resource queue is full. No tool was started."));
            var pending = new Waiter(owner, claims, exclusive, cancellationToken);
            pending.Node = _waiting.AddLast(pending);
            pending.Registration = cancellationToken.UnsafeRegister(_ => Cancel(pending), null);
            if (pending.Node is null) pending.Registration.Unregister();
            return pending.Completion.Task;
        }
    }

    public SessionResourceState GetSessionState(string owner)
    {
        lock (_sync) return new(
            _held.Where(held => held.Owner == owner).SelectMany(held => held.Resources).Distinct().ToArray(),
            _waiting.Where(waiter => waiter.Owner == owner).SelectMany(waiter => waiter.Resources).Distinct().ToArray(),
            _waiting.Count(waiter => waiter.Owner == owner));
    }

    private Lease Grant(string owner, string[] resources, bool exclusive)
    {
        var held = new Held(owner, resources, exclusive);
        _held.Add(held);
        return new(this, held);
    }
    private IDisposable Retain(Held held)
    {
        lock (_sync)
        {
            if (held.References <= 0) throw new ObjectDisposedException(nameof(IExecutionResourceLease));
            checked { held.References++; }
            return new Lease(this, held);
        }
    }
    private void Release(Held held)
    {
        lock (_sync)
        {
            if (--held.References != 0) return;
            _held.Remove(held);
            if (!_disposed) Pump();
        }
    }
    private void Pump()
    {
        var earlier = new List<Waiter>();
        for (var node = _waiting.First; node is not null;)
        {
            var next = node.Next;
            var waiter = node.Value;
            if (waiter.Cancellation.IsCancellationRequested)
            {
                Remove(waiter);
                waiter.Completion.TrySetCanceled(waiter.Cancellation);
            }
            else if (!_held.Any(held => Conflicts(held.Resources, held.Exclusive, waiter.Resources, waiter.Exclusive)) &&
                     !earlier.Any(previous => Conflicts(previous.Resources, previous.Exclusive, waiter.Resources, waiter.Exclusive)))
            {
                Remove(waiter);
                waiter.Completion.TrySetResult(Grant(waiter.Owner, waiter.Resources, waiter.Exclusive));
            }
            else earlier.Add(waiter);
            node = next;
        }
    }
    private void Cancel(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.Node is null) return;
            Remove(waiter);
            waiter.Completion.TrySetCanceled(waiter.Cancellation);
            if (!_disposed) Pump();
        }
    }
    private void Remove(Waiter waiter)
    {
        if (waiter.Node is not null) _waiting.Remove(waiter.Node);
        waiter.Node = null;
        waiter.Registration.Unregister();
    }
    private static bool Conflicts(string[] first, bool firstExclusive, string[] second, bool secondExclusive) =>
        (firstExclusive || secondExclusive) && first.Any(a => second.Any(b => Overlaps(a, b)));
    private static bool Overlaps(string a, string b)
    {
        if (a == "*" || b == "*")
        {
            var other = a == "*" ? b : a;
            return !other.StartsWith("job|", StringComparison.Ordinal) && !other.StartsWith("coordination|", StringComparison.Ordinal);
        }
        if (a.Equals(b, Comparison)) return true;
        if (!a.StartsWith("fs|", StringComparison.Ordinal) || !b.StartsWith("fs|", StringComparison.Ordinal)) return false;
        return a.StartsWith(b + "/", Comparison) || b.StartsWith(a + "/", Comparison);
    }
    private static string Normalize(string resource) => resource.StartsWith("fs|", StringComparison.Ordinal)
        ? resource.Replace('\\', '/').TrimEnd('/') : resource;
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var waiter in _waiting.ToArray())
            {
                Remove(waiter);
                waiter.Completion.TrySetException(new ObjectDisposedException(nameof(ExecutionResourceCoordinator)));
            }
        }
    }
    private sealed class Held(string owner, string[] resources, bool exclusive)
    {
        public string Owner { get; } = owner;
        public string[] Resources { get; } = resources;
        public bool Exclusive { get; } = exclusive;
        public int References = 1;
    }
    private sealed class Lease(ExecutionResourceCoordinator owner, Held held) : IExecutionResourceLease
    {
        private ExecutionResourceCoordinator? _owner = owner;
        public IDisposable Retain() => (Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(Lease))).Retain(held);
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(held);
    }
    private sealed class Waiter(string owner, string[] resources, bool exclusive, CancellationToken cancellation)
    {
        public string Owner { get; } = owner;
        public string[] Resources { get; } = resources;
        public bool Exclusive { get; } = exclusive;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
        public CancellationTokenRegistration Registration;
    }
}

public sealed record SessionResourceState(IReadOnlyList<string> Held, IReadOnlyList<string> WaitingFor, int WaitingCalls);
