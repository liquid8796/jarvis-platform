namespace JarvisCode.App.Services;

/// <summary>Where an update state machine currently is (the reference's five states).</summary>
public enum UpdateStatus
{
    Idle,
    Checking,
    Downloading,
    Ready,
    Error,
}

/// <summary>
/// One state of the updater, ported from the reference's own discriminated union
/// (app.asar <c>index.chunk-DnlgCaT3.js</c>, the <c>ZU</c> singleton): idle and
/// error carry nothing, checking and downloading carry whether a person asked,
/// ready carries the version that is staged.
/// </summary>
/// <param name="Status">Which of the five states this is.</param>
/// <param name="IsManualCheck">
/// Set while the run in flight was started from the menu rather than by the timer.
/// The reference keeps it on the state rather than on the machine because a manual
/// check that finishes has to notify even though the flag was cleared when the
/// download started.
/// </param>
/// <param name="Version">The staged version; only ever set in <see cref="UpdateStatus.Ready"/>.</param>
/// <param name="Error">What failed; only ever set in <see cref="UpdateStatus.Error"/>.</param>
public sealed record UpdateState(
    UpdateStatus Status,
    bool IsManualCheck = false,
    string? Version = null,
    string? Error = null)
{
    public static readonly UpdateState Idle = new(UpdateStatus.Idle);
}

/// <summary>
/// The updater's state machine, ported event for event from the reference's
/// <c>ZU</c> class. It carries no transport of its own: the reference wires
/// Electron's autoUpdater events into it, and <see cref="AppUpdateService"/>
/// wires the GitHub releases poller into the same five methods, so every
/// transition is testable without a network.
/// </summary>
public sealed class UpdateStateMachine
{
    private UpdateState _state = UpdateState.Idle;
    private bool _isManualCheck;

    /// <summary>Raised after every transition, as the reference emits "change".</summary>
    public event Action<UpdateState>? Changed;

    public UpdateState State
    {
        get => _state;
        private set
        {
            _state = value;
            Changed?.Invoke(value);
        }
    }

    /// <summary>
    /// True while a check or a download is running. The reference reads this to
    /// refuse a second check and to decide whether the menu row is clickable.
    /// </summary>
    public bool IsCheckInFlight =>
        _state.Status is UpdateStatus.Checking or UpdateStatus.Downloading;

    /// <summary>
    /// Marks the run that is about to start — or the one already running — as the
    /// user's. The reference's <c>setManualCheck</c>: a run already in flight is
    /// re-stamped in place rather than restarted, and a check asked for while an
    /// update is already staged re-announces it instead of arming anything.
    /// </summary>
    /// <returns>True when an already-staged update should be announced again.</returns>
    public bool SetManualCheck()
    {
        _isManualCheck = true;
        var state = _state;
        if (state.Status == UpdateStatus.Ready)
        {
            return true;
        }

        if (state.Status is UpdateStatus.Checking or UpdateStatus.Downloading && !state.IsManualCheck)
        {
            State = state with { IsManualCheck = true };
        }

        return false;
    }

    public void OnCheckingForUpdate() =>
        State = new UpdateState(UpdateStatus.Checking, IsManualCheck: _isManualCheck);

    /// <summary>An update exists: the reference goes straight on to downloading it.</summary>
    public void OnUpdateAvailable()
    {
        var manual = _isManualCheck;
        _isManualCheck = false;
        State = new UpdateState(UpdateStatus.Downloading, IsManualCheck: manual);
    }

    public void OnUpdateNotAvailable()
    {
        _isManualCheck = false;
        State = UpdateState.Idle;
    }

    public void OnUpdateDownloaded(string version)
    {
        _isManualCheck = false;
        State = new UpdateState(UpdateStatus.Ready, Version: version);
    }

    public void OnError(string message)
    {
        _isManualCheck = false;
        State = new UpdateState(UpdateStatus.Error, Error: message);
    }
}

/// <summary>One row the Help menu shows for the updater's current state.</summary>
/// <param name="Label">The row's text.</param>
/// <param name="Enabled">False for the rows that only report progress.</param>
public readonly record struct UpdateMenuRow(string Label, bool Enabled);

/// <summary>
/// What the Help menu shows for each updater state, ported from the reference's
/// <c>bSn()</c>. Idle and error offer the check; error adds a disabled row saying
/// the last attempt failed; the two busy states replace the row with a disabled
/// progress line; ready offers the restart, naming the version.
/// </summary>
public static class UpdateMenuLabels
{
    public const string CheckForUpdates = "Check for Updates…";
    public const string CheckingForUpdates = "Checking for Updates…";
    public const string DownloadingUpdate = "Downloading Update…";
    public const string LastUpdateAttemptFailed = "Last Update Attempt Failed";

    public static string RestartToUpdate(string version) => $"Restart to update to {version}";

    public static IReadOnlyList<UpdateMenuRow> For(UpdateState state) => state.Status switch
    {
        UpdateStatus.Checking => [new(CheckingForUpdates, Enabled: false)],
        UpdateStatus.Downloading => [new(DownloadingUpdate, Enabled: false)],
        UpdateStatus.Ready => [new(RestartToUpdate(state.Version ?? ""), Enabled: true)],
        UpdateStatus.Error =>
        [
            new(CheckForUpdates, Enabled: true),
            new(LastUpdateAttemptFailed, Enabled: false),
        ],
        _ => [new(CheckForUpdates, Enabled: true)],
    };
}

/// <summary>Which card the sidebar shows for the updater's state, if any.</summary>
public enum UpdateCardKind
{
    None,
    Checking,
    Downloading,
    Ready,
    Failed,
}

/// <summary>
/// The sidebar's auto-updater banner, ported from the reference's
/// <c>auto_updater_banner</c> (ion-dist <c>shared-17-BG9iAXbK.js</c>): two
/// spinner banners while it works, a clickable card offering the relaunch with
/// the staged version beneath it, and a card saying the update did not complete.
/// </summary>
/// <param name="Kind">Which of the five shapes to draw.</param>
/// <param name="Title">The card's first line, which is the whole text of a spinner banner.</param>
/// <param name="Detail">The card's second line, or null when it has none.</param>
/// <param name="Clickable">True for the two states the reference draws as a button.</param>
public readonly record struct UpdateCard(
    UpdateCardKind Kind,
    string Title,
    string? Detail,
    bool Clickable)
{
    public const string CheckingText = "Checking for updates...";
    public const string DownloadingText = "Downloading update...";
    public const string RelaunchTitle = "Relaunch to update";
    public const string FailedTitle = "Update didn’t complete";
    public const string FailedDetail = "Please quit and reopen Jarvis";

    public static UpdateCard For(UpdateState state) => state.Status switch
    {
        UpdateStatus.Checking => new(UpdateCardKind.Checking, CheckingText, null, Clickable: false),
        UpdateStatus.Downloading => new(UpdateCardKind.Downloading, DownloadingText, null, Clickable: false),
        // The reference writes the staged version as "v{n}" under the title, and
        // draws no second line at all when the staged build did not name one.
        UpdateStatus.Ready => new(
            UpdateCardKind.Ready,
            RelaunchTitle,
            string.IsNullOrEmpty(state.Version) ? null : "v" + state.Version,
            Clickable: true),
        UpdateStatus.Error => new(UpdateCardKind.Failed, FailedTitle, FailedDetail, Clickable: true),
        _ => new(UpdateCardKind.None, "", null, Clickable: false),
    };
}

/// <summary>
/// The body of the reference's "Jarvis is still working" dialog, which stands
/// between a relaunch and a session with a turn in flight. Two wordings: the one
/// running session is named, and any other count is counted.
/// </summary>
public static class RelaunchWhileWorking
{
    public const string Title = "Jarvis is still working";
    public const string UntitledSession = "Untitled session";
    public const string WaitingDetail =
        "Waiting for Jarvis to finish — the app will relaunch automatically.";
    public const string Cancel = "Cancel";
    public const string WaitForJarvis = "Wait for Jarvis";
    public const string UpdateAnyway = "Update anyway";
    public const string RelaunchAnyway = "Relaunch anyway";

    /// <summary>
    /// The dialog's description. The reference names the session only when exactly
    /// one is running and it could resolve which one that is; every other count,
    /// one included, takes the counted wording.
    /// </summary>
    public static string Detail(int runningCount, string? sessionTitle)
    {
        if (runningCount == 1 && sessionTitle is not null)
        {
            var title = string.IsNullOrWhiteSpace(sessionTitle) ? UntitledSession : sessionTitle;
            return $"Jarvis is working in {title}. Relaunching now will interrupt that work.";
        }

        var sessions = runningCount == 1 ? "1 session" : $"{runningCount} sessions";
        return $"Jarvis is working in {sessions}. Relaunching now will interrupt that work.";
    }
}
