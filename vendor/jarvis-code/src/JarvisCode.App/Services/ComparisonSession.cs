namespace JarvisCode.App.Services;

/// <summary>Where a comparison session is, in the reference's own five states.</summary>
internal enum ComparisonPhase
{
    /// <summary>Nothing sent yet, or the last turn errored; the composer is open.</summary>
    Idle,

    /// <summary>Both panels are answering; the composer refuses.</summary>
    Streaming,

    /// <summary>Both answered; the footer asks which one the reader prefers.</summary>
    Vote,

    /// <summary>A side was picked; the footer asks why.</summary>
    Reason,

    /// <summary>The preference was recorded, and can still be changed.</summary>
    Saved,
}

/// <summary>Which side won a turn.</summary>
internal enum ComparisonVote
{
    A,
    B,
    Tie,
}

/// <summary>One prompt, answered twice.</summary>
internal sealed class ComparisonTurn(string prompt)
{
    public string Prompt { get; } = prompt;

    /// <summary>Per panel: null while it is answering or once it answered cleanly.</summary>
    public string?[] Errors { get; } = new string?[2];

    public bool[] Settled { get; } = new bool[2];

    public ComparisonVote? Vote { get; set; }

    public IReadOnlyList<string>? Reasons { get; set; }

    public string? Comment { get; set; }
}

/// <summary>
/// The state a side-by-side comparison is in, and every rule about moving
/// between those states — ported from the reference's own session hook
/// (<c>en</c> in ion-dist chunk <c>cf6c0e3a1-Bi64U0E_.js</c>, desktop
/// 1.40609.1.0) with its guards intact: a vote can only be saved out of the
/// reason phase and only once per turn, a skip is recorded once per turn, an
/// errored turn goes back to idle rather than asking for a preference, and a
/// panel's memory is fixed the moment its conversation starts.
///
/// What the reference does with a saved vote — POST it to
/// <c>ClaudeaiContentService/SaveComparisonFeedback</c> under an organization
/// uuid, with both conversations' uuids — has no destination here, so this
/// keeps the count and the transitions and sends nothing. Nothing else about
/// the flow depends on the request.
/// </summary>
internal sealed class ComparisonSession
{
    private readonly List<ComparisonTurn> _turns = [];
    private readonly bool[] _started = new bool[2];
    private int _lastSavedTurn = -1;
    private int _skippedTurn = -1;

    /// <summary>Raised whenever anything a view draws has changed.</summary>
    public event Action? Changed;

    public ComparisonPhase Phase { get; private set; } = ComparisonPhase.Idle;

    public IReadOnlyList<ComparisonTurn> Turns => _turns;

    /// <summary>How many preferences have been recorded, for the header's counter.</summary>
    public int Votes { get; private set; }

    public ComparisonTurn? LastTurn => _turns.Count > 0 ? _turns[^1] : null;

    /// <summary>
    /// A panel whose conversation has begun, or a session past idle, cannot
    /// change its memory setting — the reference's <c>at</c>.
    /// </summary>
    public bool MemoryLocked(int panel) => _started[panel] || Phase != ComparisonPhase.Idle;

    /// <summary>
    /// Whether the sides may still be swapped. The reference only shuffles
    /// before the first turn and only while neither conversation exists.
    /// </summary>
    public bool CanShuffleSides =>
        _turns.Count == 0 && Phase == ComparisonPhase.Idle && !_started[0] && !_started[1];

    /// <summary>
    /// Opens a turn. Refused while the panels are still answering; a pending
    /// vote is saved and a pending question skipped first, which is what the
    /// reference does when a prompt is sent instead of answered.
    /// </summary>
    public bool BeginTurn(string prompt, bool hasAttachments = false)
    {
        if (Phase == ComparisonPhase.Streaming || (string.IsNullOrWhiteSpace(prompt) && !hasAttachments))
        {
            return false;
        }

        if (Phase == ComparisonPhase.Reason)
        {
            SaveVote(null, null);
        }
        else if (Phase == ComparisonPhase.Vote)
        {
            SkipVote();
        }

        _turns.Add(new ComparisonTurn(prompt.Trim()));
        _started[0] = _started[1] = true;
        Phase = ComparisonPhase.Streaming;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// One panel finished. Once both have, an error on either side returns the
    /// session to idle — the reference asks for no preference between an answer
    /// and a failure — and a clean pair asks for one.
    /// </summary>
    public void Settle(int panel, string? error)
    {
        if (LastTurn is not { } turn || Phase != ComparisonPhase.Streaming)
        {
            return;
        }

        turn.Settled[panel] = true;
        turn.Errors[panel] = error;
        if (!turn.Settled[0] || !turn.Settled[1])
        {
            Changed?.Invoke();
            return;
        }

        Phase = turn.Errors[0] is null && turn.Errors[1] is null
            ? ComparisonPhase.Vote
            : ComparisonPhase.Idle;
        Changed?.Invoke();
    }

    /// <summary>A side was picked; the footer moves on to asking why.</summary>
    public void Vote(ComparisonVote vote)
    {
        if (Phase != ComparisonPhase.Vote || LastTurn is not { } turn)
        {
            return;
        }

        turn.Vote = vote;
        Phase = ComparisonPhase.Reason;
        Changed?.Invoke();
    }

    /// <summary>The back arrow on the reason row: the pick is taken back.</summary>
    public void BackToVote()
    {
        if (Phase != ComparisonPhase.Reason || LastTurn is not { } turn)
        {
            return;
        }

        turn.Vote = null;
        turn.Reasons = null;
        turn.Comment = null;
        Phase = ComparisonPhase.Vote;
        Changed?.Invoke();
    }

    /// <summary>
    /// Records the preference. Only out of the reason phase, only with a pick
    /// behind it, and only once per turn — all three are the reference's guard.
    /// </summary>
    public void SaveVote(IReadOnlyList<string>? reasons, string? comment)
    {
        var index = _turns.Count - 1;
        if (Phase != ComparisonPhase.Reason
            || index < 0
            || _turns[index].Vote is null
            || _lastSavedTurn == index)
        {
            return;
        }

        _lastSavedTurn = index;
        var turn = _turns[index];
        turn.Reasons = reasons is { Count: > 0 } ? [.. reasons] : null;
        turn.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        Votes++;
        Phase = ComparisonPhase.Saved;
        Changed?.Invoke();
    }

    /// <summary>"Change": the recorded preference is taken back off the count.</summary>
    public void UndoVote()
    {
        var index = _turns.Count - 1;
        if (index < 0 || _turns[index].Vote is null)
        {
            return;
        }

        var turn = _turns[index];
        turn.Vote = null;
        turn.Reasons = null;
        turn.Comment = null;
        Votes = Math.Max(0, Votes - 1);
        _lastSavedTurn = -1;
        Phase = ComparisonPhase.Vote;
        Changed?.Invoke();
    }

    /// <summary>
    /// The question was left unanswered — the reference records that as its own
    /// outcome and leaves the phase alone, once per turn.
    /// </summary>
    public void SkipVote()
    {
        var index = _turns.Count - 1;
        if (Phase != ComparisonPhase.Vote || index < 0 || _skippedTurn == index)
        {
            return;
        }

        _skippedTurn = index;
    }

    /// <summary>
    /// The reference reverses the two panels on a coin flip before the first
    /// turn, so which model sits on the left carries no information.
    /// </summary>
    public bool ShouldShuffleSides(Func<double> random) => CanShuffleSides && random() < 0.5;
}
