namespace JarvisCode.App.Services;

/// <summary>What a row of the working-directory menu does.</summary>
public enum WorkingDirectoryAction
{
    OpenInVsCode,
    OpenInExplorer,
    CopyPath,
    ChangeDirectory,
    OpenRepoOnGithub,
    OpenInTerminal,
    CopyBranchName,
}

/// <summary>One row of the menu, or a rule when <see cref="SeparatorBefore"/> opens it.</summary>
public sealed record WorkingDirectoryRow(string Label, WorkingDirectoryAction Action, bool SeparatorBefore = false);

/// <summary>What the menu reads about the folder it describes.</summary>
public sealed record WorkingDirectoryContext
{
    /// <summary>The folder's own name, shown as the group label when a session has several.</summary>
    public string? RepoName { get; init; }

    /// <summary>True when the session has more than one folder, which is what puts the name in the label.</summary>
    public bool Multi { get; init; }

    public string? Cwd { get; init; }

    public string? Branch { get; init; }

    /// <summary>The https URL of the GitHub repository, when there is one.</summary>
    public string? RepoUrl { get; init; }

    /// <summary>The session's directory can still be moved (the reference's `changeCwdSessionRef`).</summary>
    public bool CanChangeDirectory { get; init; }

    /// <summary>The terminal tile can be pointed at this folder.</summary>
    public bool CanOpenInTerminal { get; init; }
}

/// <summary>
/// The "Working directory" menu the repo chip opens (desktop 1.40609.1.0: the group
/// `rb` at `ca80fca8d`@279401 over the item builder `Vm`@153322, plus the two rows
/// the PR bar's repo chip appends at 193381 and 193887). Its order is the editor and
/// reveal rows, Copy path, Change directory, Open repo on GitHub, then Open in
/// terminal behind a rule — and Copy branch name last, which is `rb`'s own.
/// </summary>
public static class WorkingDirectoryMenu
{
    public const string GroupLabel = "Working directory";
    public const string OpenInVsCode = "VS Code";
    public const string Explorer = "Explorer";
    public const string CopyPath = "Copy path";
    public const string ChangeDirectory = "Change directory";
    public const string OpenRepoOnGithub = "Open repo on GitHub";
    public const string OpenInTerminal = "Open in terminal";
    public const string CopyBranchName = "Copy branch name";

    /// <summary>The toasts the two copy rows raise.</summary>
    public const string PathCopied = "Path copied to clipboard.";

    public const string BranchNameCopied = "Branch name copied to clipboard.";

    /// <summary>The folder picker's title, and what the move can answer with.</summary>
    public const string ChangeDirectoryTitle = "Change project directory";

    public const string NetworkPathRefused =
        "Network (UNC) paths can’t be used as a session working directory.";

    public const string AlreadyInDirectory = "The session is already in this directory.";

    public const string ChangeDirectoryFailed = "Couldn’t change the session directory.";

    public static string MovedTo(string directory) => $"Session moved to {directory}";

    /// <summary>The label above the rows: the folder's name once a session has several, else the fixed one.</summary>
    public static string Label(WorkingDirectoryContext c) =>
        c.Multi && !string.IsNullOrEmpty(c.RepoName) ? c.RepoName! : GroupLabel;

    public static IReadOnlyList<WorkingDirectoryRow> Build(WorkingDirectoryContext c)
    {
        var rows = new List<WorkingDirectoryRow>();
        if (!string.IsNullOrEmpty(c.Cwd))
        {
            rows.Add(new WorkingDirectoryRow(OpenInVsCode, WorkingDirectoryAction.OpenInVsCode));
            rows.Add(new WorkingDirectoryRow(Explorer, WorkingDirectoryAction.OpenInExplorer));
            rows.Add(new WorkingDirectoryRow(CopyPath, WorkingDirectoryAction.CopyPath));
        }

        if (c.CanChangeDirectory)
        {
            rows.Add(new WorkingDirectoryRow(ChangeDirectory, WorkingDirectoryAction.ChangeDirectory));
        }

        if (!string.IsNullOrEmpty(c.RepoUrl))
        {
            rows.Add(new WorkingDirectoryRow(OpenRepoOnGithub, WorkingDirectoryAction.OpenRepoOnGithub));
        }

        if (c.CanOpenInTerminal)
        {
            rows.Add(new WorkingDirectoryRow(OpenInTerminal, WorkingDirectoryAction.OpenInTerminal,
                SeparatorBefore: true));
        }

        if (!string.IsNullOrEmpty(c.Branch))
        {
            rows.Add(new WorkingDirectoryRow(CopyBranchName, WorkingDirectoryAction.CopyBranchName));
        }

        return rows;
    }

    /// <summary>
    /// The repo chip's tooltip: the reference's `mb` joins the folder path and the
    /// repository names with a middle dot, and drops the names when they say nothing
    /// the chip's own label does not.
    /// </summary>
    public static string? ChipTooltip(string? cwd, IReadOnlyList<string> repoNames, string label)
    {
        var joined = string.Join(", ", repoNames);
        var interesting = joined.Length > 0 && (repoNames.Count > 1 || joined != label);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(cwd))
        {
            parts.Add(cwd!);
        }

        if (interesting)
        {
            parts.Add(joined);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

/// <summary>
/// The one-time prompt the reference raises when the GitHub CLI is missing (desktop
/// 1.40609.1.0: `Km`/`zm` at `ca80fca8d`@149600). Its answer is remembered under the
/// reference's own key so the card is offered once per installation.
/// </summary>
public static class GitHubCliPrompt
{
    /// <summary>The reference's dismissal key, kept verbatim so the choice reads the same.</summary>
    public const string DismissedKey = "epitaxy-gh-install-dismissed";

    public const string Title = "Install the GitHub CLI";

    public const string Description =
        "The GitHub CLI enables creating PRs, monitoring CI, and merging directly from the desktop app.";

    public const string Confirm = "Open cli.github.com";

    public const string Installing = "Installing…";

    public const string Cancel = "Skip";

    public const string InstallFailed = "Installation failed. Visit cli.github.com to install manually.";

    public const string Url = "https://cli.github.com";
}
