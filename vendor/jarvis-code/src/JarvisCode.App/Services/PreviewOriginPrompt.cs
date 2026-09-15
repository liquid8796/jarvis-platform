namespace JarvisCode.App.Services;

/// <summary>
/// What the host supplies so the pane can ask about a site: the card itself, the
/// list it consults before asking, and where an allowed site is recorded. A
/// session with nowhere to ask supplies none of it and the pane stays ungated,
/// which is the answer a headless run and a subagent get.
/// </summary>
/// <param name="Card">Shows the card and answers with the button index.</param>
/// <param name="IsAllowed">Whether this site is already granted.</param>
/// <param name="Allow">Records a site the user allowed.</param>
public sealed record PreviewOriginConsent(
    Func<string, string, CancellationToken, Task<int>> Card,
    Func<string, bool> IsAllowed,
    Action<string> Allow);

/// <summary>What asking about an origin came to.</summary>
public enum PreviewOriginDecision
{
    /// <summary>There was nothing to ask about, or the pane moved while asking.</summary>
    NotPrompted,

    /// <summary>The pane may act on the site.</summary>
    Allowed,

    /// <summary>The user said no, or has said no often enough to stop being asked.</summary>
    DeniedByUser,
}

/// <summary>
/// How risky the reference considers a site. Only <see cref="Ordinary"/> is
/// reachable in this build: the other two come from lists Anthropic serves - the
/// blocklist categories its classifier returns, and a host set read from a
/// remote feature value that is empty by default - and there is no local source
/// for either. They are carried because the copy each one selects is the
/// reference's, and a category that cannot arrive is a smaller gap than a card
/// that would word itself differently if one did.
/// </summary>
public enum PreviewOriginCategory
{
    Ordinary,
    Financial,
    HigherRisk,
}

/// <summary>
/// The Browser pane's per-origin consent, ported from the reference's own card
/// (desktop 1.46388.3.0). Its exported requestPreviewOriginPermission is real
/// there - unlike the domain-transition sibling beside it, which ships stubbed -
/// and it is what makes an origin allowed by being consented to rather than only
/// by being typed into Settings.
///
/// <para>
/// The rules carried here are its own: only a call that came from a tool is
/// gated (a user typing a URL is not asked), an origin already granted is not
/// asked again, and the card stops appearing once it has been refused enough -
/// three times for one origin, or nine times across the session.
/// </para>
/// </summary>
public sealed class PreviewOriginPrompt
{
    /// <summary>The reference's <c>y2t</c>: refusals of one origin before it stops asking.</summary>
    public const int PerOriginRefusals = 3;

    /// <summary>The reference's <c>b2t</c>: refusals across the session before it stops asking.</summary>
    public const int SessionRefusals = 9;

    /// <summary>The question, over the origin the embedded content came from.</summary>
    public static string Message(string origin) =>
        $"This page embeds content from {origin}. Allow Jarvis to click inside it?";

    /// <summary>The line every card carries.</summary>
    public const string EmbeddedDetail =
        "The embedded content is a different website from the page Jarvis is browsing, and clicks " +
        "inside it act on that site.";

    /// <summary>The line a financial site adds.</summary>
    public const string FinancialDetail =
        "This site appears to be a financial site. Jarvis won't transfer funds, but approving lets " +
        "it interact with the embedded page.";

    /// <summary>The line a higher-risk site adds, and why it is asked every time.</summary>
    public const string HigherRiskDetail =
        "This site is on a higher-risk list, so this approval is asked for every time.";

    /// <summary>The card's buttons, in the reference's order.</summary>
    public static readonly IReadOnlyList<string> Buttons = ["Allow", "Cancel"];

    /// <summary>Cancel is both the default and the escape, as the reference sets them.</summary>
    public const int DefaultButton = 1;

    private readonly Dictionary<string, int> _refusalsByOrigin = new(StringComparer.OrdinalIgnoreCase);
    private int _refusals;

    /// <summary>The detail block a card carries, its lines a blank line apart.</summary>
    public static string Detail(PreviewOriginCategory category)
    {
        List<string> lines = [EmbeddedDetail];
        if (category == PreviewOriginCategory.Financial)
        {
            lines.Add(FinancialDetail);
        }

        if (category == PreviewOriginCategory.HigherRisk)
        {
            lines.Add(HigherRiskDetail);
        }

        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// Whether the card would be suppressed rather than shown - the reference
    /// checks this before it builds one, and answers as a refusal.
    /// </summary>
    public bool IsSuppressed(string origin) =>
        _refusalsByOrigin.GetValueOrDefault(origin) >= PerOriginRefusals || _refusals >= SessionRefusals;

    /// <summary>
    /// Asks about an origin. <paramref name="ask"/> is handed the message and the
    /// detail and answers with the button index; a suppressed origin refuses
    /// without it being called at all.
    /// </summary>
    public async Task<PreviewOriginDecision> AskAsync(
        string origin,
        PreviewOriginCategory category,
        Func<string, string, CancellationToken, Task<int>> ask,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return PreviewOriginDecision.NotPrompted;
        }

        if (IsSuppressed(origin))
        {
            return PreviewOriginDecision.DeniedByUser;
        }

        var answer = await ask(Message(origin), Detail(category), cancellationToken);
        if (answer == 0)
        {
            // An allowed origin starts again from nothing, so a later refusal
            // gets its own three tries rather than inheriting an old count.
            _refusalsByOrigin.Remove(origin);
            return PreviewOriginDecision.Allowed;
        }

        _refusalsByOrigin[origin] = _refusalsByOrigin.GetValueOrDefault(origin) + 1;
        _refusals++;
        return PreviewOriginDecision.DeniedByUser;
    }
}
