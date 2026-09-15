using System.Collections.Concurrent;

namespace JarvisCode.Core.BackgroundTasks;

/// <summary>
/// The reference's "Run in background": while a tool call is running, the host
/// can ask it to carry on off the turn. A tool that supports the move registers
/// its call here and races the token it gets back; the host asks by call id and
/// learns from the return value whether anything was listening.
/// </summary>
public sealed class BackgroundMoveRequests
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);

    /// <summary>Fired when a call is registered or released, so a host can re-evaluate its buttons.</summary>
    public event Action? Changed;

    /// <summary>Tool side: announce that this call can be moved, and take the signal to race.</summary>
    public CancellationToken Register(string callId)
    {
        var source = _pending.AddOrUpdate(callId, _ => new CancellationTokenSource(), (_, existing) =>
        {
            existing.Dispose();
            return new CancellationTokenSource();
        });
        Changed?.Invoke();
        return source.Token;
    }

    /// <summary>Tool side: the call is over (moved, finished or failed).</summary>
    public void Release(string callId)
    {
        if (_pending.TryRemove(callId, out var source))
        {
            source.Dispose();
            Changed?.Invoke();
        }
    }

    /// <summary>Host side: is a running call listening for the move?</summary>
    public bool CanMove(string callId) => _pending.ContainsKey(callId);

    /// <summary>
    /// Host side: ask the call to continue in the background. False means nothing
    /// was listening — the call had already finished, or its tool cannot move.
    /// </summary>
    public bool Request(string callId)
    {
        if (!_pending.TryGetValue(callId, out var source))
        {
            return false;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The call finished between the lookup and the cancel.
            return false;
        }

        return true;
    }
}
