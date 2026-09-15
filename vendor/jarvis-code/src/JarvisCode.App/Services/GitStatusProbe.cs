using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// Reads what the PR bar draws out of the working copy: `git` for the repository,
/// branch, uncommitted diff and how far behind the base the branch is, and `gh` for
/// the pull request and its checks. Everything here is best effort — a folder that is
/// not a repository, a missing `gh` or an unauthenticated one all answer with what is
/// known and leave the rest at its default, which is what puts the bar into the
/// reference's commit mode rather than failing.
/// </summary>
public static class GitStatusProbe
{
    /// <summary>Whether the GitHub CLI is on PATH at all — the reference's `checkGhAvailable`.</summary>
    public static bool GhAvailable(string workingDirectory) =>
        Run(workingDirectory, "gh", "--version").Exit == 0;

    public static GitBarInput Read(string workingDirectory, string createMode, bool lookUpPullRequest = true)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return new GitBarInput { IsGitRepo = false };
        }

        var branch = Run(workingDirectory, "git", "rev-parse --abbrev-ref HEAD");
        if (branch.Exit != 0 || branch.Output.Trim() is not { Length: > 0 } branchName)
        {
            return new GitBarInput { IsGitRepo = false };
        }

        var root = Run(workingDirectory, "git", "rev-parse --show-toplevel");
        var repoName = root.Exit == 0
            ? Path.GetFileName(root.Output.Trim().Replace('/', '\\').TrimEnd('\\'))
            : "";

        var (added, removed) = ReadDiff(workingDirectory);
        var remote = Run(workingDirectory, "git", "remote get-url origin");
        var repoUrl = remote.Exit == 0 ? NormalizeRemote(remote.Output.Trim()) : null;
        var upstream = Run(workingDirectory, "git", "rev-parse --abbrev-ref --symbolic-full-name @{u}");
        var branchNotPushed = upstream.Exit != 0;

        var baseBranch = ReadBaseBranch(workingDirectory);
        var behind = baseBranch is null || branchNotPushed ? 0 : ReadBehind(workingDirectory, baseBranch);

        var input = new GitBarInput
        {
            IsGitRepo = true,
            HasGithubRepo = repoUrl is not null,
            LocalOnly = repoUrl is null,
            RepoName = repoName,
            Cwd = workingDirectory,
            BranchName = branchName,
            BaseBranch = baseBranch,
            RepoUrl = repoUrl,
            BranchUrl = repoUrl is null ? null : $"{repoUrl}/tree/{Uri.EscapeDataString(branchName)}",
            Added = added,
            Removed = removed,
            HasChanges = added > 0 || removed > 0,
            Behind = behind,
            BranchNotPushed = branchNotPushed,
            OnBaseBranch = baseBranch is not null && string.Equals(branchName, baseBranch, StringComparison.Ordinal),
            CreateMode = createMode,
        };

        return lookUpPullRequest && repoUrl is not null ? input with { Pr = ReadPullRequest(workingDirectory) } : input;
    }

    /// <summary>The uncommitted diff, the way the reference counts it (`git diff HEAD --shortstat`).</summary>
    public static (int Added, int Removed) ReadDiff(string workingDirectory)
    {
        var stat = Run(workingDirectory, "git", "diff HEAD --shortstat");
        if (stat.Exit != 0)
        {
            return (0, 0);
        }

        var adds = Regex.Match(stat.Output, @"(\d+) insertion");
        var dels = Regex.Match(stat.Output, @"(\d+) deletion");
        return (adds.Success ? int.Parse(adds.Groups[1].Value) : 0,
            dels.Success ? int.Parse(dels.Groups[1].Value) : 0);
    }

    /// <summary>The repository's default branch, from the origin HEAD symbolic ref.</summary>
    public static string? ReadBaseBranch(string workingDirectory)
    {
        var head = Run(workingDirectory, "git", "symbolic-ref --short refs/remotes/origin/HEAD");
        if (head.Exit == 0 && head.Output.Trim() is { Length: > 0 } text)
        {
            var slash = text.LastIndexOf('/');
            return slash >= 0 ? text[(slash + 1)..] : text;
        }

        return null;
    }

    private static int ReadBehind(string workingDirectory, string baseBranch)
    {
        var counts = Run(workingDirectory, "git", $"rev-list --left-right --count origin/{baseBranch}...HEAD");
        if (counts.Exit != 0)
        {
            return 0;
        }

        var parts = counts.Output.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && int.TryParse(parts[0], out var behind) ? behind : 0;
    }

    /// <summary>An https URL for the origin remote, whichever spelling git stores.</summary>
    public static string? NormalizeRemote(string remote)
    {
        if (remote.Length == 0)
        {
            return null;
        }

        var url = remote;
        if (url.StartsWith("git@", StringComparison.Ordinal))
        {
            var colon = url.IndexOf(':');
            if (colon < 0)
            {
                return null;
            }

            url = $"https://{url[4..colon]}/{url[(colon + 1)..]}";
        }

        if (url.EndsWith(".git", StringComparison.Ordinal))
        {
            url = url[..^4];
        }

        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
    }

    /// <summary>
    /// The branch's pull request as `gh` reports it, with its checks folded into the
    /// counts the CI dot reads. A missing `gh`, a missing PR or an unauthenticated
    /// CLI all answer null, which is what leaves the bar in its create mode.
    /// </summary>
    public static PrInfo? ReadPullRequest(string workingDirectory)
    {
        var result = Run(workingDirectory, "gh",
            "pr view --json number,url,state,title,isDraft,mergeable,reviewDecision,baseRefName,statusCheckRollup");
        if (result.Exit != 0 || result.Output.Trim().Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            return FromJson(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The fields the bar reads off one pull request.</summary>
    private const string PrFields =
        "number,url,state,title,isDraft,mergeable,reviewDecision,baseRefName,statusCheckRollup";

    /// <summary>
    /// A named branch's pull request, which is how the reference finds a stack: the
    /// row it adds for a stacked PR is another bar row on the parent branch, and
    /// that row looks its own PR up the same way.
    /// </summary>
    public static PrInfo? ReadPullRequestFor(string workingDirectory, string branch)
    {
        var result = Run(workingDirectory, "gh", $"pr view {branch} --json {PrFields}");
        if (result.Exit != 0 || result.Output.Trim().Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            return FromJson(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The stack above this pull request: follow <c>baseRefName</c> while it names a
    /// branch that is neither this one nor the repository's base, taking each
    /// parent's own pull request. Capped at the reference's five extra rows, and it
    /// refuses to revisit a branch so a cycle cannot spin.
    /// </summary>
    public static IReadOnlyList<RelatedPr> ReadStack(
        string workingDirectory, PrInfo? pr, string branch, string? baseBranch)
    {
        var rows = new List<RelatedPr>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { branch };
        var current = pr;
        var currentBranch = branch;
        while (rows.Count < GitBarPresentation.MaxExtraRows &&
               GitBarPresentation.HasStackParent(current?.BaseRefName, currentBranch, baseBranch) &&
               current?.BaseRefName is { Length: > 0 } parentBranch &&
               visited.Add(parentBranch))
        {
            var parent = ReadPullRequestFor(workingDirectory, parentBranch);
            if (parent is null)
            {
                break;
            }

            rows.Add(new RelatedPr(
                parent.Number, parent.Url, parent.State, parentBranch, parent.BaseRefName, Stacked: true));
            current = parent;
            currentBranch = parentBranch;
        }

        return rows;
    }

    /// <summary>Reads one `gh pr view --json …` object; separated out so it is unit-testable.</summary>
    public static PrInfo? FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("number", out var numberValue) ||
            !numberValue.TryGetInt32(out var number))
        {
            return null;
        }

        var state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
        var draft = root.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;
        var mergeable = root.TryGetProperty("mergeable", out var m) ? m.GetString() : null;
        var review = root.TryGetProperty("reviewDecision", out var r) ? r.GetString() : null;

        var display = DisplayState(state, draft, mergeable, review);
        var checks = root.TryGetProperty("statusCheckRollup", out var rollup) ? CountChecks(rollup) : null;

        return new PrInfo(
            number,
            root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
            display,
            root.TryGetProperty("title", out var t) ? t.GetString() : null,
            root.TryGetProperty("baseRefName", out var b) ? b.GetString() : null,
            checks);
    }

    /// <summary>
    /// The reference widens the raw GitHub state with the review decision and the
    /// mergeability before it picks an icon: a conflicting or changes-requested open
    /// PR reads as those rather than as "Open".
    /// </summary>
    public static PrDisplayState DisplayState(string state, bool draft, string? mergeable, string? reviewDecision) =>
        state.ToUpperInvariant() switch
        {
            "MERGED" => PrDisplayState.Merged,
            "CLOSED" => PrDisplayState.Closed,
            _ when draft => PrDisplayState.Draft,
            _ when string.Equals(reviewDecision, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase)
                => PrDisplayState.ChangesRequested,
            _ when string.Equals(mergeable, "CONFLICTING", StringComparison.OrdinalIgnoreCase)
                => PrDisplayState.Conflicting,
            _ when string.Equals(reviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase)
                => PrDisplayState.Approved,
            _ => PrDisplayState.Open,
        };

    /// <summary>The pass/fail conclusions the reference's `pM` and `uM` sets name.</summary>
    private static readonly HashSet<string> Passing = new(StringComparer.OrdinalIgnoreCase)
        { "SUCCESS", "NEUTRAL" };

    private static readonly HashSet<string> Failing = new(StringComparer.OrdinalIgnoreCase)
        { "FAILURE", "TIMED_OUT", "CANCELLED", "ACTION_REQUIRED", "STARTUP_FAILURE" };

    /// <summary>
    /// Counts a `statusCheckRollup` array the reference's way (`mM`): a status context
    /// is read from its state, a check run from its conclusion once it has completed,
    /// and anything else is still pending.
    /// </summary>
    public static CiCounts? CountChecks(JsonElement rollup)
    {
        if (rollup.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int passed = 0, failed = 0, pending = 0, skipped = 0;
        foreach (var check in rollup.EnumerateArray())
        {
            switch (CheckState(check))
            {
                case "pass":
                    passed++;
                    break;
                case "fail":
                    failed++;
                    break;
                case "skipping":
                    skipped++;
                    break;
                default:
                    pending++;
                    break;
            }
        }

        return new CiCounts(passed, failed, pending, skipped);
    }

    private static string CheckState(JsonElement check)
    {
        var typename = check.TryGetProperty("__typename", out var tn) ? tn.GetString() : null;
        if (string.Equals(typename, "StatusContext", StringComparison.Ordinal))
        {
            var state = (check.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "").ToUpperInvariant();
            return state switch
            {
                "SUCCESS" => "pass",
                "FAILURE" or "ERROR" => "fail",
                _ => "pending",
            };
        }

        var status = (check.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "").ToUpperInvariant();
        if (status != "COMPLETED")
        {
            return "pending";
        }

        var conclusion = check.TryGetProperty("conclusion", out var c) ? c.GetString() ?? "" : "";
        if (string.Equals(conclusion, "SKIPPED", StringComparison.OrdinalIgnoreCase))
        {
            // The reference's fourth bucket. Its own checks arrive already bucketed
            // from its server; gh reports a conclusion, so this mapping is ours.
            return "skipping";
        }

        if (Passing.Contains(conclusion))
        {
            return "pass";
        }

        return Failing.Contains(conclusion) ? "fail" : "pending";
    }

    private static (int Exit, string Output) Run(string workingDirectory, string file, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, "");
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, "");
            }

            if (!Task.WaitAll([output, error], 15000)) return (-1, "");
            return (process.ExitCode, output.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, "");
        }
    }
}
