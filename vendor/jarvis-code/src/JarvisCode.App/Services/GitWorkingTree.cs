using System.Diagnostics;
using System.IO;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// Whether the session's working tree has uncommitted changes. The pane rail's diff
/// toggle grows the reference's activity dot while it does, and its tooltip then reads
/// "Diff (uncommitted changes)" instead of "Diff".
/// </summary>
public static class GitWorkingTree
{
    /// <summary>True when git reports anything at all in the working tree.</summary>
    public static async Task<bool> IsDirtyAsync(string workingDirectory)
    {
        if (workingDirectory.Length == 0 || !Directory.Exists(workingDirectory))
        {
            return false;
        }

        var output = await RunAsync(workingDirectory, "status --porcelain");
        return output is { Length: > 0 } && output.AsSpan().TrimStart().Length > 0;
    }

    private static async Task<string?> RunAsync(string workingDirectory, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
