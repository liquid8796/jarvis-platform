using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Hooks;
using JarvisCode.Core.Models;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Services;

/// <summary>
/// The text bodies behind the informational slash commands — /status, /doctor,
/// /insights, /skill-doctor, /hooks. Pure builders where possible so they unit
/// test without a window; /doctor's environment probes are the async exception.
/// </summary>
public static class SessionInfo
{
    // ---- /status ----

    public sealed record StatusData(
        string Version,
        string? ModelName,
        string? ProviderName,
        string Effort,
        string PermissionMode,
        string WorkingDirectory,
        string SessionId,
        long ContextTokens,
        int ContextCap,
        int ToolCount,
        IReadOnlyDictionary<string, int> McpServers);

    public static string BuildStatus(StatusData data)
    {
        var text = new StringBuilder();
        text.AppendLine($"Jarvis Code {data.Version}");
        text.AppendLine($"Model: {data.ModelName ?? "(none configured)"}" +
            (data.ProviderName is null ? "" : $" · {data.ProviderName}"));
        text.AppendLine($"Effort: {data.Effort} · Permission mode: {data.PermissionMode}");
        text.AppendLine($"Working directory: {data.WorkingDirectory}");
        text.AppendLine($"Session: {data.SessionId}");
        if (data.ContextCap > 0)
        {
            text.AppendLine($"Context: {data.ContextTokens:N0} / {data.ContextCap:N0} tokens" +
                (data.ContextTokens > 0 ? $" ({data.ContextTokens * 100 / data.ContextCap}%)" : ""));
        }

        text.AppendLine($"Host + MCP tools: {data.ToolCount}");
        text.Append(data.McpServers.Count == 0
            ? "MCP servers: none connected"
            : "MCP servers: " + string.Join(", ",
                data.McpServers.OrderBy(s => s.Key).Select(s => $"{s.Key} ({s.Value} tools)")));
        return text.ToString().TrimEnd();
    }

    // ---- /doctor ----

    public static async Task<string> RunDoctorAsync(
        string workingDirectory,
        string settingsRoot,
        string? searxngBaseUrl,
        bool hasApiKeyForDefaultModel,
        string? defaultModelLabel,
        int mcpConfigured,
        int mcpConnected,
        HttpClient http,
        int maxContextTokens)
    {
        var lines = new List<string>
        {
            Check("git", ProbeCommand("git", "--version")),
            Check("gh (GitHub CLI)", ProbeCommand("gh", "--version")),
            Check("Settings directory writable", ProbeWritable(settingsRoot)),
            Check("Browser engine", ProbeEngine()),
            Check(defaultModelLabel is null ? "Default model" : $"Credentials for {defaultModelLabel}",
                defaultModelLabel is null
                    ? "missing — pick a default model in Settings"
                    : hasApiKeyForDefaultModel ? null : "no API key stored (or the provider needs none)"),
            Check("MCP servers", mcpConfigured == 0
                ? null
                : mcpConnected >= mcpConfigured
                    ? null
                    : $"{mcpConnected}/{mcpConfigured} configured servers connected"),
            Check("Web search endpoint", await ProbeUrlAsync(http, searxngBaseUrl)),
            Check("Project instructions", LargeInstructionFiles(workingDirectory, maxContextTokens)),
        };
        return "Doctor:\n" + string.Join('\n', lines);
    }

    /// <summary>
    /// The reference's large-memory-file warning. Instruction files are sent whole,
    /// so the one that costs real context is named here instead of being cut.
    /// </summary>
    private static string? LargeInstructionFiles(string workingDirectory, int maxContextTokens)
    {
        int threshold = JarvisCode.Core.Agent.ProjectInstructions.LargeFileThreshold(maxContextTokens);
        var oversized = JarvisCode.Core.Agent.ProjectInstructions.LoadAll(workingDirectory).Files
            .Where(file => file.Content.Length > threshold)
            .Select(file =>
                $"Large {Path.GetFileName(file.FilePath)} will impact performance " +
                $"({file.Content.Length:N0} chars > {threshold:N0})")
            .ToList();
        return oversized.Count == 0 ? null : string.Join("; ", oversized);
    }

    /// <summary>
    /// One row of the reference's /memory picker: what it is called, what it is,
    /// and the file choosing it opens.
    /// </summary>
    public sealed record MemoryFileRow(string Label, string Description, string FilePath, bool Exists);

    /// <summary>
    /// The reference's /memory listing: every instruction file in force, and the
    /// two the user is most likely to want next even when they do not exist yet.
    /// </summary>
    public static IReadOnlyList<MemoryFileRow> BuildMemoryFiles(string workingDirectory, string userDirectory)
    {
        var scope = JarvisCode.Core.Agent.ProjectInstructions.ScopeFor(workingDirectory);
        var loaded = JarvisCode.Core.Agent.ProjectInstructions.LoadAll(scope).Files;

        var primary = JarvisCode.Core.Agent.ProjectInstructions.FileNames[0];
        var userFile = Path.Combine(userDirectory, primary);
        var projectFile = Path.Combine(workingDirectory, primary);
        bool hasUser = loaded.Any(f => PathEquals(f.FilePath, userFile));
        bool hasProject = loaded.Any(f => PathEquals(f.FilePath, projectFile));
        bool checkedIn = Directory.Exists(Path.Combine(workingDirectory, ".git")) ||
                         File.Exists(Path.Combine(workingDirectory, ".git"));
        var savedAt = checkedIn ? "Checked in at" : "Saved in";

        var rows = new List<MemoryFileRow>();
        foreach (var file in loaded)
        {
            if (file.FilePath == JarvisCode.Core.Agent.ProjectInstructions.ManagedSettingsPath)
            {
                continue;
            }

            bool top = PathEquals(file.FilePath, userFile) || PathEquals(file.FilePath, projectFile);
            var label =
                file.Type == JarvisCode.Core.Agent.InstructionMemoryType.User && top ? "User instructions"
                : file.Type == JarvisCode.Core.Agent.InstructionMemoryType.Project && top ? "Project instructions"
                : file.ParentFilePath is not null ? $"  L {Path.GetFileName(file.FilePath)}"
                : Path.GetFileName(file.FilePath);
            var description =
                file.Type == JarvisCode.Core.Agent.InstructionMemoryType.User && top ? $"Saved in {userFile}"
                : file.Type == JarvisCode.Core.Agent.InstructionMemoryType.Project && top ? $"{savedAt} ./{primary}"
                : file.ParentFilePath is not null ? "@-imported"
                : file.Globs is { Count: > 0 } ? "dynamically loaded"
                : file.FilePath;
            rows.Add(new MemoryFileRow(label, description, file.FilePath, Exists: true));
        }

        if (!hasUser)
        {
            rows.Add(new MemoryFileRow("User instructions (new)", $"Saved in {userFile}", userFile, Exists: false));
        }

        if (!hasProject)
        {
            rows.Add(new MemoryFileRow(
                "Project instructions (new)", $"{savedAt} ./{primary}", projectFile, Exists: false));
        }

        return rows;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static string Check(string name, string? problem) =>
        problem is null ? $"  ✓ {name}" : $"  ✗ {name} — {problem}";

    private static string? ProbeCommand(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null || !process.WaitForExit(4000))
                return "did not respond";
            return process.ExitCode == 0 ? null : $"exited with code {process.ExitCode}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "not found on PATH";
        }
    }

    private static string? ProbeWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".doctor-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The engine is fetched on first use rather than installed with the app, so
    /// "not there yet" is a normal state and says how it is fixed.
    /// </summary>
    private static string? ProbeEngine() =>
        new ElectronRuntime().IsInstalled
            ? null
            : "not downloaded yet (it is fetched the first time a browser pane, artifact or diagram is shown)";

    private static async Task<string?> ProbeUrlAsync(HttpClient http, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "not configured";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var response = await http.GetAsync(baseUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return null; // Any HTTP answer proves the endpoint is reachable.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return $"unreachable ({baseUrl})";
        }
    }

    // ---- /insights ----

    public static string BuildInsights(UsageStatsData data, IReadOnlyList<SessionSummary> sessions)
    {
        var days = data.Days;
        long totalTokens = days.Values.Sum(d => d.Tokens);
        int totalMessages = days.Values.Sum(d => d.Messages);
        var text = new StringBuilder("Session insights:\n");
        text.AppendLine($"  Sessions on disk: {sessions.Count}");
        text.AppendLine($"  Active days recorded: {days.Count} · messages: {totalMessages:N0} · tokens: {FormatTokens(totalTokens)}");

        if (days.Count > 0)
        {
            var busiest = days.MaxBy(d => d.Value.Tokens);
            text.AppendLine($"  Busiest day: {busiest.Key} ({FormatTokens(busiest.Value.Tokens)})");
            var hours = new Dictionary<int, int>();
            foreach (var day in days.Values)
            foreach (var (hour, count) in day.Hours)
                hours[hour] = hours.GetValueOrDefault(hour) + count;
            if (hours.Count > 0)
            {
                int top = hours.MaxBy(h => h.Value).Key;
                text.AppendLine($"  Busiest hour of day: {top:00}:00–{top + 1:00}:00");
            }

            var byModel = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var day in days.Values)
            foreach (var (model, tokens) in day.TokensByModel)
                byModel[model] = byModel.GetValueOrDefault(model) + tokens;
            if (byModel.Count > 0)
            {
                text.AppendLine("  Top models: " + string.Join(", ", byModel
                    .OrderByDescending(m => m.Value)
                    .Take(3)
                    .Select(m => $"{m.Key} ({FormatTokens(m.Value)})")));
            }
        }

        if (sessions.Count > 0)
        {
            var recent = sessions.Count(s => s.UpdatedAt > DateTimeOffset.Now.AddDays(-7));
            text.AppendLine($"  Active in the last 7 days: {recent} session(s)");
        }

        return text.ToString().TrimEnd();
    }

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M tokens",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k tokens",
        _ => $"{tokens} tokens",
    };

    // ---- /usage ----

    /// <summary>/usage: this session's context plus recent local activity totals.
    /// The reference's plan-limits half is account-bound and has no local analog.</summary>
    public static string BuildUsage(UsageStatsData data, long sessionContextTokens, int contextCap)
    {
        var text = new StringBuilder("Usage:\n");
        text.AppendLine(contextCap > 0
            ? $"  Session context: {FormatTokens(sessionContextTokens)} of {FormatTokens(contextCap)} " +
              $"({sessionContextTokens * 100.0 / contextCap:0}%)"
            : $"  Session context: {FormatTokens(sessionContextTokens)}");

        var today = DateOnly.FromDateTime(DateTime.Now);
        (long Tokens, int Messages) Window(int days)
        {
            long tokens = 0;
            int messages = 0;
            foreach (var (key, day) in data.Days)
            {
                if (DateOnly.TryParseExact(key, "yyyy-MM-dd", out var date) && date > today.AddDays(-days))
                {
                    tokens += day.Tokens;
                    messages += day.Messages;
                }
            }

            return (tokens, messages);
        }

        var (dayTokens, dayMessages) = Window(1);
        var (weekTokens, weekMessages) = Window(7);
        var (monthTokens, monthMessages) = Window(30);
        long allTokens = data.Days.Values.Sum(d => d.Tokens);
        int allMessages = data.Days.Values.Sum(d => d.Messages);
        text.AppendLine($"  Today: {FormatTokens(dayTokens)} · {dayMessages:N0} message(s)");
        text.AppendLine($"  Last 7 days: {FormatTokens(weekTokens)} · {weekMessages:N0} message(s)");
        text.AppendLine($"  Last 30 days: {FormatTokens(monthTokens)} · {monthMessages:N0} message(s)");
        text.AppendLine($"  All time: {FormatTokens(allTokens)} · {allMessages:N0} message(s) across {data.Days.Count} active day(s)");
        text.Append("  /insights has the full local report.");
        return text.ToString();
    }

    // ---- /skill-doctor ----

    /// <summary>
    /// The reference /skill-doctor report: a table of skill · source · context
    /// (listing-entry tokens) · 7d tokens (attributed across the last week's
    /// sessions on this machine) · uses · last used, with the reference legend
    /// and per-source advice lines.
    /// </summary>
    public static string BuildSkillDoctor(
        IReadOnlyList<SkillDefinition> enabled,
        IReadOnlyList<SkillDefinition> listed,
        UiSettings uiSettings,
        string sessionsDirectory)
    {
        if (enabled.Count == 0)
            return "No skills are loaded.";

        var listedNames = new HashSet<string>(listed.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var weekTokens = ScanWeekTokens(enabled, sessionsDirectory);

        var rows = new List<string[]> { new[] { "skill", "source", "context", "7d tokens", "uses", "last used" } };
        foreach (var skill in enabled.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var usage = uiSettings.SkillUsage.TryGetValue(skill.Name, out var entry) ? entry : null;
            var context = listedNames.Contains(skill.Name)
                ? $"~{Math.Max(1, SkillInvocation.ListingEntry(skill).Length / 4)}"
                : "—";
            var lastUsed = usage is null ? "never"
                : (DateTimeOffset.Now - usage.LastUsedAt).TotalDays < 1 ? "today"
                : $"{(int)(DateTimeOffset.Now - usage.LastUsedAt).TotalDays}d ago";
            rows.Add(new[]
            {
                skill.Name, skill.Source, context,
                weekTokens.TryGetValue(skill.Name, out var tokens) ? $"~{tokens}" : "—",
                (usage?.UsageCount ?? 0).ToString(), usage is null ? "never" : lastUsed,
            });
        }

        var widths = new int[rows[0].Length];
        foreach (var row in rows)
        {
            for (int i = 0; i < row.Length; i++)
                widths[i] = Math.Max(widths[i], row[i].Length);
        }

        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append("  ");
            for (int i = 0; i < row.Length; i++)
                text.Append(row[i].PadRight(widths[i] + (i == row.Length - 1 ? 0 : 2)));
            text.AppendLine();
        }

        text.AppendLine();
        text.AppendLine("  context = this skill's one-line listing in the system prompt, included every turn");
        text.AppendLine("  (dash = not in the current listing, costs nothing; full SKILL.md loads only when it runs)");
        text.AppendLine("  7d tokens = tokens attributed to the skill over the last 7 days of sessions on this machine");
        text.AppendLine();

        bool NeverUsed(SkillDefinition s) =>
            !uiSettings.SkillUsage.TryGetValue(s.Name, out var e) || e.UsageCount == 0;
        var unusedLocal = enabled.Where(s => !s.Source.StartsWith("plugin", StringComparison.OrdinalIgnoreCase) && NeverUsed(s)).ToList();
        var unusedPlugin = enabled.Where(s => s.Source.StartsWith("plugin", StringComparison.OrdinalIgnoreCase) && NeverUsed(s)).ToList();
        if (unusedLocal.Count == 0 && unusedPlugin.Count == 0)
        {
            text.Append("  All loaded skills have been used at least once.");
        }
        else
        {
            if (unusedLocal.Count > 0)
            {
                text.AppendLine(
                    $"  {unusedLocal.Count} skill(s) loaded but never invoked. Each one adds to the system " +
                    "prompt every turn. Disable in /skills, or remove from .claude/skills.");
            }
            if (unusedPlugin.Count > 0)
            {
                var sources = string.Join(", ", unusedPlugin.Select(s => s.Source).Distinct(StringComparer.OrdinalIgnoreCase));
                text.AppendLine(
                    $"  {unusedPlugin.Count} plugin skill(s) loaded but never invoked, from {sources}. " +
                    "Disable those plugins in Customize › Personal plugins.");
            }
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Tokens attributed per skill across sessions touched in the last 7 days:
    /// each invocation found in a session file attributes the skill's current
    /// body estimate (chars/4). Bounded work — recent files only, capped.
    /// </summary>
    private static Dictionary<string, long> ScanWeekTokens(
        IReadOnlyList<SkillDefinition> skills, string sessionsDirectory)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!System.IO.Directory.Exists(sessionsDirectory))
                return result;
            var cutoff = DateTime.Now.AddDays(-7);
            var files = System.IO.Directory
                .EnumerateFiles(sessionsDirectory, "*.json", System.IO.SearchOption.AllDirectories)
                .Select(f => new System.IO.FileInfo(f))
                .Where(f => f.LastWriteTime >= cutoff && f.Length < 5_000_000)
                .OrderByDescending(f => f.LastWriteTime)
                .Take(100);
            foreach (var file in files)
            {
                string content;
                try
                {
                    content = System.IO.File.ReadAllText(file.FullName);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (var skill in skills)
                {
                    int invocations = CountOccurrences(content, $"Launching skill: {skill.Name}") +
                                      CountOccurrences(content, $"<command-name>/{skill.Name}</command-name>");
                    if (invocations > 0)
                    {
                        result[skill.Name] = result.GetValueOrDefault(skill.Name) +
                            invocations * Math.Max(1L, skill.Body.Length / 4);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
        }
        return result;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ---- /hooks ----

    public static string BuildHooksSummary(HookRunner hooks, string projectHooksPath, string userHooksPath)
    {
        var text = new StringBuilder();
        if (hooks.Hooks.Count == 0)
        {
            text.AppendLine("No hooks are configured.");
        }
        else
        {
            text.AppendLine($"{hooks.Hooks.Count} hook(s) loaded:");
            foreach (var hook in hooks.Hooks)
            {
                var match = string.IsNullOrWhiteSpace(hook.ToolMatch) ? "" : $" [{hook.ToolMatch}]";
                // A hook is a shell command, a condition a model judges, a URL or
                // an MCP tool; the line says which, so "→ audit the diff" is not
                // mistaken for something that runs in a shell.
                var (kind, subject) = hook.Kind switch
                {
                    HookKind.Prompt => ("prompt", hook.Prompt ?? hook.Command),
                    HookKind.Http => ("http", hook.Http?.Url ?? hook.Command),
                    HookKind.McpTool => ("mcp_tool", $"{hook.Mcp?.Server}/{hook.Mcp?.Tool}"),
                    _ => ("command", hook.Command),
                };
                var shown = subject.Length > 70 ? subject[..70] + "…" : subject;
                text.AppendLine(
                    $"  {EventName(hook.Event)}{match} → {kind}: {shown} ({hook.TimeoutSeconds}s)");
            }
        }

        text.AppendLine($"Project config: {projectHooksPath}{(File.Exists(projectHooksPath) ? "" : " (not present)")}");
        text.Append($"User config: {userHooksPath}{(File.Exists(userHooksPath) ? "" : " (not present)")}");
        return text.ToString();
    }

    private static string EventName(HookEvent hookEvent) => hookEvent switch
    {
        HookEvent.PreToolUse => "pre_tool_use",
        HookEvent.PostToolUse => "post_tool_use",
        HookEvent.TurnCompleted => "turn_completed",
        HookEvent.UserPromptSubmit => "user_prompt_submit",
        HookEvent.SessionStart => "session_start",
        HookEvent.SessionEnd => "session_end",
        HookEvent.Stop => "stop",
        HookEvent.SubagentStop => "subagent_stop",
        HookEvent.SubagentStart => "subagent_start",
        HookEvent.Notification => "notification",
        HookEvent.PreCompact => "pre_compact",
        HookEvent.PermissionRequest => "permission_request",
        HookEvent.PostToolUseFailure => "post_tool_use_failure",
        HookEvent.PostToolBatch => "post_tool_batch",
        HookEvent.UserPromptExpansion => "user_prompt_expansion",
        HookEvent.StopFailure => "stop_failure",
        HookEvent.PostCompact => "post_compact",
        HookEvent.PermissionDenied => "permission_denied",
        HookEvent.Setup => "setup",
        HookEvent.Elicitation => "elicitation",
        HookEvent.ElicitationResult => "elicitation_result",
        HookEvent.ConfigChange => "config_change",
        HookEvent.WorktreeCreate => "worktree_create",
        HookEvent.WorktreeRemove => "worktree_remove",
        HookEvent.InstructionsLoaded => "instructions_loaded",
        HookEvent.CwdChanged => "cwd_changed",
        HookEvent.FileChanged => "file_changed",
        HookEvent.DirectoryAdded => "directory_added",
        HookEvent.MessageDisplay => "message_display",
        HookEvent.TaskCreated => "task_created",
        HookEvent.TaskCompleted => "task_completed",
        HookEvent.TeammateIdle => "teammate_idle",
        HookEvent.PreModelSwitch => "pre_model_switch",
        HookEvent.PostModelSwitch => "post_model_switch",
        _ => hookEvent.ToString(),
    };
}
