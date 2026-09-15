using System.Diagnostics;

namespace JarvisCode.Core.Agent;

/// <summary>
/// Per-agent git worktrees — the reference's <c>isolation: "worktree"</c>: a
/// fresh checkout on its own branch so agents that mutate files in parallel do
/// not collide, removed again when the agent changed nothing.
/// </summary>
public static class AgentWorktrees
{
    /// <summary>Where an agent's worktree lands, beside the repo like the session's own.</summary>
    public const string DirectoryName = ".jarvis-worktrees";

    public sealed record Worktree(string Path, string Branch, string RepoRoot, string BaseCommit);

    /// <summary>
    /// Creates a worktree for one agent. Returns null with a reason when the
    /// directory is not a git repository or git refuses.
    /// </summary>
    public static Worktree? Create(string workingDirectory, string label, out string? error)
    {
        error = null;
        var (rootExit, rootOutput) = RunGit(workingDirectory, "rev-parse --show-toplevel");
        if (rootExit != 0)
        {
            error = "isolation: \"worktree\" needs a git repository — this directory is not one.";
            return null;
        }

        var repoRoot = rootOutput.Trim();
        var slug = Slug(label);
        var unique = Guid.NewGuid().ToString("n")[..6];
        var branch = $"jarvis/agent-{slug}-{unique}";
        var path = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(repoRoot) ?? repoRoot,
            DirectoryName,
            $"{System.IO.Path.GetFileName(repoRoot)}-agent-{slug}-{unique}");

        var (headExit, head) = RunGit(repoRoot, "rev-parse HEAD");
        var baseCommit = headExit == 0 ? head.Trim() : "";

        var (exit, output) = RunGit(repoRoot, $"worktree add \"{path}\" -b {branch}");
        if (exit != 0)
        {
            error = $"git worktree add failed: {output.Trim()}";
            return null;
        }

        return new Worktree(path, branch, repoRoot, baseCommit);
    }

    /// <summary>
    /// Removes the worktree when the agent left it untouched, as the reference
    /// does; a worktree with changes is kept so the work is not lost. Returns
    /// true when it was removed.
    /// </summary>
    public static bool RemoveIfUnchanged(Worktree worktree)
    {
        var (statusExit, status) = RunGit(worktree.Path, "status --porcelain");
        if (statusExit != 0 || status.Trim().Length > 0)
            return false;

        // A branch with commits of its own counts as changed too. The check is
        // against the commit it branched from: a fresh branch has no upstream,
        // and a failed comparison must never read as "nothing was done here".
        var (headExit, head) = RunGit(worktree.Path, "rev-parse HEAD");
        if (headExit != 0)
            return false;
        if (worktree.BaseCommit.Length > 0 && !string.Equals(head.Trim(), worktree.BaseCommit, StringComparison.Ordinal))
            return false;

        var (removeExit, _) = RunGit(worktree.RepoRoot, $"worktree remove --force \"{worktree.Path}\"");
        if (removeExit != 0)
            return false;

        RunGit(worktree.RepoRoot, $"branch -D {worktree.Branch}");
        return true;
    }

    /// <summary>
    /// The main checkout behind a linked worktree. Asking the worktree itself
    /// with rev-parse --show-toplevel answers with the worktree, and a worktree
    /// cannot be removed from inside itself, so the first entry of
    /// `worktree list` is what a removal has to run in.
    /// </summary>
    public static string? MainRepoRoot(string worktreePath)
    {
        var (exit, output) = RunGit(worktreePath, "worktree list --porcelain");
        if (exit != 0)
            return null;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("worktree ", StringComparison.Ordinal))
                return trimmed["worktree ".Length..].Trim();
        }

        return null;
    }

    private static string Slug(string label)
    {
        var slug = new string(label.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (slug.Length == 0)
            return "agent";
        return slug.Length > 24 ? slug[..24].ToLowerInvariant() : slug.ToLowerInvariant();
    }

    internal static (int ExitCode, string Output) RunGit(string workingDirectory, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
                return (-1, "git could not be started");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return (process.ExitCode, output.Length > 0 ? output : error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}
