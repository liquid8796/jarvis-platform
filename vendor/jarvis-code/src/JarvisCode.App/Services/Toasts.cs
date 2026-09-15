namespace JarvisCode.App.Services;

/// <summary>
/// The three looks a toast can have. The reference's adders map onto these:
/// <c>addSuccess</c> is <see cref="Neutral"/> (its "info" toast type, rendered
/// as the neutral variant), <c>addError</c>/<c>addWarning</c> are
/// <see cref="Warning"/>, and <c>addDanger</c> is <see cref="Danger"/>.
/// </summary>
public enum ToastVariant
{
    Neutral,
    Warning,
    Danger,
}

/// <summary>One toast, as the reference's toast manager holds it.</summary>
public sealed class Toast
{
    public required int Id { get; init; }

    /// <summary>The text the reference stores as <c>baseTitle</c> — what dedupe compares.</summary>
    public required string BaseTitle { get; init; }

    /// <summary>What is drawn: the base title, or "{base} (×N)" once it has been repeated.</summary>
    public string Title { get; internal set; } = "";

    public string? Description { get; init; }

    public ToastVariant Variant { get; init; }

    /// <summary>The action button's label, when the toast carries one ("Undo").</summary>
    public string? ActionLabel { get; init; }

    public Action? ActionInvoke { get; init; }

    /// <summary>
    /// Milliseconds before it dismisses itself; zero means it stays until closed.
    /// A toast carrying an action is always zero — the reference refuses to time
    /// out a card the user is being asked to click.
    /// </summary>
    public int TimeoutMs { get; internal set; }

    /// <summary>
    /// The reference's <c>uniqueKey</c>: a second toast with the same key replaces
    /// the live one instead of stacking behind it.
    /// </summary>
    public string? UniqueKey { get; init; }

    /// <summary>How many times this toast has been raised; drives the "(×N)" suffix.</summary>
    public int Count { get; internal set; } = 1;

    /// <summary>Danger toasts are high priority, everything else low — the reference's own split.</summary>
    public bool IsHighPriority => Variant == ToastVariant.Danger;

    internal Action? OnClose { get; init; }
}

/// <summary>
/// The toast manager, ported from the reference desktop's own
/// (<c>ErrorsProvider</c> in <c>shared-1-BK5wDqY-.js</c> for the adders, the
/// <c>Toast</c> component in <c>shared-16-DFDNRrwQ.js</c> for the queue rules,
/// and the app's provider in <c>index-DEczO-db.js</c> for the timeout).
///
/// Kept free of WPF so the dedupe, the timeout rule and the stacking order are
/// unit-testable; <see cref="Controls.ToastHost"/> draws whatever this holds.
/// </summary>
public sealed class ToastQueue
{
    /// <summary>
    /// The reference app mounts its toast provider with <c>timeout: 6500</c>.
    /// (The component's own default is 6000; the app overrides it, and the app
    /// is what the user sees.)
    /// </summary>
    public const int DefaultTimeoutMs = 6500;

    /// <summary>
    /// base-ui's toast limit, which the reference leaves at its default: three
    /// cards in the stack, the front one readable and two peeking behind it.
    /// </summary>
    public const int VisibleLimit = 3;

    /// <summary>
    /// The reference clamps every requested timeout up to six seconds before it
    /// reaches the toast manager, so a caller asking for two seconds still gets
    /// a card that can be read. Zero and below pass through as "stays open".
    /// </summary>
    public const int MinimumTimeoutMs = 6000;

    /// <summary>The reference's own <c>GN</c>: undefined and non-positive pass through.</summary>
    public static int ClampTimeout(int timeoutMs) =>
        timeoutMs <= 0 ? timeoutMs : Math.Max(timeoutMs, MinimumTimeoutMs);

    /// <summary>
    /// The window's queue, for the panels that have no services of their own.
    /// The reference reaches its toast manager through a React context from
    /// anywhere in the tree; this is that, in the static-hook shape the rest of
    /// this app already uses for cross-tree wiring.
    /// </summary>
    public static ToastQueue? Current { get; set; }

    private readonly List<Toast> _toasts = [];
    private int _nextId = 1;

    /// <summary>Raised whenever the list changes, so the host can repaint.</summary>
    public event Action? Changed;

    /// <summary>Oldest first, which is the order the reference appends in.</summary>
    public IReadOnlyList<Toast> Toasts => _toasts;

    /// <summary>The card the user is reading: the newest, drawn in front.</summary>
    public Toast? Front => _toasts.Count == 0 ? null : _toasts[^1];

    public int Add(
        string title,
        ToastVariant variant = ToastVariant.Neutral,
        string? description = null,
        string? actionLabel = null,
        Action? actionInvoke = null,
        int? timeoutMs = null,
        string? uniqueKey = null)
    {
        var hasAction = actionLabel is { Length: > 0 };

        // A keyed toast replaces the live one carrying that key rather than
        // stacking a second copy of the same news.
        if (uniqueKey is { Length: > 0 })
        {
            var live = _toasts.FirstOrDefault(t => t.UniqueKey == uniqueKey);
            if (live is not null)
            {
                Close(live.Id);
            }
        }
        else if (_toasts.FirstOrDefault(t => t.BaseTitle == title) is { } repeated)
        {
            // Unkeyed toasts dedupe by their own text: the live card counts up
            // instead of a second identical card appearing behind it.
            repeated.Count++;
            repeated.Title = $"{repeated.BaseTitle} (×{repeated.Count})";
            repeated.TimeoutMs = repeated.ActionLabel is { Length: > 0 }
                ? 0
                : ClampTimeout(timeoutMs ?? DefaultTimeoutMs);
            Changed?.Invoke();
            return repeated.Id;
        }

        var toast = new Toast
        {
            Id = _nextId++,
            BaseTitle = title,
            Title = title,
            Description = description,
            Variant = variant,
            ActionLabel = actionLabel,
            ActionInvoke = actionInvoke,
            TimeoutMs = hasAction ? 0 : ClampTimeout(timeoutMs ?? DefaultTimeoutMs),
            UniqueKey = uniqueKey,
        };
        _toasts.Add(toast);
        Changed?.Invoke();
        return toast.Id;
    }

    /// <summary>addSuccess: the neutral card.</summary>
    public int AddSuccess(string title, string? uniqueKey = null, int? timeoutMs = null) =>
        Add(title, ToastVariant.Neutral, uniqueKey: uniqueKey, timeoutMs: timeoutMs);

    /// <summary>addError / addWarning: both raise the warning card in the reference.</summary>
    public int AddError(string title, string? uniqueKey = null, int? timeoutMs = null) =>
        Add(title, ToastVariant.Warning, uniqueKey: uniqueKey, timeoutMs: timeoutMs);

    public int AddWarning(string title, string? uniqueKey = null, int? timeoutMs = null) =>
        Add(title, ToastVariant.Warning, uniqueKey: uniqueKey, timeoutMs: timeoutMs);

    public int AddDanger(string title, string? uniqueKey = null, int? timeoutMs = null) =>
        Add(title, ToastVariant.Danger, uniqueKey: uniqueKey, timeoutMs: timeoutMs);

    /// <summary>A toast with an action button, which never times itself out.</summary>
    public int AddWithAction(
        string title,
        string actionLabel,
        Action actionInvoke,
        ToastVariant variant = ToastVariant.Neutral,
        string? uniqueKey = null) =>
        Add(title, variant, actionLabel: actionLabel, actionInvoke: actionInvoke, uniqueKey: uniqueKey);

    public void Close(int id)
    {
        var index = _toasts.FindIndex(t => t.Id == id);
        if (index < 0)
        {
            return;
        }

        var toast = _toasts[index];
        _toasts.RemoveAt(index);
        toast.OnClose?.Invoke();
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_toasts.Count == 0)
        {
            return;
        }

        _toasts.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// How far behind the front a card sits, counting from zero. The reference
    /// draws each one <c>index * -14px</c> up and <c>1 - index * 0.04</c> wide,
    /// and hides everything past <see cref="VisibleLimit"/>.
    /// </summary>
    public static double OffsetFor(int stackIndex) => stackIndex * -14.0;

    public static double ScaleFor(int stackIndex) => 1.0 - (stackIndex * 0.04);

    public static bool IsVisible(int stackIndex) => stackIndex < VisibleLimit;
}

/// <summary>
/// The toasts the reference raises, in its own words. Kept in one place so the
/// parity suite pins them together and a caller cannot quietly reword one.
/// </summary>
public static class ToastText
{
    // ---- clipboard ----
    public const string PathCopied = "Path copied to clipboard.";
    public const string BranchNameCopied = "Branch name copied to clipboard.";
    public const string LinkCopied = "Link copied to clipboard.";
    public const string ImageCopied = "Image copied to clipboard.";

    // ---- the session's directory ----
    public static string MovedTo(string directory) => $"Session moved to {directory}";
    public const string AlreadyInDirectory = "The session is already in this directory.";
    public const string DirectoryChangeFailed = "Couldn’t change the session directory.";
    public const string DirectoryHistoryAtRisk =
        "The session is in the new directory, but its history may not survive an app restart.";
    public const string DirectoryNotTrusted = "The directory couldn’t be trusted. Nothing was changed.";
    public const string DirectoryIsNetworkPath =
        "Network (UNC) paths can’t be used as a session working directory.";
    public const string DirectoryLockedToWorktree =
        "This session runs in an isolated worktree, so its directory can’t be changed. Start a new session in the other directory instead.";

    // ---- session actions ----
    public const string PullRequestFailed = "Couldn’t create the pull request. Try again.";
    public const string ArchiveFailed = "Couldn’t archive this session. Try again.";
    public const string Unarchived = "Session unarchived";
    public const string UnarchiveFailed = "Couldn’t unarchive this session. Try again.";
    public const string DeleteFailed = "Couldn’t delete this session. Try again.";
    public const string SessionLoadFailed = "Something went wrong loading this session.";
    public static string Unpinned(string name) => $"Unpinned {name}";
    public const string Undo = "Undo";
    public const string Forking = "Forking session…";
    public const string ForkUnavailable = "Fork isn’t available for this session.";

    // ---- rewind ----
    public const string RewindFailed = "Couldn’t rewind this session. Your message is back in the composer.";
    public const string RewindTargetLost = "Couldn’t go back to that message. Use Rewind on the message instead.";
    public const string RewindUnavailable = "Rewind isn’t available for this session.";
    public const string NothingToRewind = "Nothing to rewind to yet";
    public const string RewindWhileRunning = "Rewind is unavailable while Jarvis is working";

    // ---- worktrees ----
    public const string CreatingWorktree = "Creating worktree…";
    public const string AddingWorktree = "Adding worktree…";
    public const string CheckingOutWorktree = "Checking out worktree files… (large repositories may take a while)";
    public const string RunningWorktreeHook = "Running your WorktreeCreate hook…";
    public const string WorkingTreeUpdateFailed = "Couldn’t update the working tree.";

    // ---- connectors ----
    public static string Connected(string serverName) => $"Connected to {serverName}.";
    public static string Disconnected(string name) => $"“{name}” disconnected.";
    public const string ConnectorFailed =
        "Something went wrong. Try again, or run /mcp in the terminal for details.";

    // ---- bulk session housekeeping ----
    public static string ArchivedOlder(int count) =>
        count == 1 ? "Archived 1 older session" : $"Archived {count} older sessions";

    public static string ArchivedOlderPartial(int count) =>
        $"{ArchivedOlder(count)}. Sessions that haven’t loaded weren’t archived.";

    public static string DeletedOlder(int count) =>
        count == 1 ? "Deleted 1 older session" : $"Deleted {count} older sessions";

    public static string DeletedOlderPartial(int count) =>
        $"{DeletedOlder(count)}. Sessions that haven’t loaded weren’t deleted.";

    public static string ArchivedSessions(int count) =>
        count == 1 ? "Archived 1 session" : $"Archived {count} sessions";

    public static string ArchiveSessionsFailed(int count) =>
        count == 1 ? "1 session couldn’t be archived. Try again." : $"{count} sessions couldn’t be archived. Try again.";
}
