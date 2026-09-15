namespace JarvisCode.App.Services;

/// <summary>Which destructive action a confirm is being raised for.</summary>
public enum SessionDialogAction
{
    Archive,
    Delete,
}

/// <summary>What one confirm dialog should say and offer.</summary>
public sealed record DialogCopy(
    string Title,
    string Description,
    string ConfirmLabel,
    bool Danger = true,
    string? Footnote = null,
    string? Preformatted = null);

/// <summary>
/// The copy of the reference desktop's own confirm dialogs, ported from
/// <c>shared-16-DFDNRrwQ.js</c> (the uncommitted-changes and delete-session
/// confirms), <c>shared-14-CYxe7_Hl.js</c> (the group dialogs),
/// <c>shared-17-BG9iAXbK.js</c> (the discard-draft and bulk-housekeeping
/// dialogs), <c>c165c93f1-DnsL5lOk.js</c> (workspace trust) and
/// <c>c11959232-DM8o5ho4.js</c> (the Git and branch dialogs).
///
/// Free of WPF so the wording, the pluralisation and the truncation rules are
/// unit-tested rather than eyeballed.
/// </summary>
public static class SessionDialogs
{
    public const string Cancel = "Cancel";
    public const string Save = "Save";
    public const string Delete = "Delete";
    public const string Archive = "Archive";
    public const string Discard = "Discard";
    public const string Back = "Back";
    public const string GotIt = "Got it";
    public const string NotNow = "Not now";

    // ---- deleting or archiving a session ----

    public static string DeleteTitle(int count) => count == 1 ? "Delete session?" : $"Delete {count} sessions?";

    public const string ArchiveTitle = "Archive session?";

    public static string DeleteBody(string sessionTitle) =>
        $"“{sessionTitle}” will be permanently deleted. This can’t be undone.";

    // ---- the worktree that still holds changes ----

    /// <summary>The reference shows at most ten paths and counts the rest.</summary>
    public const int MaxListedPaths = 10;

    public const string CheckingForChanges = "Checking for uncommitted changes…";

    public const string MoreSessionsFollow = "More sessions with uncommitted changes will follow.";

    public static string UncommittedTitle(SessionDialogAction action) => action == SessionDialogAction.Archive
        ? "Archive session with uncommitted changes?"
        : "Delete session with uncommitted changes?";

    public static string UncommittedBody(int count) => count == 1
        ? "This session’s worktree has 1 uncommitted change that will be permanently discarded."
        : $"This session’s worktree has {count} uncommitted changes that will be permanently discarded.";

    public static string UncommittedConfirmLabel(SessionDialogAction action) => action == SessionDialogAction.Archive
        ? "Archive anyway"
        : "Delete anyway";

    /// <summary>The dialog's <c>&lt;pre&gt;</c>: ten paths, then how many were left out.</summary>
    public static string PathList(IReadOnlyList<string> paths)
    {
        var shown = paths.Take(MaxListedPaths).ToList();
        var more = paths.Count - MaxListedPaths;
        var body = string.Join("\n", shown);
        return more > 0 ? $"{body}\n… and {more} more" : body;
    }

    /// <summary>
    /// While the check is still running the reference shows the plain title, no
    /// list, a primary confirm button and a dialog that cannot be committed.
    /// </summary>
    public static DialogCopy Checking(SessionDialogAction action) => new(
        action == SessionDialogAction.Archive ? ArchiveTitle : DeleteTitle(1),
        CheckingForChanges,
        action == SessionDialogAction.Archive ? Archive : Delete,
        Danger: false);

    public static DialogCopy Uncommitted(
        SessionDialogAction action, IReadOnlyList<string> paths, bool moreSessionsFollow) => new(
        UncommittedTitle(action),
        UncommittedBody(paths.Count),
        UncommittedConfirmLabel(action),
        Footnote: moreSessionsFollow ? MoreSessionsFollow : null,
        Preformatted: PathList(paths));

    // ---- session groups ----

    public const string RenameGroupTitle = "Rename group";
    public const string NewGroupTitle = "New group";
    public const string GroupNamePlaceholder = "Group name";
    public const string DeleteGroupTitle = "Delete group?";

    /// <summary>The menu row that raises it, which the reference words without the question mark.</summary>
    public const string DeleteGroupMenuItem = "Delete group";

    public static string DeleteGroupBody(string groupName, int sessionCount) => sessionCount switch
    {
        0 => $"“{groupName}” will be removed. It contains no sessions.",
        1 => $"“{groupName}” will be removed. The 1 session in it will no longer be grouped.",
        _ => $"“{groupName}” will be removed. The {sessionCount} sessions in it will no longer be grouped.",
    };

    // ---- bulk housekeeping ----

    public const string ArchiveOlderTitle = "Archive older sessions?";
    public const string DeleteOlderTitle = "Delete older sessions?";
    public const string CountingOlder = "Counting sessions older than a week…";
    public const string ArchiveAll = "Archive all";
    public const string DeleteAll = "Delete all";

    /// <summary>The trailing sentence the reference adds when the listing was cut short.</summary>
    private const string NotAllLoadedArchive =
        " Not all sessions could be loaded, so some older sessions will remain.";

    public static string ArchiveOlderBody(int count, bool truncated)
    {
        var sessions = count == 1 ? "1 session" : $"{count} sessions";
        return $"This will archive {sessions} older than a week. Pinned sessions are not affected." +
               (truncated ? NotAllLoadedArchive : "");
    }

    public static string DeleteOlderBody(int count, bool truncated)
    {
        var sessions = count == 1 ? "1 session" : $"{count} sessions";
        return $"This will permanently delete {sessions} older than a week. Pinned sessions are not affected. " +
               "This cannot be undone." + (truncated ? NotAllLoadedArchive : "");
    }

    // ---- an unsent draft ----

    public const string DiscardDraftTitle = "Discard draft?";
    public const string KeepEditing = "Keep editing";
    public const string StartSession = "Start session";

    /// <summary>The reference collapses whitespace and cuts the preview at 60 characters.</summary>
    public static string DraftPreview(string draft)
    {
        var collapsed = string.Join(" ", (draft ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 60 ? collapsed[..60] + "…" : collapsed;
    }

    public static string DiscardDraftBody(string? draft)
    {
        var preview = draft is { Length: > 0 } text ? DraftPreview(text) : "";
        return preview.Length > 0
            ? $"“{preview}” hasn’t been sent. Start a session from it, or discard it?"
            : "This draft hasn’t been sent. Start a session from it, or discard it?";
    }

    // ---- workspace trust ----

    public const string TrustTitle = "Trust this workspace?";

    public const string TrustBody =
        "Jarvis Code may read, write, or execute files in this folder. Only proceed if you trust this workspace.";

    public const string TrustConfirm = "Trust workspace";

    /// <summary>
    /// The reference's footnote links "security guide" to its own documentation.
    /// This build ships no such page, so the sentence is carried without the
    /// link rather than pointing at another product's docs.
    /// </summary>
    public const string TrustFootnote = "Read our security guide for more information.";

    public const string ExecutionAllowedBy = "Execution allowed by:";

    public const string ResumeImportedTitle = "Resume imported session?";

    /// <summary>The button that answers it.</summary>
    public const string Resume = "Resume";

    public const string ResumeImportedBody =
        "This session was imported. Resuming lets Jarvis act on its history — only continue if you trust where it " +
        "came from.";

    // ---- git ----

    public const string GitBlockedTitle = "Git was blocked";
    public const string GitBlockedBody = "Your system blocked Jarvis from running Git.";
    public const string DownloadGit = "Download Git";

    public static string GitRequiredBody(string environmentVariable) =>
        $"Git for Windows is required to run local sessions. If it’s already installed, set the " +
        $"{environmentVariable} environment variable to the full path of bash.exe and restart the app — or switch " +
        "to a remote environment.";

    // ---- switching branches with a dirty tree ----

    public static string UncommittedOnBranchTitle(string currentBranch) =>
        $"Uncommitted changes on {currentBranch}";

    public static string HandleThemBody(string targetBranch) => $"Handle them before switching to {targetBranch}.";

    public static string OtherSessionsUsingFolder(int count) => count == 1
        ? "1 other session is using this folder. Switching branches will affect it too."
        : $"{count} other sessions are using this folder. Switching branches will affect them too.";

    public static string FilesChanged(int count) => count == 1 ? "1 file changed" : $"{count} files changed";

    public const string StashChanges = "Stash changes";
    public const string CommitAsWip = "Commit as WIP";
    public const string DiscardChanges = "Discard changes";
    public const string DiscardConfirmTitle = "Discard uncommitted changes?";

    public static string DiscardConfirmBody(string currentBranch) =>
        $"All uncommitted changes on {currentBranch} will be permanently lost.";

    // ---- a session whose folder is gone ----

    public const string SessionNotFoundTitle = "Session not found on disk";
    public const string SessionNotFoundBody = "Send a message to start fresh in this directory.";
    public const string ImportCliSessions = "Import CLI sessions";

    /// <summary>The folder picker the "Change project directory" action opens.</summary>
    public const string ChangeProjectDirectoryTitle = "Change project directory";

    /// <summary>The picker /add-dir opens.</summary>
    public const string AddDirectoryTitle = "Add directory";
}
