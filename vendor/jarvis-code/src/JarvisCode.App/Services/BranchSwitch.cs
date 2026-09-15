using System.IO;
using System.Diagnostics;

namespace JarvisCode.App.Services;

/// <summary>Which answer the branch-switch dialog's split button is on.</summary>
public enum DirtyTreeResolution
{
    Stash,
    Commit,
    Discard,
}

/// <summary>One row of <c>git status --porcelain=v1</c>, shaped the way the reference's dialog reads it.</summary>
public sealed record WorkingTreeEntry(string Status, string Path, bool Unmerged);

/// <summary>
/// What the reference's <c>getWorkingTreeStatus</c> answers: the changed paths and
/// the <c>git diff --numstat HEAD</c> totals the dialog prints beside the count.
/// </summary>
public sealed record WorkingTreeStatus(IReadOnlyList<WorkingTreeEntry> Files, int Additions, int Deletions)
{
    public static readonly WorkingTreeStatus Empty = new([], 0, 0);
}

/// <summary>
/// The reference desktop's branch-switch gate (app.asar
/// <c>index.chunk-B28p2L31.js</c>: its <c>wouldBranchSwitchConflict</c>,
/// <c>getWorkingTreeStatus</c>, <c>stashWorkingTree</c>,
/// <c>commitWipForBranchSwitch</c> and <c>discardWorkingTree</c>, with the
/// renderer's dirty-tree dialog in the ccd chunk <c>c11959232-DM8o5ho4.js</c>
/// supplying the stash message and the three answers).
///
/// The decisions are pure so they are unit-tested; running git is a thin shell
/// over them.
/// </summary>
public static class BranchSwitch
{
    /// <summary>The reference's stash message: <c>epitaxy: pre-switch from {branch}</c>.</summary>
    public static string StashMessage(string currentBranch) => $"epitaxy: pre-switch from {currentBranch}";

    /// <summary>The reference's WIP commit subject.</summary>
    public static string WipCommitMessage(string currentBranch) => $"WIP: epitaxy pre-switch from {currentBranch}";

    /// <summary>
    /// The reference's path-collision helper: does any dirty path collide with a
    /// path the switch would rewrite? A dirty entry matches when the other side
    /// changed the same path, one of its parent directories, or a directory the
    /// dirty path sits under.
    /// </summary>
    public static bool Collides(IEnumerable<string> dirtyPaths, IEnumerable<string> changedPaths)
    {
        var changed = new HashSet<string>(changedPaths, StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in changed)
        {
            for (var i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1))
            {
                directories.Add(path[..i]);
            }
        }

        foreach (var raw in dirtyPaths)
        {
            var path = raw.EndsWith('/') ? raw[..^1] : raw;
            if (changed.Contains(path) || directories.Contains(path))
            {
                return true;
            }

            for (var i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1))
            {
                if (changed.Contains(path[..i]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The reference's <c>wouldBranchSwitchConflict</c>, minus the git calls: a
    /// target that reads as an option is refused, a clean tree never conflicts, an
    /// unmerged entry always does, a target that could not be resolved is treated
    /// as a conflict, and otherwise the dirty paths are intersected with what the
    /// switch would rewrite.
    /// </summary>
    public static bool WouldConflict(
        string targetBranch,
        WorkingTreeStatus status,
        bool targetResolved,
        IReadOnlyList<string> changedBetween)
    {
        if (targetBranch.StartsWith('-'))
        {
            return true;
        }

        if (status.Files.Count == 0)
        {
            return false;
        }

        if (status.Files.Any(static f => f.Unmerged))
        {
            return true;
        }

        return !targetResolved || Collides(status.Files.Select(static f => f.Path), changedBetween);
    }

    /// <summary>The dialog's changed-file row.</summary>
    public static string FilesChanged(int count) => SessionDialogs.FilesChanged(count);

    /// <summary>The accessible label the reference gives the row's ± pair.</summary>
    public static string DiffLabel(int additions, int deletions) =>
        $"{additions} additions, {deletions} deletions";

    /// <summary>The reference's split-button labels, in its order.</summary>
    public static string ResolutionLabel(DirtyTreeResolution resolution) => resolution switch
    {
        DirtyTreeResolution.Stash => SessionDialogs.StashChanges,
        DirtyTreeResolution.Commit => SessionDialogs.CommitAsWip,
        _ => SessionDialogs.DiscardChanges,
    };

    /// <summary>The failure line all three answers share when git refuses.</summary>
    public const string ResolveFailed = "Couldn’t update the working tree.";

    /// <summary>The git arguments each answer runs, in the reference's order.</summary>
    public static IReadOnlyList<string[]> Commands(DirtyTreeResolution resolution, string currentBranch) =>
        resolution switch
        {
            DirtyTreeResolution.Stash => [["stash", "push", "-u", "-m", StashMessage(currentBranch)]],
            DirtyTreeResolution.Commit =>
                [["add", "-A"], ["commit", "-m", WipCommitMessage(currentBranch), "--no-verify"]],
            _ => [["reset", "--hard"], ["clean", "-fd", "--", ":/"]],
        };

    /// <summary>
    /// The reference treats "no local changes to save" and "nothing to commit" as
    /// success rather than as a refusal.
    /// </summary>
    public static bool IsBenign(string output) =>
        output.Contains("no local changes to save", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("nothing added to commit", StringComparison.OrdinalIgnoreCase);

    // ---- the thin git shell over the decisions above ----

    /// <summary>Reads <c>git status --porcelain=v1</c> and the numstat totals.</summary>
    public static WorkingTreeStatus ReadStatus(string cwd)
    {
        var porcelain = Run(cwd, ["status", "--porcelain=v1"]);
        var files = new List<WorkingTreeEntry>();
        foreach (var line in porcelain.Output.Split('\n'))
        {
            if (line.Length < 4)
            {
                continue;
            }

            var xy = line[..2];
            var path = line[3..].Trim();
            if (path.Length == 0)
            {
                continue;
            }

            files.Add(new WorkingTreeEntry(xy, path, IsUnmerged(xy)));
        }

        var numstat = Run(cwd, ["diff", "--no-textconv", "--numstat", "HEAD"]);
        var added = 0;
        var removed = 0;
        foreach (var line in numstat.Output.Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            if (int.TryParse(parts[0], out var a))
            {
                added += a;
            }

            if (int.TryParse(parts[1], out var d))
            {
                removed += d;
            }
        }

        return new WorkingTreeStatus(files, added, removed);
    }

    /// <summary>git's own unmerged XY pairs, which the reference reads as an outright conflict.</summary>
    public static bool IsUnmerged(string xy) =>
        xy is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU";

    /// <summary>Whether switching to <paramref name="target"/> would collide with the tree.</summary>
    public static bool WouldConflict(string cwd, string target, WorkingTreeStatus status)
    {
        var commit = ResolveCommit(cwd, target);
        IReadOnlyList<string> changed = commit is null
            ? []
            : [.. Run(cwd, ["diff", "--name-only", "--no-renames", "HEAD", commit]).Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static l => l.Trim())
                .Where(static l => l.Length > 0)];
        return WouldConflict(target, status, commit is not null, changed);
    }

    /// <summary>The reference's <c>resolveBranchTargetCommit</c>: the local ref, then origin's.</summary>
    private static string? ResolveCommit(string cwd, string target)
    {
        foreach (var reference in new[] { $"refs/heads/{target}", $"refs/remotes/origin/{target}" })
        {
            var sha = Run(cwd, ["rev-parse", "--verify", "--quiet", reference + "^{commit}"]).Output.Trim();
            if (sha.Length > 0)
            {
                return sha;
            }
        }

        return null;
    }

    /// <summary>Runs one answer's commands; null on success, otherwise the reference's failure line.</summary>
    public static string? Resolve(string cwd, DirtyTreeResolution resolution, string currentBranch)
    {
        foreach (var arguments in Commands(resolution, currentBranch))
        {
            var result = Run(cwd, arguments);
            if (result.ExitCode == 0 || IsBenign(result.Output))
            {
                continue;
            }

            // reset --hard failing is fatal; the clean that follows it is not,
            // which is the reference logging a warning and carrying on.
            if (resolution == DirtyTreeResolution.Discard && arguments[0] == "clean")
            {
                continue;
            }

            return ResolveFailed;
        }

        return null;
    }

    internal static (int ExitCode, string Output) Run(string cwd, IReadOnlyList<string> arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (1, "");
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return (process.HasExited ? process.ExitCode : 1, output);
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (1, "");
        }
    }
}
