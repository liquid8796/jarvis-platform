namespace JarvisCode.Core.Utilities;

/// <summary>
/// Reads the current git branch straight from .git metadata — no git process,
/// so it is cheap enough to call on every directory change.
/// </summary>
public static class GitInfo
{
    private const string BranchRefPrefix = "ref: refs/heads/";

    /// <summary>
    /// Returns the branch name for the repository containing <paramref name="directory"/>
    /// (searching upwards), a short commit hash when HEAD is detached, or null when
    /// the directory is not inside a git repository.
    /// </summary>
    public static string? GetBranch(string directory)
    {
        try
        {
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            {
                var gitPath = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(gitPath))
                    return ReadHead(Path.Combine(gitPath, "HEAD"));
                if (File.Exists(gitPath))
                    return ReadWorktreeHead(gitPath, current.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Unreadable metadata is treated the same as "not a repository".
        }
        return null;
    }

    /// <summary>A .git *file* points at the real git dir (worktrees, submodules).</summary>
    private static string? ReadWorktreeHead(string gitFile, string baseDirectory)
    {
        var content = File.ReadAllText(gitFile).Trim();
        if (!content.StartsWith("gitdir:", StringComparison.Ordinal))
            return null;
        var gitDir = content["gitdir:".Length..].Trim();
        if (!Path.IsPathRooted(gitDir))
            gitDir = Path.GetFullPath(Path.Combine(baseDirectory, gitDir));
        return ReadHead(Path.Combine(gitDir, "HEAD"));
    }

    private static string? ReadHead(string headFile)
    {
        if (!File.Exists(headFile))
            return null;
        var head = File.ReadAllText(headFile).Trim();
        if (head.StartsWith(BranchRefPrefix, StringComparison.Ordinal))
            return head[BranchRefPrefix.Length..];
        if (head.StartsWith("ref:", StringComparison.Ordinal))
            return head[4..].Trim();
        return head.Length >= 7 ? head[..7] : (head.Length > 0 ? head : null);
    }
}
