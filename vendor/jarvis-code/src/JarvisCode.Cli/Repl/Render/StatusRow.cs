namespace JarvisCode.Cli.Repl.Render;

/// <summary>What the turn is doing, in the reference's own vocabulary (its <c>mode</c>).</summary>
internal enum TurnPhase
{
    Requesting,
    Responding,
    ToolInput,
    ToolUse,
    Thinking,
}

/// <summary>Why the turn is waiting, when it is (the reference's <c>retryStatus.kind</c>).</summary>
internal enum RetryKind
{
    None,

    /// <summary>No bytes have come back; the reference calls this <c>stalled</c>.</summary>
    Stalled,

    /// <summary>A low-priority request waiting its turn.</summary>
    LowPriorityWaiting,

    /// <summary>A failed call being retried.</summary>
    Error,
}

internal sealed record RetryStatus(
    RetryKind Kind,
    TimeSpan Remaining,
    int Attempt = 0,
    int MaxRetries = 0,
    string? Message = null,
    string? Banner = null,
    string? RateLimitType = null,
    DateTimeOffset? ResetsAt = null,
    bool IsNetworkDown = false,
    bool IsSslError = false);

/// <summary>
/// The live status row (CLI 2.1.257, its <c>Wo</c>/<c>Oit</c>): a spinner frame,
/// the turn's verb, and a parenthesised cluster of whatever else there is to
/// say — the elapsed timer, the token count with the direction arrow the
/// reference puts in front of it, and the thinking word — joined by its own
/// <c>" · "</c>. While the turn is retrying, the row is replaced by the retry
/// ladder instead.
/// </summary>
internal static class StatusRow
{
    /// <summary>The reference's <c>ri</c>.</summary>
    public const string Separator = " · ";

    /// <summary>The reference's <c>ii</c>: past this the stats show even with nothing else to report.</summary>
    public static readonly TimeSpan StatsAfter = TimeSpan.FromMilliseconds(16000);

    /// <summary>The reference's frame period for its four-frame animation.</summary>
    public static readonly TimeSpan FramePeriod = TimeSpan.FromMilliseconds(120);

    public static string Frame(TimeSpan elapsed, bool reducedMotion = false)
    {
        if (reducedMotion)
        {
            return Glyphs.SpinnerFrames[0];
        }

        int index = (int)(elapsed.Ticks / FramePeriod.Ticks) % Glyphs.SpinnerFrames.Count;
        return Glyphs.SpinnerFrames[Math.Abs(index)];
    }

    /// <summary>The reference's <c>$o</c>: which way the tokens are flowing.</summary>
    public static string DirectionArrow(TurnPhase phase) =>
        phase == TurnPhase.Requesting ? Glyphs.ArrowUp : Glyphs.ArrowDown;

    /// <summary>
    /// The row's text, without colour. <paramref name="verb"/> is the spinner
    /// word, and the cluster is dropped whole when there is nothing in it.
    /// </summary>
    public static string Render(
        string verb,
        TurnPhase phase,
        TimeSpan elapsed,
        long totalTokens,
        bool thinking,
        bool verbose = false,
        string? suffix = null,
        bool reducedMotion = false)
    {
        var parts = new List<string>(4);
        bool showStats = verbose || thinking || totalTokens > 0 || elapsed > StatsAfter;
        if (suffix is { Length: > 0 })
        {
            parts.Add(suffix);
        }

        if (showStats)
        {
            parts.Add(Format.Duration(elapsed));
            if (totalTokens > 0)
            {
                parts.Add($"{DirectionArrow(phase)}{Format.Tokens(totalTokens)} tokens");
            }
        }

        if (thinking)
        {
            parts.Add("thinking");
        }

        var head = $"{Frame(elapsed, reducedMotion)} {verb}…";
        return parts.Count == 0 ? head : $"{head} ({string.Join(Separator, parts)})";
    }

    /// <summary>
    /// The reference's <c>Oit</c>: what the row says instead while a call is
    /// waiting or being retried. Each sentence and each separator is its own.
    /// </summary>
    public static string RenderRetry(RetryStatus status)
    {
        // The reference rounds the remaining time up to the second and drops to
        // its most-significant unit past five minutes.
        var remaining = TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling(status.Remaining.TotalSeconds)));
        var time = Format.Duration(remaining, mostSignificantOnly: remaining.TotalMilliseconds >= 300000);
        switch (status.Kind)
        {
            case RetryKind.Stalled:
                return $"{Glyphs.Star} Waiting for API response{Separator}will retry in {time}{Separator}check your network";

            case RetryKind.LowPriorityWaiting:
                return $"{Glyphs.Star} {status.Banner ?? ""}{Separator}next try in {time}{Separator}" +
                       $"attempt {status.Attempt}{Separator}esc to interrupt";

            case RetryKind.Error:
                var resets = status.ResetsAt is { } at ? $" ({Format.ClockTime(at, DateTimeOffset.Now)})" : "";
                var label = ErrorLabel(status);
                return $"{Glyphs.Star} {label}{Separator}Retrying in {time}{resets}{Separator}" +
                       $"attempt {status.Attempt}/{status.MaxRetries}";

            default:
                return "";
        }
    }

    /// <summary>
    /// The reference's label choice: a plain "API error" until the third attempt
    /// (or a network/SSL/rate-limit failure, which it names at once), then the
    /// rate-limit's own name, then the formatted error.
    /// </summary>
    internal static string ErrorLabel(RetryStatus status)
    {
        bool explain = status.Attempt >= Math.Min(3, status.MaxRetries) ||
                       status.IsNetworkDown || status.IsSslError || status.ResetsAt is not null ||
                       status.RateLimitType is not null;
        if (!explain)
        {
            return "API error";
        }

        if (status.RateLimitType is { Length: > 0 } || status.ResetsAt is not null)
        {
            var name = status.RateLimitType is { Length: > 0 } type ? type : "usage limit";
            return char.ToUpperInvariant(name[0]) + name[1..] + " reached";
        }

        return status.Message ?? "API error";
    }

    /// <summary>
    /// The reference's compaction row: while a compaction runs the spinner's
    /// word is replaced and a progress bar rides under it.
    /// </summary>
    public const string CompactingMessage = "Compacting conversation";

    /// <summary>The reference's interrupted row, written where the answer would have been.</summary>
    public const string InterruptedRow = "Interrupted" + Separator + "What should Jarvis do instead?";
}
