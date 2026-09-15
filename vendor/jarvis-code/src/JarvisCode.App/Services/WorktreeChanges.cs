using System.Diagnostics;
using System.IO;

namespace JarvisCode.App.Services;

/// <summary>
/// What a session's worktree still holds that has not been committed. The
/// reference asks git this before it deletes or archives a session whose folder
/// is a worktree, and shows the answer in the confirm dialog.
/// </summary>
public static class WorktreeChanges
{
    /// <summary>
    /// Whether the folder is a linked worktree rather than an ordinary checkout:
    /// a linked worktree's <c>.git</c> is a file pointing back at the repository.
    /// </summary>
    public static bool IsLinkedWorktree(string directory)
    {
        try
        {
            return Directory.Exists(directory) && File.Exists(Path.Combine(directory, ".git"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The paths git reports as changed, in porcelain order. An empty list means
    /// nothing would be lost; a folder that is not a worktree is not asked at all.
    /// </summary>
    public static async Task<IReadOnlyList<string>> UncommittedAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        if (!IsLinkedWorktree(directory))
        {
            return [];
        }

        try
        {
            var psi = new ProcessStartInfo("git", "status --porcelain")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return [];
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? Parse(output) : [];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or IOException or OperationCanceledException)
        {
            // Without git there is nothing to warn about; the confirm falls back
            // to the plain one.
            return [];
        }
    }

    /// <summary>
    /// The path out of each porcelain row. A rename reads "R  old -> new"; the
    /// reference lists the destination, which is the file that would be lost.
    /// </summary>
    public static IReadOnlyList<string> Parse(string porcelain)
    {
        var paths = new List<string>();
        foreach (var line in (porcelain ?? "").Split('\n'))
        {
            var row = line.TrimEnd('\r');
            if (row.Length <= 3)
            {
                continue;
            }

            var path = row[3..].Trim();
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                path = path[(arrow + 4)..];
            }

            paths.Add(path.Trim('"'));
        }

        return paths;
    }
}
