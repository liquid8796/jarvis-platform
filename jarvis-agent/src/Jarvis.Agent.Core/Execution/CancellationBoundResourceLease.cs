namespace Jarvis.Agent.Core.Execution;

/// <summary>
/// Binds the call-owned reference of a resource lease to that call's cancellation without changing
/// the lifetime of references explicitly retained by long-running owned work.
/// </summary>
internal sealed class CancellationBoundResourceLease : IExecutionResourceLease
{
    private IExecutionResourceLease? _inner;
    private CancellationTokenRegistration _cancellation;

    private CancellationBoundResourceLease(IExecutionResourceLease inner, CancellationToken cancellationToken)
    {
        _inner = inner;
        _cancellation = cancellationToken.UnsafeRegister(static state =>
            ThreadPool.UnsafeQueueUserWorkItem(static queued => ((CancellationBoundResourceLease)queued!).Cancel(), state, preferLocal: false), this);
        if (Volatile.Read(ref _inner) is null) _cancellation.Unregister();
    }

    public static IExecutionResourceLease Bind(IExecutionResourceLease inner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return cancellationToken.CanBeCanceled ? new CancellationBoundResourceLease(inner, cancellationToken) : inner;
    }

    public IDisposable Retain() =>
        (Volatile.Read(ref _inner) ?? throw new ObjectDisposedException(nameof(CancellationBoundResourceLease))).Retain();

    private void Cancel() => Interlocked.Exchange(ref _inner, null)?.Dispose();

    public void Dispose()
    {
        _cancellation.Unregister();
        Cancel();
    }
}
