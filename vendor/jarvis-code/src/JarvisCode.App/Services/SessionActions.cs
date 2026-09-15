using System.Diagnostics;
using System.IO;
using JarvisCode.App.Composition;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Services;

/// <summary>
/// Session operations shared between the session-header menu and the window's
/// keyboard shortcuts (Ctrl+Alt+O fork, Ctrl+Alt+G open PR).
/// </summary>
internal static class SessionActions
{
    /// <summary>
    /// Copies a session — history, model, directories — into a new one, saved
    /// to disk. Returns the fork, or an error line instead when saving failed.
    /// </summary>
    public static async Task<(Session? Fork, string? Error)> ForkAsync(AppServices services, Session source)
    {
        var fork = Session.CreateNew(source.WorkingDirectory);
        fork.Title = source.Title.EndsWith(" (fork)", StringComparison.Ordinal) ? source.Title : $"{source.Title} (fork)";
        fork.ModelId = source.ModelId;
        fork.AdditionalDirectories = [.. source.AdditionalDirectories];
        fork.Messages = [.. source.Messages];

        try
        {
            await services.Sessions.SaveAsync(fork);
            return (fork, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Could not fork the session: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the session's transcript to a file the user picks. Returns false when
    /// the picker was dismissed.
    /// </summary>
    public static async Task<bool> ExportAsync(AppServices services, string sessionId, System.Windows.Window? owner)
    {
        var session = await services.Sessions.LoadAsync(sessionId);
        if (session is null)
        {
            return false;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export transcript",
            FileName = $"{session.Title}.md",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return false;
        }

        await File.WriteAllTextAsync(dialog.FileName, JarvisCode.Core.Utilities.TranscriptExporter.Render(session));
        return true;
    }

    /// <summary>
    /// Opens the pull request for the session's branch in the browser via
    /// `gh pr view`. Returns an error line when there is none to open.
    /// </summary>
    public static async Task<string?> OpenPullRequestAsync(string workingDirectory)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return "The session's folder no longer exists.";
        }

        try
        {
            var psi = new ProcessStartInfo("gh", "pr view --json url -q .url")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return "Could not start gh.";
            }

            var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !output.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return "No pull request found for this session's branch.";
            }

            Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"Could not open the pull request: {ex.Message}";
        }
    }
}
