using System.IO;

namespace JarvisCode.App.Services;

/// <summary>
/// Where Git Bash is, and whether it is anywhere. The reference's Install Git
/// dialog tells the user to point <c>CLAUDE_CODE_GIT_BASH_PATH</c> at bash.exe, and
/// its CLI reads that variable too, so this build honours it under the reference's
/// own name — the same rule every other <c>CLAUDE_CODE_*</c> variable follows here.
/// </summary>
public static class GitBash
{
    /// <summary>The reference's own variable for a Git installed somewhere unusual.</summary>
    public const string PathVariable = "CLAUDE_CODE_GIT_BASH_PATH";

    /// <summary>Where to download it, which is where the reference's dialog sends the user.</summary>
    public const string DownloadUrl = "https://git-scm.com/download/win";

    /// <summary>The bash this machine would run, or null when it has none.</summary>
    public static string? Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "/bin/bash";
        }

        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured &&
            File.Exists(configured))
        {
            return configured;
        }

        foreach (var candidate in new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "bash.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry; the next one may still be good.
            }
        }

        return null;
    }

    public static bool IsAvailable() => Resolve() is not null;
}
