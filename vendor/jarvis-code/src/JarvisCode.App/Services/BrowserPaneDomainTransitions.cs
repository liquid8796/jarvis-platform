namespace JarvisCode.App.Services;

/// <summary>
/// Shows the domain-transition card and answers whether the user allowed the
/// move: true allow, false deny, null when the card could not be shown at all.
/// </summary>
public delegate Task<bool?> DomainTransitionCard(
    string source, string destination, CancellationToken cancellationToken);

/// <summary>
/// What the pane's domain-transition gate decided about one navigation — the
/// reference's own six answers, in its own spelling.
/// </summary>
public enum DomainTransitionOutcome
{
    /// <summary>No transition to consent to, so the navigation just runs.</summary>
    NotRequired,

    /// <summary>The user allowed it; the pair is remembered and not asked again.</summary>
    Allowed,

    /// <summary>The user said no.</summary>
    Denied,

    /// <summary>Asked and declined too often, so the card is not shown again.</summary>
    Suppressed,

    /// <summary>A card was already up, or the tab moved while this one was.</summary>
    Retry,

    /// <summary>There is nowhere to show the card in this context.</summary>
    Refused,
}

/// <summary>
/// Consent for moving the Browser pane from one site to another.
///
/// The reference keeps a per-tab <c>lastExternalCommittedOrigin</c> and, when a
/// tool navigates that tab to a *different* origin, asks the user before the
/// navigation happens; the tool is then answered with one of five sentences
/// depending on how the asking went. Both a URL navigation and a
/// <c>back</c>/<c>forward</c> move go through it.
///
/// Ported from the reference desktop's preview origin policy (app.asar
/// <c>index.chunk-BHbE7U4N.js</c>): its <c>vJn</c>
/// (<c>requestPreviewDomainTransition</c>) for the outcome ladder, its
/// <c>msr</c> for the sentence each outcome answers with, its <c>KM</c>
/// (<c>normalizePreviewOrigin</c>) and <c>YM</c> for what counts as the same
/// site, its <c>yM</c>/<c>SM</c>/<c>lXt</c>/<c>xM</c> for the hosts that are
/// never an external origin, its <c>$G</c> for the precondition that both
/// sides are already-allowed origins, and the pair key
/// <c>"{src}→{dest}"</c> its <c>launchPreviewAllowedDomainTransitions</c>
/// list is written in. The decline thresholds are its live sibling prompt's
/// (<c>zZt</c>: <c>IZt</c> 3 per key, <c>LZt</c> 9 across the session).
///
/// Two measured caveats ride with it, both stated in
/// <c>Deltas/reference-surface-deltas.tsv</c>. The installed build ships the
/// *decision* stubbed — <c>DQt</c>, which computes the transition, is
/// <c>return null</c>, and <c>BZt</c>, which shows the card, is
/// <c>return "allowed"</c> — so that build never asks; what a transition is was
/// therefore read from the machinery around the stub rather than from its body.
/// And the pair is remembered for the session only, because the reference's
/// persisted half is written by the card, whose handler registration
/// (<c>uZt</c>) is stubbed too and so cannot be measured.
///
/// The state is one window's, as the reference's policy state is one app's;
/// the decisions are pure and unit-tested.
/// </summary>
public sealed class BrowserPaneDomainTransitions(Func<IReadOnlyCollection<string>> allowedOrigins)
{
    /// <summary>Joins the two halves of a remembered pair (the reference's U+2192).</summary>
    private const char PairSeparator = '→';

    /// <summary>
    /// Declines of one pair before the card stops being shown for it — the
    /// reference's <c>IZt</c>.
    /// </summary>
    internal const int DeclinesPerPair = 3;

    /// <summary>
    /// Declines across the whole session before every card stops — its <c>LZt</c>.
    /// </summary>
    internal const int DeclinesPerSession = 9;

    // ---- the five sentences (the reference's msr) ------------------------------

    /// <summary>
    /// The tail of the reference's <c>DM</c>, which builds every "the user said
    /// no" answer; navigate passes it "this domain transition".
    /// </summary>
    public static string Declined(string what) =>
        $"The user declined {what}. Do not retry — ask what they'd like to do instead.";

    public const string SuppressedRefusal =
        "The user has repeatedly declined this domain transition, so the prompt is suppressed and the " +
        "navigation was not performed. Do not retry; the user can navigate there manually if they want.";

    public const string RetryRefusal =
        "Could not confirm the domain transition (another permission prompt was open, or the session " +
        "changed during the prompt) — retry the navigation.";

    public const string UnavailableRefusal =
        "This domain transition is not available right now (the consent prompt cannot be shown in this " +
        "context), so the navigation was not performed.";

    /// <summary>The answer for an outcome the ladder does not name.</summary>
    public const string UnconfirmedRefusal =
        "The domain transition could not be confirmed, so the navigation was not performed.";

    // ---- the card itself -------------------------------------------------------
    //
    // This app's own wording. The reference's card is reached through a handler
    // its shipped build registers with a no-op (its uZt), so there is no
    // registration to read the question off and nothing to copy; these three
    // strings are declared in Deltas/ported-text-deltas.tsv for that reason.

    /// <summary>The chip the question is filed under.</summary>
    public const string CardHeader = "Site";

    public static string CardQuestion(string source, string destination) =>
        $"Let Jarvis take the Browser pane from {source} to {destination}?";

    public const string CardAllow = "Allow";

    public const string CardDeny = "Deny";

    public static string CardAllowHint(string destination) =>
        $"The pane navigates to {destination}, and moving between these two sites stops asking";

    public const string CardDenyHint = "The pane stays where it is";

    /// <summary>
    /// The sentence this outcome answers the tool with, or null to let the
    /// navigation run. The reference's <c>msr</c>, branch for branch.
    /// </summary>
    public static string? Refusal(DomainTransitionOutcome outcome) => outcome switch
    {
        DomainTransitionOutcome.NotRequired or DomainTransitionOutcome.Allowed => null,
        DomainTransitionOutcome.Denied => Declined("this domain transition"),
        DomainTransitionOutcome.Suppressed => SuppressedRefusal,
        DomainTransitionOutcome.Retry => RetryRefusal,
        DomainTransitionOutcome.Refused => UnavailableRefusal,
        _ => UnconfirmedRefusal,
    };

    // ---- what counts as the same site ------------------------------------------

    /// <summary>
    /// A URL reduced to the origin the policy reasons about, or null for
    /// anything that is not an ordinary web page — the reference's <c>KM</c>.
    /// Only http and https; trailing dots go; a leading "www." is dropped from a
    /// host of three or more labels that is neither ".local" nor an address
    /// literal; an explicit port stays.
    /// </summary>
    public static string? NormalizeOrigin(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var host = uri.Host.TrimEnd('.');
        if (host.Length == 0)
        {
            return null;
        }

        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
            host.Split('.').Length >= 3 &&
            !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
            !IsAddressLiteral(host))
        {
            host = host[4..];
        }

        if (host.Length == 0 || host.StartsWith('.'))
        {
            return null;
        }

        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return $"{uri.Scheme}://{host}{port}";
    }

    /// <summary>
    /// The same origin under the other scheme — the reference's <c>YM</c>, which
    /// is what makes an https grant cover its http twin.
    /// </summary>
    public static string SchemeTwin(string origin) =>
        origin.StartsWith("https://", StringComparison.Ordinal)
            ? "http://" + origin[8..]
            : origin.StartsWith("http://", StringComparison.Ordinal)
                ? "https://" + origin[7..]
                : origin;

    /// <summary>
    /// The hosts a page is never treated as having come *from* — the reference's
    /// <c>yM</c>. A dev server the pane is previewing is not a site the user
    /// consented to, so moving away from one is not a transition.
    /// </summary>
    public static bool IsLoopbackHost(string host) =>
        host is "localhost" or "127.0.0.1" or "0.0.0.0" or "::1" or "[::1]" ||
        host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The wider set the reference keeps out of its grant list (<c>xM</c> over
    /// <c>SM</c> and <c>lXt</c>): loopback under any spelling, the rest of
    /// 127.0.0.0/8, a trailing-dot loopback name, and the IPv6 literals.
    /// </summary>
    public static bool IsPrivateHost(string host)
    {
        var trimmed = host.TrimEnd('.');
        if (IsLoopbackHost(host) || (trimmed != host && IsLoopbackHost(trimmed)))
        {
            return true;
        }

        return host is "[::]" ||
            host.StartsWith("[::ffff:", StringComparison.OrdinalIgnoreCase) ||
            IsLoopbackRange(host);
    }

    /// <summary>127.0.0.0/8 written as one to four dotted parts, as the reference matches it.</summary>
    private static bool IsLoopbackRange(string host)
    {
        var parts = host.Split('.');
        if (parts.Length is < 2 or > 4 || parts[0] != "127")
        {
            return false;
        }

        return parts.All(static p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit));
    }

    /// <summary>True for a host that is an address rather than a name.</summary>
    private static bool IsAddressLiteral(string host) =>
        host.StartsWith('[') || (host.Split('.') is { Length: 4 } q && q.All(static p => p.All(char.IsAsciiDigit)));

    /// <summary>The key one remembered pair is stored under.</summary>
    public static string PairKey(string source, string destination) =>
        $"{source}{PairSeparator}{destination}";

    /// <summary>
    /// The origin a committed navigation leaves behind as the tab's last
    /// external one, or null to leave the previous value standing. The reference
    /// sets it only for an ordinary web origin whose host is not loopback, and
    /// never clears it on the way past one.
    /// </summary>
    public static string? CommittedOrigin(string? url)
    {
        if (NormalizeOrigin(url) is not { } origin)
        {
            return null;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLoopbackHost(uri.Host) ? null : origin;
    }

    // ---- the session's state ---------------------------------------------------

    // One gate serves every session in a window, as the reference's policy state
    // serves every tab, so two turns can reach it at once.
    private readonly object _state = new();
    private readonly Dictionary<string, int> _declines = new(StringComparer.Ordinal);
    private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);
    private int _sessionDeclines;
    private int _cardsOpen;

    /// <summary>
    /// Drops what the session has decided — the reference's <c>_Zt</c>, which
    /// clears the session's transition grants and its decline counts when the
    /// pane's isolation surface is reset.
    /// </summary>
    public void Reset()
    {
        lock (_state)
        {
            _declines.Clear();
            _admitted.Clear();
            _sessionDeclines = 0;
        }
    }

    /// <summary>Whether this pair has already been allowed in this session.</summary>
    public bool IsAdmitted(string source, string destination)
    {
        lock (_state)
        {
            return _admitted.Contains(PairKey(source, destination));
        }
    }

    /// <summary>
    /// Records a pair the user allowed, so the card is not shown for it again —
    /// the reference's <c>xFn</c>, and what makes its history mover stop
    /// reporting the move as changed. Its sibling prompt clears the key's
    /// decline tally when it is allowed, because that one can be asked again;
    /// an admitted pair here never is, so there is nothing for it to do.
    /// </summary>
    public void Admit(string source, string destination)
    {
        lock (_state)
        {
            _admitted.Add(PairKey(source, destination));
        }
    }

    /// <summary>
    /// Whether the pane has been allowed to act on this origin at all — the
    /// reference's <c>$G</c>. Its list is <c>launchPreviewAllowedOrigins</c>,
    /// which this app keeps as Settings › Jarvis Code › Browser › Allowed sites;
    /// an https origin is also covered by a grant of its http twin, as the
    /// reference's <c>SN</c> has it, and an address or loopback host is never
    /// granted.
    /// </summary>
    public bool IsGranted(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || IsPrivateHost(uri.Host))
        {
            return false;
        }

        // Settings stores an entry reduced to its authority, which is not the
        // same reduction as this one — it keeps a "www." host. The reference
        // compares its list against KM on both sides, so both sides normalize.
        var twin = origin.StartsWith("https://", StringComparison.Ordinal) ? SchemeTwin(origin) : null;
        foreach (var entry in allowedOrigins())
        {
            if (NormalizeOrigin(entry) is not { } listed)
            {
                continue;
            }

            if (string.Equals(listed, origin, StringComparison.OrdinalIgnoreCase) ||
                (twin is not null && string.Equals(listed, twin, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The transition this navigation would make, or null when there is none to
    /// consent to. The reference's <c>DQt</c>, whose body the installed build
    /// ships as <c>return null</c>: what is left of it is its inputs — the tab's
    /// last external origin and where it is going — its output pair, and the
    /// normaliser the same module uses to decide when two addresses are the same
    /// site. A tab that has not committed an external page yet has no source, so
    /// its first navigation is never a transition.
    /// </summary>
    public (string Source, string Destination)? Transition(string? lastExternalOrigin, string? destinationUrl)
    {
        if (NormalizeOrigin(lastExternalOrigin) is not { } source ||
            NormalizeOrigin(destinationUrl) is not { } destination)
        {
            return null;
        }

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(SchemeTwin(source), destination, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return IsAdmitted(source, destination) ? null : (source, destination);
    }

    /// <summary>
    /// What the shipped build answers: allowed, always.
    ///
    /// The reference's own <c>requestDomainTransitionPermission</c> (<c>BZt</c>
    /// in desktop 1.44121.2.0's <c>index.chunk-BHbE7U4N.js</c>) is
    /// <c>async function BZt(e,t,n,r,i){return"allowed"}</c>; the transition it
    /// would be asked about is computed by <c>DQt</c>, which is
    /// <c>return null</c>; and the card handler setter <c>uZt</c> is an empty
    /// function. So that build never shows a card, and none of the five
    /// refusals above can be produced by it. This pane matches it.
    ///
    /// The decision those parts describe is <see cref="DecideAsync"/>, kept
    /// beside this and covered, because the reference ships it the same way:
    /// everything around the stub is live code. Re-enabling the card is this
    /// method calling that one.
    /// </summary>
    public Task<DomainTransitionOutcome> RequestAsync(
        string? lastExternalOrigin,
        string? destinationUrl,
        DomainTransitionCard? card,
        Func<CancellationToken, Task<bool>>? stillCurrent,
        CancellationToken cancellationToken) =>
        Task.FromResult(DomainTransitionOutcome.Allowed);

    /// <summary>
    /// Consents to one navigation, the way the reference's
    /// <c>requestPreviewDomainTransition</c> describes it: no transition, or
    /// either side not an allowed origin, needs nothing; otherwise the card
    /// decides. Not on the shipped path — see <see cref="RequestAsync"/>.
    /// </summary>
    /// <param name="lastExternalOrigin">The tab's last committed external origin.</param>
    /// <param name="destinationUrl">Where the tool is navigating it.</param>
    /// <param name="card">
    /// Shows the card and answers allow/deny, or null when there is none to show
    /// — the reference's unregistered card handler, which its
    /// <c>requestPreviewCredentialedNavConsent</c> answers "unavailable" for.
    /// </param>
    /// <param name="stillCurrent">
    /// Whether the tab is still where it was when the card went up. The
    /// reference re-reads the tab's navEpoch across the prompt and discards a
    /// stale answer; a null probe skips the check.
    /// </param>
    internal async Task<DomainTransitionOutcome> DecideAsync(
        string? lastExternalOrigin,
        string? destinationUrl,
        DomainTransitionCard? card,
        Func<CancellationToken, Task<bool>>? stillCurrent,
        CancellationToken cancellationToken)
    {
        if (Transition(lastExternalOrigin, destinationUrl) is not { } pair)
        {
            return DomainTransitionOutcome.NotRequired;
        }

        var (source, destination) = pair;
        if (!IsGranted(destination) || !IsGranted(source))
        {
            return DomainTransitionOutcome.NotRequired;
        }

        if (card is null)
        {
            return DomainTransitionOutcome.Refused;
        }

        var key = PairKey(source, destination);
        lock (_state)
        {
            if (_declines.GetValueOrDefault(key) >= DeclinesPerPair || _sessionDeclines >= DeclinesPerSession)
            {
                return DomainTransitionOutcome.Suppressed;
            }

            // "another permission prompt was open" — a second card cannot go up
            // while one is, and the model is told to send the navigation again.
            if (_cardsOpen > 0)
            {
                return DomainTransitionOutcome.Retry;
            }

            _cardsOpen++;
        }

        bool? answer;
        try
        {
            answer = await card(source, destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_state)
            {
                _cardsOpen--;
            }
        }

        // "or the session changed during the prompt": an answer about a page the
        // tab has already left is not an answer about this navigation.
        if (stillCurrent is not null && !await stillCurrent(cancellationToken).ConfigureAwait(false))
        {
            return DomainTransitionOutcome.Retry;
        }

        if (answer is not { } decided)
        {
            return DomainTransitionOutcome.Refused;
        }

        if (!decided)
        {
            lock (_state)
            {
                _declines[key] = _declines.GetValueOrDefault(key) + 1;
                _sessionDeclines++;
            }

            return DomainTransitionOutcome.Denied;
        }

        Admit(source, destination);
        return DomainTransitionOutcome.Allowed;
    }
}
