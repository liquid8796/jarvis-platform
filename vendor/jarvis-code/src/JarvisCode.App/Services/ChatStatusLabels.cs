namespace JarvisCode.App.Services;

/// <summary>Why the provider is being retried, as the reference names the four causes.</summary>
public enum ChatRetryCause
{
    /// <summary>HTTP 429 — "Rate limit reached".</summary>
    RateLimit,

    /// <summary>The vendor is overloaded (529/5xx) — "Server is busy".</summary>
    Overloaded,

    /// <summary>The credential was refused — "Authentication failed".</summary>
    AuthenticationFailed,

    /// <summary>Anything else the transport retries — "Request failed".</summary>
    RequestFailed,
}

/// <summary>
/// The Chat surface's own waiting line, ported from the reference desktop's chat
/// page (<c>ca2ef848d-D8BWZk64.js</c>, its <c>Pw</c> message table, <c>Fw</c> pick
/// list, <c>Lw</c> timers and <c>Ow</c> compaction indicator — found by the message
/// id <c>C6UYRcZHCt</c>).
///
/// This is not the Code surface's ladder (<see cref="Controls.TurnStatusLine"/>):
/// the reference runs a different component on each surface, and they share neither
/// their labels, their timings nor their layout. Chat's shows one sentence while the
/// answer has not started arriving, and nothing else — no elapsed clock, no token
/// counter, no spinner.
/// </summary>
public static class ChatStatusLabels
{
    /// <summary>The first label appears five seconds in (the reference's 5e3 timer).</summary>
    public const int FirstLabelSeconds = 5;

    /// <summary>"Still working on it" takes over at fifteen seconds (15e3).</summary>
    public const int LongSeconds = 15;

    /// <summary>"A bit longer" takes over at thirty seconds (3e4).</summary>
    public const int LongestSeconds = 30;

    public const string Gathering = "Gathering my thoughts, be right there...";   // C6UYRcZHCt
    public const string Contemplating = "Contemplating, stand by...";             // r6P7kLapjf
    public const string Pondering = "Pondering, stand by...";                     // l0/dX2gLlN
    public const string Ruminating = "Ruminating on it, stand by...";             // wt2OTGsj0k
    public const string Long = "Still working on it, stand by...";                // R9hnARYgER
    public const string Longest = "A bit longer, thanks for your patience...";    // MCt5e8xpx8

    public const string RateLimitReached = "Rate limit reached";                  // jd5qR1RcUF
    public const string ServerBusy = "Server is busy";                            // oMOLgYnZwB
    public const string AuthenticationFailed = "Authentication failed";           // 7+UPMnhJCX
    public const string RequestFailed = "Request failed";                         // iMGl038nIV

    /// <summary>The compaction line under the transcript while history is summarized.</summary>
    public const string Compacting = "Compacting our conversation so we can keep chatting..."; // TtfNhgtojm

    /// <summary>
    /// The five-second pick list. "pondering" is in it twice, so it comes up twice
    /// as often as its neighbours — the reference's own array, not a typo of ours.
    /// </summary>
    public static readonly IReadOnlyList<string> FirstPicks =
        [Gathering, Contemplating, Pondering, Ruminating, Pondering];

    /// <summary>
    /// The label for a turn that has been waiting <paramref name="elapsedSeconds"/>
    /// with nothing to show yet, given the entry <paramref name="pick"/> the turn drew
    /// from <see cref="FirstPicks"/> when it crossed five seconds. Null before then:
    /// the reference shows no line at all for the first five seconds.
    /// </summary>
    public static string? Ladder(double elapsedSeconds, int pick) =>
        elapsedSeconds >= LongestSeconds ? Longest
        : elapsedSeconds >= LongSeconds ? Long
        : elapsedSeconds >= FirstLabelSeconds ? FirstPicks[((pick % FirstPicks.Count) + FirstPicks.Count) % FirstPicks.Count]
        : null;

    /// <summary>The engine's classification of a retried call, in this surface's terms.</summary>
    public static ChatRetryCause CauseFrom(JarvisCode.Core.Providers.ProviderRetryCause cause) => cause switch
    {
        JarvisCode.Core.Providers.ProviderRetryCause.RateLimit => ChatRetryCause.RateLimit,
        JarvisCode.Core.Providers.ProviderRetryCause.Overloaded => ChatRetryCause.Overloaded,
        JarvisCode.Core.Providers.ProviderRetryCause.AuthenticationFailed => ChatRetryCause.AuthenticationFailed,
        _ => ChatRetryCause.RequestFailed,
    };

    /// <summary>The cause sentence a retry notice opens with.</summary>
    public static string CauseLabel(ChatRetryCause cause) => cause switch
    {
        ChatRetryCause.AuthenticationFailed => AuthenticationFailed,
        ChatRetryCause.RateLimit => RateLimitReached,
        ChatRetryCause.Overloaded => ServerBusy,
        _ => RequestFailed,
    };

    /// <summary>
    /// The retry line. The reference counts the seconds down and swaps to its
    /// "Retrying now" wording once the wait has run out.
    /// </summary>
    public static string RetryLabel(ChatRetryCause cause, int seconds, int attempt, int maxRetries)
    {
        var reason = CauseLabel(cause);
        return seconds > 0
            // L05EkQSQ0J
            ? $"{reason}. Retrying in {seconds}s (attempt {attempt} of {maxRetries})"
            // xo70FAR2Eu
            : $"{reason}. Retrying now (attempt {attempt} of {maxRetries})";
    }

    /// <summary>
    /// The line a turn that is simply taking a second run at itself shows, with no
    /// notice from the transport to name a cause. The reference numbers the attempt
    /// from one above the retry count it holds.
    /// </summary>
    public static string TakingLongerLabel(int retryCount) =>
        // Aaxo80X0HK
        $"Taking longer than usual. Trying again shortly (attempt {retryCount + 1})";

    /// <summary>
    /// The compaction bar's fill, in whole percent: the reference's
    /// <c>round(min(95, 100 * (1 - exp(-ms / 25000))))</c>, which approaches but never
    /// reaches 95 while the pass runs, and is only ever 100 once it completes. The
    /// caller keeps the running maximum, as the reference does, so a clock that jumps
    /// backwards cannot make the bar retreat.
    /// </summary>
    public static int CompactionProgress(double elapsedSeconds)
    {
        var value = 100 * (1 - Math.Exp(-(elapsedSeconds * 1000) / 25000.0));
        return (int)ThinkingLabels.RoundHalfUp(Math.Min(95, value));
    }

    /// <summary>
    /// The waiting-state escalation the reference's session working indicator applies
    /// over whatever label it was given (its <c>rw</c>, same chunk): the sentence is
    /// replaced once a wait passes thirty and then sixty seconds. Null means the
    /// caller's own label stands.
    /// </summary>
    public static string? WaitingEscalation(double elapsedSeconds) =>
        elapsedSeconds >= 60 ? "Working through a complex response..."   // 9aX8EHBM23
        : elapsedSeconds >= 30 ? "Still thinking..."                      // 7VOycUjyKn
        : null;
}
