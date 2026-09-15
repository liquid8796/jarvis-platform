namespace JarvisCode.App.Services;

/// <summary>
/// The state one sidebar row is in, in the reference's own vocabulary. The order of
/// the members is its precedence table <c>{error:0, awaiting:1, running:2, ready:3,
/// pr:4, idle:5}</c> (desktop 1.44121.2.0, <c>shared-18-6A6evEfS.js</c>@256790), so
/// the enum's numeric value <em>is</em> the rank.
/// </summary>
public enum SessionRowState
{
    Error = 0,
    Awaiting = 1,
    Running = 2,
    Ready = 3,
    Pr = 4,
    Idle = 5,
}

/// <summary>Whether a row is waiting on a permission card or on an answer to a question.</summary>
public enum AwaitingKind
{
    Permission,
    Question,
}

/// <summary>The bucket a row falls in when the sidebar groups by State.</summary>
public enum SessionStateBucket
{
    Blocked,
    Review,
    Working,
    Done,
}

/// <summary>
/// What a session row reports about itself, in the shape the reference's own
/// resolver reads.
/// </summary>
public sealed record SessionRowStatus
{
    public bool IsArchived { get; init; }

    public bool HasError { get; init; }

    /// <summary>A turn is running right now.</summary>
    public bool IsRunning { get; init; }

    /// <summary>The session is waiting on the user — a permission card or a question.</summary>
    public bool IsAwaiting { get; init; }

    public AwaitingKind Awaiting { get; init; } = AwaitingKind.Permission;

    /// <summary>The session has produced an answer at least once (its <c>hasCompleted</c>).</summary>
    public bool HasCompleted { get; init; }

    public bool IsUnread { get; init; }

    /// <summary>The user marked it unread by hand, rather than a background turn having ended.</summary>
    public bool ExplicitUnread { get; init; }

    public PrDisplayState PrState { get; init; } = PrDisplayState.None;
}

/// <summary>
/// The sidebar row's status ladder and the State grouping over it, ported from the
/// reference desktop 1.44121.2.0: the resolver <c>Mp</c>
/// (<c>shared-11-CL4cxK09.js</c>@23600), the precedence table <c>_z</c>
/// (<c>shared-18</c>@256790), the accessible labels <c>XE</c>
/// (<c>shared-17-DsNaDSP_.js</c>@164284), and the State buckets <c>dk</c>/<c>uk</c>
/// with their order <c>iu</c> and labels <c>au</c> (<c>shared-19</c>@159700 over
/// <c>shared-14-CnCX6SbW.js</c>@59900).
///
/// Pure, so every rung is unit-testable without a window.
/// </summary>
public static class SessionRowStates
{
    /// <summary>The reference's <c>XE</c> — what an assistive reader is told the mark means.</summary>
    public const string AwaitingPermissionLabel = "Awaiting input";
    public const string AwaitingQuestionLabel = "Awaiting answer";
    public const string RunningLabel = "Running";
    public const string BrowsingLabel = "Using the browser";
    public const string ReadyLabel = "Unread response";
    public const string IdleLabel = "Idle";

    /// <summary>The reference's <c>au</c>, the State grouping's section headings.</summary>
    public const string BlockedHeading = "Needs input";
    public const string ReviewHeading = "Ready for review";
    public const string WorkingHeading = "Working";
    public const string DoneHeading = "Completed";

    /// <summary>The reference's <c>iu</c>: the order the State sections appear in.</summary>
    public static IReadOnlyList<SessionStateBucket> BucketOrder { get; } =
    [
        SessionStateBucket.Blocked,
        SessionStateBucket.Review,
        SessionStateBucket.Working,
        SessionStateBucket.Done,
    ];

    /// <summary>
    /// The reference's <c>Mp</c>, rung for rung. An archived session answers only
    /// ready-or-idle; otherwise error, then the two live statuses, then the pull
    /// request and the unread response settle it — with an unread the user marked by
    /// hand outranking the pull request, which is what its <c>explicitUnread</c> arm
    /// says.
    /// </summary>
    public static SessionRowState Resolve(SessionRowStatus status, bool readyAfterPr = false)
    {
        if (status.IsArchived)
        {
            return status is { HasCompleted: true, IsUnread: true } ? SessionRowState.Ready : SessionRowState.Idle;
        }

        if (status.HasError)
        {
            return SessionRowState.Error;
        }

        if (status.IsAwaiting)
        {
            return SessionRowState.Awaiting;
        }

        if (status.IsRunning)
        {
            return SessionRowState.Running;
        }

        var hasPr = status.PrState != PrDisplayState.None;
        var prSettled = hasPr && status.PrState is PrDisplayState.Merged or PrDisplayState.Closed;
        var unreadAnswer = status is { HasCompleted: true, IsUnread: true };

        if (unreadAnswer && status.ExplicitUnread)
        {
            return SessionRowState.Ready;
        }

        if (hasPr && (prSettled || readyAfterPr))
        {
            return SessionRowState.Pr;
        }

        if (unreadAnswer)
        {
            return SessionRowState.Ready;
        }

        return hasPr ? SessionRowState.Pr : SessionRowState.Idle;
    }

    /// <summary>The reference's <c>_z</c> rank — lower sorts first.</summary>
    public static int Rank(SessionRowState state) => (int)state;

    /// <summary>
    /// Whether the row draws a status mark at all: the reference's
    /// <c>!isArchived &amp;&amp; state !== "idle" &amp;&amp; state !== "pr"</c>.
    /// </summary>
    public static bool ShowsMark(SessionRowState state, bool isArchived) =>
        !isArchived && state is not (SessionRowState.Idle or SessionRowState.Pr);

    /// <summary>The accessible name the mark carries.</summary>
    public static string Label(SessionRowState state, AwaitingKind awaiting = AwaitingKind.Permission) =>
        state switch
        {
            SessionRowState.Awaiting =>
                awaiting == AwaitingKind.Question ? AwaitingQuestionLabel : AwaitingPermissionLabel,
            SessionRowState.Running => RunningLabel,
            SessionRowState.Ready => ReadyLabel,
            _ => IdleLabel,
        };

    /// <summary>
    /// The reference's <c>uk</c>: the pull-request states that put a row in the
    /// Review bucket. A merged or closed pull request is not one of them.
    /// </summary>
    public static bool IsReviewablePr(PrDisplayState state) =>
        state is PrDisplayState.Open or PrDisplayState.Draft or PrDisplayState.Approved
            or PrDisplayState.ChangesRequested or PrDisplayState.Conflicting;

    /// <summary>
    /// The reference's <c>dk</c>: an archived row is Completed; otherwise the status
    /// ladder run <em>without</em> a pull request decides, an error or a wait reads as
    /// Blocked and a running turn as Working, and only then does a reviewable pull
    /// request lift the row into Review.
    /// </summary>
    public static SessionStateBucket Bucket(SessionRowStatus status)
    {
        if (status.IsArchived)
        {
            return SessionStateBucket.Done;
        }

        var state = Resolve(status with { PrState = PrDisplayState.None });
        if (state is SessionRowState.Error or SessionRowState.Awaiting)
        {
            return SessionStateBucket.Blocked;
        }

        if (state == SessionRowState.Running)
        {
            return SessionStateBucket.Working;
        }

        return IsReviewablePr(status.PrState) ? SessionStateBucket.Review : SessionStateBucket.Done;
    }

    /// <summary>The section heading for one bucket.</summary>
    public static string Heading(SessionStateBucket bucket) => bucket switch
    {
        SessionStateBucket.Blocked => BlockedHeading,
        SessionStateBucket.Review => ReviewHeading,
        SessionStateBucket.Working => WorkingHeading,
        _ => DoneHeading,
    };

    /// <summary>The section key, the reference's <c>state-{bucket}</c>.</summary>
    public static string BucketKey(SessionStateBucket bucket) =>
        "state-" + bucket switch
        {
            SessionStateBucket.Blocked => "blocked",
            SessionStateBucket.Review => "review",
            SessionStateBucket.Working => "working",
            _ => "done",
        };
}
