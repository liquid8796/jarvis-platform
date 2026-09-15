using System.IO;

namespace JarvisCode.Cli;

/// <summary>Reads Git's local metadata for picker grouping, without spawning one process per saved session.</summary>
internal static class CliGitLocation
{
    public static (string Repository, string Branch, bool Worktree) Read(string directory)
    {
        try
        {
            for (var parent = new DirectoryInfo(Path.GetFullPath(directory)); parent is not null; parent = parent.Parent)
            {
                var marker = Path.Combine(parent.FullName, ".git");
                var linked = File.Exists(marker);
                if (!linked && !Directory.Exists(marker)) continue;
                var gitDirectory = marker;
                if (linked)
                {
                    var declaration = File.ReadAllText(marker).Trim();
                    if (!declaration.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return (parent.FullName, "", true);
                    gitDirectory = Path.GetFullPath(declaration[7..].Trim(), parent.FullName);
                }
                var commonFile = Path.Combine(gitDirectory, "commondir");
                var common = File.Exists(commonFile) ? Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDirectory) : gitDirectory;
                var repository = Directory.GetParent(common)?.FullName ?? parent.FullName;
                var headFile = Path.Combine(gitDirectory, "HEAD");
                var head = File.Exists(headFile) ? File.ReadAllText(headFile).Trim() : "";
                return (repository, head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? head[16..] : head, linked);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return (directory, "", false);
    }
}
