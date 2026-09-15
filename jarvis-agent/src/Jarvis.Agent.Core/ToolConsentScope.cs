namespace Jarvis.Agent.Core;

/// <summary>
/// Carries local consent through this invocation's async/STA calls, never a process-wide switch.
/// Detached continuations cannot retain consent after the invocation is disposed or cancelled.
/// </summary>
public static class ToolConsentScope
{
    private static readonly AsyncLocal<Lease?> Current = new();
    public static bool IsFullPermission => Current.Value is { } lease && lease.IsActive;
    public static IDisposable Enter(bool fullPermission, CancellationToken cancellationToken = default)
    {
        var lease = new Lease(Current.Value, fullPermission, cancellationToken);
        Current.Value = lease;
        return lease;
    }
    private sealed class Lease(Lease? parent, bool fullPermission, CancellationToken cancellationToken) : IDisposable
    {
        private int _active = 1;
        public bool IsActive => Volatile.Read(ref _active) == 1 && fullPermission && !cancellationToken.IsCancellationRequested;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            if (ReferenceEquals(Current.Value, this)) Current.Value = parent;
        }
    }
}
