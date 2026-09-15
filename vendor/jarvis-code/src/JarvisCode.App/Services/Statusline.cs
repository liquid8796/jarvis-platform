using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference's statusLine, adapted to the desktop: a user-supplied shell
/// command receives session context as JSON on stdin and its first stdout line
/// renders under the composer. Refreshes are debounced by the caller.
/// </summary>
public static class Statusline
{
    public const int MaxLineChars = 220;
    private const int TimeoutMs = 4_000;

    /// <summary>The stdin payload, shaped like the reference's status line input.</summary>
    public static string BuildContextJson(
        string? modelId, string? modelDisplayName, string workingDirectory,
        string sessionId, string permissionMode, string effort) => new JsonObject
    {
        ["model"] = new JsonObject { ["id"] = modelId, ["display_name"] = modelDisplayName },
        ["workspace"] = new JsonObject { ["current_dir"] = workingDirectory },
        ["session_id"] = sessionId,
        ["permission_mode"] = permissionMode,
        ["effort"] = effort,
    }.ToJsonString();

    /// <summary>Runs the command; null when it fails, times out, or prints nothing.</summary>
    public static string? Run(string command, string contextJson)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            if (OperatingSystem.IsWindows())
            {
                startInfo.FileName = "powershell.exe";
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-ExecutionPolicy");
                startInfo.ArgumentList.Add("Bypass");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(command);
            }
            else
            {
                startInfo.FileName = "/bin/bash";
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(command);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
                return null;
            try
            {
                process.StandardInput.Write(contextJson);
            }
            catch (IOException)
            {
            }
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeoutMs))
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

            var line = output.Split('\n')
                .Select(static l => l.TrimEnd('\r').Trim())
                .FirstOrDefault(static l => l.Length > 0);
            if (line is null)
                return null;
            return line.Length <= MaxLineChars ? line : line[..MaxLineChars] + "…";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
