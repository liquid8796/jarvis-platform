using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;

namespace JarvisCode.Core.Agent;

/// <summary>A summary written ahead of time, and the prefix of history it covers.</summary>
public sealed record PrecomputedSummary(
    CompactionResult Result,
    int PrefixLength,
    ChatMessage? LastSummarized);

/// <summary>Where an armed precomputation has got to.</summary>
public enum PrecomputeStatus
{
    Idle,
    Running,
    Ready,
    Failed,
}

/// <summary>
/// The reference's precomputed compact (its <c>XRe</c>/<c>YRe</c>): once the
/// context passes the arm point — a fifth of the window below the top — the
/// conversation so far is summarized in the background, so that crossing the
/// compaction threshold swaps in a summary that is already written instead of
/// stopping the turn to write one.
///
/// Held per session and off unless a host supplies one, because the reference
/// reaches it only behind <c>tengu_sepia_moth</c>, which ships false.
/// </summary>
public sealed class PrecomputeStore
{
    /// <summary>Consecutive failures after which the session stops arming (the reference's <c>CRt</c>).</summary>
    public const int FailureLimit = 3;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _running;
    private PrecomputedSummary? _ready;
    private int _consecutiveFailures;
    private int _attempts;
    private bool _rehydrateAttempted;

    public PrecomputeStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (_ready is not null)
                    return PrecomputeStatus.Ready;
                if (_running is not null)
                    return PrecomputeStatus.Running;
                return _consecutiveFailures > 0 ? PrecomputeStatus.Failed : PrecomputeStatus.Idle;
            }
        }
    }

    /// <summary>How many precomputations this session has started.</summary>
    public int Attempts
    {
        get { lock (_gate) { return _attempts; } }
    }

    public int ConsecutiveFailures
    {
        get { lock (_gate) { return _consecutiveFailures; } }
    }

    /// <summary>
    /// The reference arms only when nothing is outstanding: a run in flight or a
    /// summary already written is left alone, and a session that has failed its
    /// way to the limit stops trying.
    /// </summary>
    public bool CanArm
    {
        get
        {
            lock (_gate)
            {
                return _running is null && _ready is null && _consecutiveFailures < FailureLimit;
            }
        }
    }

    /// <summary>
    /// Starts one precomputation over <paramref name="snapshot"/>. The summary is
    /// kept until it is taken or the history it covers is rewritten; a failure is
    /// counted and never surfaces to the turn, which simply compacts inline.
    /// </summary>
    public void Arm(
        ILlmProvider provider,
        string modelId,
        IReadOnlyList<ChatMessage> snapshot,
        bool stripNonEssential,
        PrecompactSidecar? sidecar = null)
    {
        // Do not start passive work or disturb a cached summary when the active
        // provider cannot guarantee a tool-free request.
        if (!ProviderCapabilities.For(provider).SupportsToolFreeInference)
            return;

        var messages = snapshot.ToList();
        lock (_gate)
        {
            if (_running is not null || _ready is not null || _consecutiveFailures >= FailureLimit)
                return;
            _attempts++;
            _running = RunAsync(
                provider, modelId, messages, stripNonEssential, sidecar, _cancellation.Token);
        }
    }

    /// <summary>
    /// The reference's <c>MRt</c>: once per session, a summary a previous
    /// process left behind is read and taken up while it still describes this
    /// conversation. Anything it cannot use is deleted rather than left to be
    /// refused again, and a session only ever tries this once.
    /// </summary>
    public void RehydrateOnce(PrecompactSidecar? sidecar, IReadOnlyList<ChatMessage> messages)
    {
        if (sidecar is null)
            return;

        lock (_gate)
        {
            if (_rehydrateAttempted || _running is not null || _ready is not null)
                return;
            _rehydrateAttempted = true;
        }

        var (summary, rejection) = sidecar.Read(messages, DateTimeOffset.UtcNow);
        if (summary is null)
        {
            if (rejection is not SidecarRejection.Absent)
            {
                sidecar.Delete();
                Utilities.DiagnosticLog.Write(
                    $"precomputed compact: rehydrate rejected ({Reason(rejection)})");
            }

            return;
        }

        lock (_gate)
        {
            if (_running is not null || _ready is not null)
                return;
            _ready = summary;
        }

        Utilities.DiagnosticLog.Write(
            $"precomputed compact: rehydrated ({summary.PrefixLength} messages summarized)");
    }

    private static string Reason(SidecarRejection? rejection) => rejection switch
    {
        SidecarRejection.TooLarge => "too_large",
        SidecarRejection.ParseError => "parse_error",
        SidecarRejection.Version => "version",
        SidecarRejection.SessionMismatch => "session_mismatch",
        SidecarRejection.ModelMismatch => "model_mismatch",
        SidecarRejection.BadTimestamp => "bad_timestamp",
        SidecarRejection.TooOld => "too_old",
        SidecarRejection.BoundaryMissing => "boundary_missing",
        SidecarRejection.GrewTooMuch => "grew_too_much",
        SidecarRejection.ShrankTooMuch => "shrank_too_much",
        SidecarRejection.PreserveMissing => "preserve_uuid_missing",
        _ => "absent",
    };

    /// <summary>Stops an outstanding precomputation — a session closing, a turn abandoned.</summary>
    public void Cancel()
    {
        Invalidate();
        _cancellation.Cancel();
    }

    private async Task RunAsync(
        ILlmProvider provider,
        string modelId,
        List<ChatMessage> snapshot,
        bool stripNonEssential,
        PrecompactSidecar? sidecar,
        CancellationToken cancellationToken)
    {
        // Yield first so Arm returns to the turn before any request goes out.
        await Task.Yield();
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        long preCompactTokens = Utilities.TokenEstimator.Estimate(snapshot);
        try
        {
            var result = await new ConversationCompactor().CompactAsync(
                provider, modelId, snapshot, cancellationToken, stripNonEssential: stripNonEssential);
            int prefix = result.Archived.Count;
            var summary = new PrecomputedSummary(
                result, prefix, prefix > 0 ? snapshot[prefix - 1] : null);
            lock (_gate)
            {
                _ready = summary;
                _consecutiveFailures = 0;
                _running = null;
            }

            long readyMs = (long)System.Diagnostics.Stopwatch
                .GetElapsedTime(startedAt).TotalMilliseconds;
            Utilities.DiagnosticLog.Write(
                $"precomputed compact: ready ({snapshot.Count} msgs, {prefix} summarized)");

            if (sidecar is not null)
            {
                var written = sidecar.Write(summary, snapshot, preCompactTokens, readyMs);
                Utilities.DiagnosticLog.Write(written.Ok
                    ? $"precomputed compact: persisted ({written.Bytes} bytes)"
                    : $"precomputed compact: persisted ({written.Reason}" +
                      $"{(written.Detail is null ? "" : ": " + written.Detail)})");
            }
        }
        // Deliberately catch-all: this runs off the turn, so an exception here
        // has no caller to reach and would only surface as an unobserved task.
        // The turn simply compacts inline instead.
        catch (Exception ex)
        {
            lock (_gate)
            {
                _consecutiveFailures = ex is ProviderException { CanRetry: false }
                    ? FailureLimit : _consecutiveFailures + 1;
                _running = null;
            }

            Utilities.DiagnosticLog.Write(
                $"precomputed compact: failed ({ex.Message}) — {_consecutiveFailures} in a row");
        }
    }

    /// <summary>
    /// Takes the ready summary when it still describes the head of
    /// <paramref name="messages"/>. Anything that rewrote history since it was
    /// written — a compaction, a rewind — leaves it unusable, and it is dropped
    /// rather than pasted over a conversation it no longer matches.
    /// </summary>
    public PrecomputedSummary? TakeReady(
        IReadOnlyList<ChatMessage> messages, PrecompactSidecar? sidecar = null)
    {
        lock (_gate)
        {
            if (_ready is not { } ready)
                return null;

            // The stored copy exists to survive a restart; once the summary is
            // in hand it has nothing left to do, taken up or not.
            sidecar?.Delete();

            _ready = null;
            bool describesHead =
                ready.PrefixLength <= messages.Count &&
                (ready.LastSummarized is null ||
                    ReferenceEquals(messages[ready.PrefixLength - 1], ready.LastSummarized));
            if (describesHead)
                return ready;

            Utilities.DiagnosticLog.Write(
                "precomputed compact: discarded — the history it covered has moved");
            return null;
        }
    }

    /// <summary>A compaction rewrote history, so anything precomputed is stale.</summary>
    public void Invalidate(PrecompactSidecar? sidecar = null)
    {
        sidecar?.Delete();
        lock (_gate)
        {
            _ready = null;
        }
    }

    /// <summary>
    /// Folds a precomputed summary into the conversation it was written for:
    /// the summary stands where the prefix was, and everything added since it
    /// was written is kept verbatim.
    /// </summary>
    public static CompactionResult Apply(
        PrecomputedSummary summary, IReadOnlyList<ChatMessage> messages)
    {
        var kept = messages.Skip(summary.PrefixLength).ToList();
        var compacted = new List<ChatMessage> { summary.Result.Messages[0] };
        compacted.AddRange(kept);
        return summary.Result with
        {
            Messages = compacted,
            Archived = [.. messages.Take(summary.PrefixLength)],
        };
    }
}
