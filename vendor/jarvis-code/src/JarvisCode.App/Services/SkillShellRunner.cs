using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// Runs a skill's !`cmd` preprocessing commands: each goes through the real
/// shell permission path (deny rules block, escalated/manual-mode commands
/// prompt like any shell call), then executes under the skill's declared shell
/// — bash (Git Bash) by default, PowerShell via "shell: powershell".
/// </summary>
public static class SkillShellRunner
{
    /// <summary>
    /// How long a finished command's output reads are given to hand over what has
    /// already arrived. Like the shell tool, a command is over when its process is:
    /// a descendant that inherited its pipes holds them open for as long as it runs.
    /// </summary>
    private static readonly TimeSpan DrainAfterExit = TimeSpan.FromSeconds(2);

    /// <summary>The permission subject: skill preprocessing commands are shell calls.</summary>
    private static readonly ShellToolStub Stub = new();

    public static SkillShellOutcome Run(
        string command, string shell, string workingDirectory, UiPermissionGate gate, TimeSpan timeout)
    {
        // The gate marshals its own prompt to the UI thread; callers must not
        // hold the UI thread while this blocks.
        var arguments = new JsonObject { ["command"] = command };
        var decision = gate.RequestAsync(
                new JarvisCode.Core.Agent.PermissionRequest(Stub, arguments, $"Run {command}", CallId: null),
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        if (decision == JarvisCode.Core.Agent.PermissionDecision.Deny)
            return new SkillShellOutcome(-1, "", "", PermissionError: "denied by permission rules or the user");

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (shell == "powershell")
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
            var bash = GitBash.Resolve();
            if (bash is null)
            {
                // Reference behavior: bash-shelled skills need Git Bash on Windows.
                return new SkillShellOutcome(-1, "",
                    "bash was not found on this machine. Install Git for Windows, or set \"shell: powershell\" " +
                    "in the skill's frontmatter.");
            }
            startInfo.FileName = bash;
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new SkillShellOutcome(-1, "", "the shell process failed to start");
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var reads = Task.WhenAll(
                PumpAsync(process.StandardOutput, stdout),
                PumpAsync(process.StandardError, stderr));
            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                return new SkillShellOutcome(-1, "", $"timed out after {timeout.TotalSeconds:0}s");
            }
            // The command is over once its process is; the reads get only a moment
            // to hand over what has arrived, because a descendant that inherited
            // the pipes keeps them open for as long as it runs.
            if (Task.WhenAny(reads, Task.Delay(DrainAfterExit)).GetAwaiter().GetResult() == reads)
            {
                reads.GetAwaiter().GetResult();
            }

            return new SkillShellOutcome(process.ExitCode, Drained(stdout), Drained(stderr));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new SkillShellOutcome(-1, "", ex.Message);
        }
    }

    /// <summary>Reads one stream into <paramref name="sink"/> until it ends.</summary>
    private static async Task PumpAsync(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer, CancellationToken.None);
                if (read == 0)
                {
                    return;
                }

                lock (sink)
                {
                    sink.Append(buffer, 0, read);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A killed process takes its pipes down under the read; whatever
            // arrived before that is already in the sink.
        }
    }

    /// <summary>Takes what a sink holds while its pump may still be writing to it.</summary>
    private static string Drained(StringBuilder sink)
    {
        lock (sink)
        {
            return sink.ToString();
        }
    }

    /// <summary>
    /// A minimal shell-shaped tool for the permission request: rules written
    /// against "PowerShell" apply, and the gate treats the command as mutating.
    /// </summary>
    private sealed class ShellToolStub : ITool
    {
        public string Name => "PowerShell";
        public string Description => "Skill preprocessing shell command";
        public JsonObject InputSchema => new() { ["type"] = "object" };
        public bool IsReadOnly => false;
        public string DescribeCall(JsonObject arguments) =>
            $"shell({JsonArgs.GetString(arguments, "command") ?? ""})";
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The stub only carries permission requests.");
    }
}

/// <summary>
/// Maps a skill's allowed-tools / disallowed-tools entries — written in the
/// reference's Tool(pattern) syntax with Claude Code tool names — onto the
/// app's "allow|deny &lt;tool&gt; [glob]" rule lines, interpolating
/// ${CLAUDE_SKILL_DIR} / ${CLAUDE_PROJECT_DIR}.
/// </summary>
public static class SkillPermissionRules
{
    private static readonly Dictionary<string, string> ToolNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bash"] = "Bash",
        ["PowerShell"] = "PowerShell",
        ["Shell"] = "PowerShell",
        ["Read"] = "Read",
        ["Edit"] = "Edit",
        ["MultiEdit"] = "Edit",
        ["Write"] = "Write",
        ["Glob"] = "Glob",
        ["Grep"] = "Grep",
        ["WebFetch"] = "WebFetch",
        ["WebSearch"] = "WebSearch",
        ["Task"] = "Agent",
        ["Agent"] = "Agent",
        ["TodoWrite"] = "todo_write",
        ["NotebookEdit"] = "NotebookEdit",
        ["Skill"] = "Skill",
        ["SlashCommand"] = "Skill",
        ["TaskOutput"] = "TaskOutput",
        ["BashOutput"] = "TaskOutput",
        ["KillShell"] = "TaskStop",
        ["TaskStop"] = "TaskStop",
        ["Monitor"] = "Monitor",
    };

    public static IReadOnlyList<string> ToRuleLines(
        IReadOnlyList<string> entries, string action, SkillDefinition skill, string projectDirectory)
    {
        var lines = new List<string>();
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry.Length == 0)
                continue;
            entry = entry
                .Replace("${CLAUDE_SKILL_DIR}", skill.Directory.Replace('\\', '/'))
                .Replace("${CLAUDE_PROJECT_DIR}", projectDirectory.Replace('\\', '/'));

            string tool = entry;
            string? pattern = null;
            int open = entry.IndexOf('(');
            if (open > 0 && entry.EndsWith(')'))
            {
                tool = entry[..open];
                pattern = entry[(open + 1)..^1].Trim();
                // The reference's "prefix:*" command patterns are prefix globs.
                if (pattern.EndsWith(":*", StringComparison.Ordinal))
                    pattern = pattern[..^2] + "*";
                if (pattern.Length == 0)
                    pattern = null;
            }
            if (ToolNameMap.TryGetValue(tool, out var mapped))
                tool = mapped;
            if (tool.Length == 0 || tool.Contains(' '))
                continue;
            lines.Add(pattern is null ? $"{action} {tool}" : $"{action} {tool} {pattern}");
        }
        return lines;
    }
}
