namespace JarvisCode.Core.Providers;

/// <summary>Why a provider call is being tried again.</summary>
public enum ProviderRetryCause
{
    /// <summary>HTTP 429: the account is over its rate limit.</summary>
    RateLimit,

    /// <summary>HTTP 529 or a 5xx: the vendor is overloaded or broken.</summary>
    Overloaded,

    /// <summary>The credential was refused.</summary>
    AuthenticationFailed,

    /// <summary>A network error, a timeout, or any other retried status.</summary>
    RequestFailed,
}

/// <summary>One scheduled retry: what went wrong, which attempt this is, and how long the wait is.</summary>
/// <param name="Cause">The classified failure.</param>
/// <param name="Attempt">1-based attempt number about to be made.</param>
/// <param name="MaxAttempts">How many the transport will make in all.</param>
/// <param name="Delay">How long the transport waits before that attempt.</param>
/// <param name="ReceivedAt">When the failure came back, so a countdown can be rendered.</param>
public sealed record ProviderRetryNotice(
    ProviderRetryCause Cause,
    int Attempt,
    int MaxAttempts,
    TimeSpan Delay,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Where the HTTP transports say that a call failed and is being tried again, so a
/// host can show it. The reference desktop renders exactly this under the composer
/// ("Rate limit reached. Retrying in 3s (attempt 2 of 5)") instead of leaving the
/// user in front of a line that says nothing while the transport works.
///
/// The sink is <see cref="AsyncLocal{T}"/> rather than a static event because a host
/// may run several turns at once and each notice belongs to the turn that provoked
/// it; the flow of an async call carries it to the right one without the transports
/// having to know that sessions exist.
/// </summary>
public static class ProviderRetries
{
    private static readonly AsyncLocal<Action<ProviderRetryNotice>?> SinkSlot = new();

    /// <summary>Routes retry notices raised inside this async flow to <paramref name="sink"/> until disposed.</summary>
    public static IDisposable Observe(Action<ProviderRetryNotice> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var previous = SinkSlot.Value;
        SinkSlot.Value = sink;
        return new Scope(previous);
    }

    /// <summary>Reports a retry to whatever is observing this flow. Never throws.</summary>
    public static void Report(ProviderRetryNotice notice)
    {
        var sink = SinkSlot.Value;
        if (sink is null)
        {
            return;
        }

        try
        {
            sink(notice);
        }
        catch
        {
            // A host that cannot render a notice must not break the call it describes.
        }
    }

    private sealed class Scope(Action<ProviderRetryNotice>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SinkSlot.Value = previous;
        }
    }
}
