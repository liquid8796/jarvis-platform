using JarvisCode.App.Infrastructure;
using JarvisCode.App.Services;

namespace JarvisCode.App.ViewModels;

/// <summary>What the Chat surface's waiting line shows right now.</summary>
/// <param name="Label">The sentence, or null when the line is not shown at all.</param>
/// <param name="Progress">Compaction percentage, or null when this is not the compaction state.</param>
public readonly record struct ChatStatusView(string? Label, int? Progress)
{
    public static ChatStatusView Hidden => default;

    public bool Visible => Label is not null;
}

/// <summary>
/// The Chat surface's turn state, as the reference's chat page keeps it
/// (<c>ca2ef848d-D8BWZk64.js</c>, its <c>Lw</c>): the line appears five seconds into
/// a wait, escalates twice, is replaced by a transport retry notice while one is
/// pending, and gives way to the compaction indicator while history is being
/// summarized. It goes away the moment the answer starts arriving — the reference
/// gates it on the streaming message's content length (its <c>jP</c>) rather than on
/// the turn being over.
///
/// <see cref="Describe"/> is the whole decision and takes the clock as an argument,
/// so it is testable without a running turn; <see cref="Controls.ChatStatusLine"/>
/// only draws what it returns.
/// </summary>
public sealed class ChatTurnStatus : ObservableObject
{
    private DateTimeOffset? _startedAt;
    private bool _hasContent;
    private int _pick;
    private int _retryCount;
    private ProviderRetryState? _retry;
    private DateTimeOffset? _compactingSince;
    private bool _compactionComplete;
    private int _compactionProgress;

    /// <summary>A retry the transport has scheduled, and when its wait runs out.</summary>
    /// <param name="Cause">The classified failure.</param>
    /// <param name="Attempt">1-based attempt about to be made.</param>
    /// <param name="MaxAttempts">How many the transport will make in all.</param>
    /// <param name="ReadyAt">When the wait ends, for the countdown.</param>
    public sealed record ProviderRetryState(
        ChatRetryCause Cause, int Attempt, int MaxAttempts, DateTimeOffset ReadyAt);

    /// <summary>Whether a turn is in flight at all.</summary>
    public bool Running => _startedAt is not null;

    /// <summary>Starts a turn. <paramref name="pick"/> is the entry drawn from the five-second list.</summary>
    public void BeginTurn(DateTimeOffset now, int pick)
    {
        _startedAt = now;
        _hasContent = false;
        _pick = pick;
        _retry = null;
        _retryCount = 0;
        _compactingSince = null;
        _compactionComplete = false;
        _compactionProgress = 0;
        Changed();
    }

    /// <summary>Starts a turn, drawing the five-second label the way the reference does.</summary>
    public void BeginTurn(DateTimeOffset now) =>
        BeginTurn(now, Random.Shared.Next(ChatStatusLabels.FirstPicks.Count));

    /// <summary>The answer has started arriving: the waiting line stands down.</summary>
    public void OnContent()
    {
        if (_hasContent)
        {
            return;
        }

        _hasContent = true;
        Changed();
    }

    /// <summary>
    /// A new model call inside the same turn starts waiting again — the reference's
    /// indicator follows the streaming message, so a tool round-trip puts it back.
    /// </summary>
    public void OnAwaitingModel(DateTimeOffset now)
    {
        if (!Running || !_hasContent)
        {
            return;
        }

        _hasContent = false;
        _startedAt = now;
        Changed();
    }

    /// <summary>The transport reported a scheduled retry.</summary>
    public void OnRetry(ChatRetryCause cause, int attempt, int maxAttempts, TimeSpan delay, DateTimeOffset now)
    {
        _retry = new ProviderRetryState(cause, attempt, maxAttempts, now + delay);
        _retryCount = attempt;
        Changed();
    }

    /// <summary>A retried call came back: the notice goes, the attempt count stays.</summary>
    public void OnRetrySettled()
    {
        if (_retry is null)
        {
            return;
        }

        _retry = null;
        Changed();
    }

    public void OnCompacting(DateTimeOffset now)
    {
        _compactingSince = now;
        _compactionComplete = false;
        _compactionProgress = 0;
        Changed();
    }

    public void OnCompacted()
    {
        if (_compactingSince is null)
        {
            return;
        }

        _compactionComplete = true;
        Changed();
    }

    public void EndTurn()
    {
        _startedAt = null;
        _retry = null;
        _compactingSince = null;
        _compactionComplete = false;
        Changed();
    }

    /// <summary>
    /// What to draw at <paramref name="now"/>. The compaction indicator wins over
    /// everything, then a pending retry, then the attempt notice, then the ladder;
    /// and the ladder alone is suppressed once content has arrived.
    /// </summary>
    public ChatStatusView Describe(DateTimeOffset now)
    {
        if (_compactingSince is { } compactingSince)
        {
            if (_compactionComplete)
            {
                return new ChatStatusView(ChatStatusLabels.Compacting, 100);
            }

            // The reference keeps the running maximum, so a clock that steps back
            // cannot make the bar retreat.
            _compactionProgress = Math.Max(
                _compactionProgress,
                ChatStatusLabels.CompactionProgress((now - compactingSince).TotalSeconds));
            return new ChatStatusView(ChatStatusLabels.Compacting, _compactionProgress);
        }

        if (_startedAt is not { } started)
        {
            return ChatStatusView.Hidden;
        }

        if (_retry is { } retry)
        {
            var seconds = (int)Math.Ceiling(Math.Max(0, (retry.ReadyAt - now).TotalSeconds));
            return new ChatStatusView(
                ChatStatusLabels.RetryLabel(retry.Cause, seconds, retry.Attempt, retry.MaxAttempts), null);
        }

        if (_retryCount > 0)
        {
            return new ChatStatusView(ChatStatusLabels.TakingLongerLabel(_retryCount), null);
        }

        return _hasContent
            ? ChatStatusView.Hidden
            : new ChatStatusView(ChatStatusLabels.Ladder((now - started).TotalSeconds, _pick), null);
    }

    private void Changed() => OnPropertyChanged(nameof(Running));
}
