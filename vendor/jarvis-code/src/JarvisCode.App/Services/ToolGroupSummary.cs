using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// One clause of a tool group's header sentence — "ran" + "2 commands". The
/// reference keeps the verb and the noun phrase apart because the failure
/// decoration goes on the noun ("2 commands (1 failed)"), and because a group
/// whose calls all touched one file collapses several verbs onto one noun
/// ("read and edited Base.xaml").
/// </summary>
/// <param name="IsError">
/// Every call of this clause failed. The reference then drops the "(n failed)"
/// parenthetical and colours the clause instead — saying "2 commands (2 failed)"
/// would be counting to itself.
/// </param>
public readonly record struct ToolSummarySegment(string Verb, string? Meta, bool IsError)
{
    public string Text => string.IsNullOrEmpty(Meta) ? Verb : $"{Verb} {Meta}";
}

/// <summary>What the group header needs to know about one call.</summary>
public interface IToolSummaryCall
{
    string ToolName { get; }
    string CallId { get; }
    JsonObject? Args { get; }
    bool IsRunning { get; }
    bool IsError { get; }
    bool IsInterrupted { get; }
    bool IsDenied { get; }

    /// <summary>What the call answered with; a spawn_task prints its task id there.</summary>
    string Result { get; }
}

/// <summary>
/// The sentence a settled tool group is titled with, ported from the reference
/// desktop's own aggregator (desktop 1.44121.2.0, ion-dist
/// <c>c78751380-0qjdp_tY.js</c>: <c>Ya</c> classifies a tool name, <c>hE</c>
/// words one kind, <c>fE</c> decorates it with its failures, <c>vE</c> puts the
/// clauses in order, and <c>Wo</c>/<c>_E</c> lift the memory operations out in
/// front).
///
/// Two things about it are easy to get wrong by writing something that merely
/// reads the same. A clause counts **distinct files**, not calls, so reading one
/// file twice is "read a file"; and a kind the reference does not classify —
/// a skill, a browser action, a screenshot — is not given a sentence of its own
/// but folded into "used {n} tools". The vocabulary here is also the Code
/// transcript's, which is not the one the chat surface uses: this says
/// "searched code" where that says "Searched 2 patterns".
/// </summary>
public static class ToolGroupSummary
{
    /// <summary>The reference's <c>qa</c>: a snake-cased tool name to its kind.</summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        ["read"] = "read",
        ["view"] = "view",
        ["write"] = "write",
        ["create_file"] = "write",
        ["edit"] = "edit",
        ["multi_edit"] = "edit",
        ["str_replace"] = "edit",
        ["str_replace_editor"] = "edit",
        ["update_file"] = "edit",
        ["open_file"] = "read",
        ["close_file"] = "read",
        ["delete_file"] = "delete_file",
        ["file_search"] = "glob",
        ["present_files"] = "read",
        ["notebook_edit"] = "notebook_edit",
        ["repl"] = "bash",
        ["glob"] = "glob",
        ["grep"] = "grep",
        ["recent_chats"] = "memory",
        ["conversation_search"] = "memory",
        ["project_knowledge_search"] = "memory",
        ["drive_search"] = "drive_search",
        ["web_fetch"] = "web",
        ["web_search"] = "web",
        ["web_search_fast"] = "web",
        ["bash"] = "bash",
        ["bash_tool"] = "bash",
        ["power_shell"] = "bash",
        ["task"] = "task",
        ["agent"] = "task",
        ["skill"] = "skill",
        ["preview_start"] = "preview",
        ["preview_stop"] = "preview",
        ["preview_list"] = "preview",
        ["preview_screenshot"] = "preview",
        ["preview_snapshot"] = "preview",
        ["preview_click"] = "preview",
        ["preview_type"] = "preview",
        ["preview_fill"] = "preview",
        ["preview_scroll"] = "preview",
        ["preview_select"] = "preview",
        ["preview_eval"] = "preview",
        ["preview_resize"] = "preview",
        ["preview_logs"] = "preview",
        ["preview_console_logs"] = "preview",
        ["preview_network"] = "preview",
        ["preview_inspect"] = "preview",
        ["todo_write"] = "todo",
        ["kill_bash"] = "kill_bash",
        ["tmux"] = "tmux",
        ["exit_plan_mode"] = "exit_plan_mode",
        ["tool_search"] = "tool_search",
    };

    /// <summary>
    /// The reference's <c>oE</c> — the kinds that get a clause of their own.
    /// Everything else is counted into "used {n} tools", which is why a skill or
    /// a browser action has no sentence here even though the row does. The two
    /// artifact kinds are the reference's and are carried for completeness:
    /// nothing here resolves to one, because this build's <c>artifact</c> tool
    /// renders into an engine tile and takes no <c>action</c> to classify.
    /// </summary>
    private static readonly HashSet<string> Classified = new(StringComparer.Ordinal)
    {
        "read", "view", "write", "edit", "notebook_edit", "delete_file",
        "bash", "grep", "glob", "web", "task", "todo", "exit_plan_mode",
        "artifact_read", "artifact_write",
    };

    /// <summary>The reference's <c>cE</c> — the kinds that name a file, and so can collapse onto one.</summary>
    private static readonly HashSet<string> FileKinds = new(StringComparer.Ordinal)
    {
        "read", "view", "write", "edit", "notebook_edit", "delete_file",
    };

    /// <summary>The reference's <c>Ja</c>: this one is a kind only under its own name.</summary>
    private static readonly HashSet<string> BareOnly = new(StringComparer.Ordinal) { "tool_search" };

    private static readonly Regex CamelBoundary = new("([a-z])([A-Z])", RegexOptions.Compiled);

    /// <summary>The reference's <c>Bo</c>: paths inside a shell command line.</summary>
    private static readonly Regex CommandPaths =
        new(@"(?:[A-Za-z]:[/\\]|/)[^\s'""]+", RegexOptions.Compiled);

    /// <summary>The reference's <c>za</c>: the tool's own name, snake-cased.</summary>
    private static string SnakeName(string toolName) =>
        CamelBoundary.Replace(StripServer(toolName), "$1_$2").ToLowerInvariant();

    private static string StripServer(string toolName) =>
        InternalMcpServers.TrySplit(toolName, out _, out var tool) ? tool : toolName;

    /// <summary>The reference's <c>Ya</c>: which kind a tool name counts as, if any.</summary>
    public static string? KindOf(string toolName)
    {
        var name = SnakeName(toolName);
        if (Kinds.TryGetValue(name, out var kind))
        {
            var prefixed = toolName.StartsWith("mcp__", StringComparison.Ordinal) ||
                !string.Equals(StripServer(toolName), toolName, StringComparison.Ordinal) ||
                toolName.Contains(':', StringComparison.Ordinal);
            return BareOnly.Contains(name) && prefixed ? null : kind;
        }

        if (IsBrowserTool(toolName))
        {
            return "browser";
        }

        return name.Contains("screenshot", StringComparison.Ordinal) && IsComputerTool(toolName)
            ? "screenshot"
            : null;
    }

    private static bool IsBrowserTool(string toolName)
    {
        if (toolName.StartsWith("browser_", StringComparison.Ordinal))
        {
            return true;
        }

        return InternalMcpServers.TrySplit(toolName, out var server, out _) &&
            server is InternalMcpServerNames.ClaudeBrowser or InternalMcpServerNames.ClaudeInChrome;
    }

    private static bool IsComputerTool(string toolName) =>
        toolName.Contains("computer-use", StringComparison.Ordinal) ||
        toolName.Contains("remote-devices__computer_", StringComparison.Ordinal);

    /// <summary>
    /// The header's clauses, in the order the reference puts them: the memory
    /// operations, then one per classified kind in first-appearance order, then
    /// the unclassified remainder.
    /// </summary>
    /// <param name="memoryDirectory">
    /// Where this session keeps its memory files, so a write into it reads as
    /// "saved a memory" rather than "created a file". Null leaves every call an
    /// ordinary one, which is what the reference does with no memory path.
    /// </param>
    public static IReadOnlyList<ToolSummarySegment> Build(
        IReadOnlyList<IToolSummaryCall> calls, string? memoryDirectory = null)
    {
        var memory = CountMemoryOperations(calls, memoryDirectory);
        var rest = memory is null
            ? calls
            : calls.Where(c => !memory.Value.ToolIds.Contains(c.CallId)).ToList();

        var segments = new List<ToolSummarySegment>();
        if (memory is not null)
        {
            segments.AddRange(MemorySegments(memory.Value.Counts));
        }

        if (rest.Count > 0 || memory is null)
        {
            segments.AddRange(Aggregate(rest));
        }

        return segments;
    }

    /// <summary>
    /// The one tool the reference files under its <c>spawnTask</c> bucket, by its
    /// wire name alone (its <c>X = b</c>). A run of these is never mixed with
    /// other calls and is summarised by its own builder rather than by the
    /// ordinary one.
    /// </summary>
    public const string SpawnTaskTool = "mcp__ccd_session__spawn_task";

    public static bool IsSpawnTask(string toolName) =>
        string.Equals(toolName, SpawnTaskTool, StringComparison.Ordinal);

    /// <summary>The reference's <c>O</c>: the task id a spawn_task result prints.</summary>
    private static readonly Regex TaskId =
        new(@"task_id: (task_[0-9a-f]{8})\b", RegexOptions.Compiled);

    /// <summary>
    /// The task a spawn_task call queued, read back out of its own result — the
    /// reference's <c>hQ</c> over its <c>ay</c>. A call that failed or has not
    /// finished queued nothing, so it names no task.
    /// </summary>
    public static string? TaskIdOf(IToolSummaryCall call)
    {
        if (call.IsRunning || call.IsError || call.IsInterrupted || call.IsDenied)
        {
            return null;
        }

        var match = TaskId.Match(call.Result);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The reference's <c>gE</c>: a run of spawn_task calls is counted by what
    /// became of the chips rather than by what the tool is. The ones the user
    /// started lead as "started {n} sessions" and are never decorated with a
    /// failure count — a chip that started a session did not fail — and the rest
    /// read "suggested {n} tasks", which is.
    /// </summary>
    /// <param name="startedTask">
    /// Whether the chip a task id names went on to start a session. The reference
    /// asks its session store which sessions record being spawned from this one;
    /// here the chip queue holds the same fact.
    /// </param>
    public static IReadOnlyList<ToolSummarySegment> BuildSpawnTask(
        IReadOnlyList<IToolSummaryCall> calls, Func<string, bool>? startedTask)
    {
        var started = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            if (TaskIdOf(call) is { } id && startedTask?.Invoke(id) == true)
            {
                started.Add(id);
            }
        }

        var suggested = calls.Count - started.Count;
        var failed = calls.Count(call => StatusOf(call) == CallStatus.Failed);
        var segments = new List<ToolSummarySegment>();
        if (started.Count > 0)
        {
            segments.Add(new ToolSummarySegment(
                "started", Plural(started.Count, "a session", "sessions"), false));
        }

        if (suggested > 0)
        {
            segments.Add(Decorate(
                new ToolSummarySegment("suggested", Plural(suggested, "a task", "tasks"), false),
                suggested,
                failed));
        }

        return segments;
    }

    /// <summary>
    /// Whether any of these calls touched the session's memory folder. The
    /// reference's bare-row test refuses a run that has memory operations
    /// (<c>uQ</c>'s <c>!e.memoryOps</c>): those clauses are the header's, and a
    /// row on its own has no header to carry them.
    /// </summary>
    public static bool AnyMemoryOperation(
        IReadOnlyList<IToolSummaryCall> calls, string? memoryDirectory) =>
        CountMemoryOperations(calls, memoryDirectory) is not null;

    /// <summary>The reference's <c>vE</c>.</summary>
    private static List<ToolSummarySegment> Aggregate(IReadOnlyList<IToolSummaryCall> calls)
    {
        var order = new List<string>();
        var byKind = new Dictionary<string, Dictionary<string, CallStatus?>>(StringComparer.Ordinal);
        var unclassified = 0;
        var unclassifiedFailed = 0;

        // The file half, kept beside the counts so the "one file, several verbs"
        // collapse can look at it without walking the calls again.
        var fileEntries = new List<(string Kind, string? Path, CallStatus? Status)>();
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anyPathlessFileCall = false;

        foreach (var call in calls)
        {
            var kind = KindOf(call.ToolName);
            if (kind is null || !Classified.Contains(kind))
            {
                unclassified++;
                if (StatusOf(call) == CallStatus.Failed)
                {
                    unclassifiedFailed++;
                }

                continue;
            }

            if (!byKind.TryGetValue(kind, out var seen))
            {
                seen = new Dictionary<string, CallStatus?>(StringComparer.OrdinalIgnoreCase);
                byKind[kind] = seen;
                order.Add(kind);
            }

            var path = FilePathOf(call);
            var key = path ?? call.CallId;
            var status = StatusOf(call);
            seen[key] = Merge(seen.TryGetValue(key, out var previous) ? previous : null, status);

            if (FileKinds.Contains(kind))
            {
                fileEntries.Add((kind, path, status));
                if (path is null)
                {
                    anyPathlessFileCall = true;
                }
                else
                {
                    filePaths.Add(path);
                }
            }
        }

        // Kinds that share a verb ("edit" and "notebook_edit" are both "edited")
        // agree about a file's outcome, so the collapsed clause cannot say a file
        // both failed and did not.
        var byVerb = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kind in order.Where(FileKinds.Contains))
        {
            var verb = Word(kind, 1).Verb;
            if (!byVerb.TryGetValue(verb, out var group))
            {
                byVerb[verb] = group = [];
            }

            group.Add(kind);
        }

        foreach (var group in byVerb.Values.Where(g => g.Count >= 2))
        {
            var unified = new Dictionary<string, CallStatus?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (kind, path, status) in fileEntries)
            {
                if (path is not null && group.Contains(kind))
                {
                    unified[path] = Merge(unified.TryGetValue(path, out var previous) ? previous : null, status);
                }
            }

            foreach (var seen in group.Select(kind => byKind[kind]))
            {
                foreach (var (path, status) in unified)
                {
                    if (seen.ContainsKey(path))
                    {
                        seen[path] = status;
                    }
                }
            }
        }

        var singleFile = !anyPathlessFileCall && filePaths.Count == 1
            ? ToolRowPresentation.FileMeta(filePaths.Single())
            : null;
        var fileKindsInOrder = singleFile is null ? [] : order.Where(FileKinds.Contains).ToList();
        ToolSummarySegment? collapsed = null;
        if (singleFile is not null && byVerb.Count > 0)
        {
            var failedVerbs = byVerb.Values.Count(group => group.Any(kind => FailureCount(byKind[kind]) > 0));
            collapsed = Decorate(
                new ToolSummarySegment(Conjoin(byVerb.Keys), singleFile, false), byVerb.Count, failedVerbs);
        }

        var segments = new List<ToolSummarySegment>();
        foreach (var kind in order)
        {
            if (collapsed is not null && FileKinds.Contains(kind))
            {
                if (kind == fileKindsInOrder[0])
                {
                    segments.Add(collapsed.Value);
                }

                continue;
            }

            var seen = byKind[kind];
            segments.Add(Decorate(Word(kind, seen.Count), seen.Count, FailureCount(seen)));
        }

        if (unclassified > 0)
        {
            segments.Add(Decorate(
                new ToolSummarySegment("used", Plural(unclassified, "a tool", "tools"), false),
                unclassified,
                unclassifiedFailed));
        }

        return segments;
    }

    /// <summary>The reference's <c>hE</c>: how one kind is worded at a given count.</summary>
    private static ToolSummarySegment Word(string kind, int n) => kind switch
    {
        "read" => new("read", Plural(n, "a file", "files"), false),
        "view" => new("viewed", Plural(n, "a file", "files"), false),
        "write" => new("created", Plural(n, "a file", "files"), false),
        "edit" => new("edited", Plural(n, "a file", "files"), false),
        "notebook_edit" => new("edited", Plural(n, "a notebook", "notebooks"), false),
        "delete_file" => new("deleted", Plural(n, "a file", "files"), false),
        "bash" => new("ran", Plural(n, "a command", "commands"), false),
        "grep" => new("searched", "code", false),
        "glob" => new("found", "files", false),
        "web" => new("browsed", "the web", false),
        "task" => new("ran", Plural(n, "an agent", "agents"), false),
        "todo" => new("updated", "todos", false),
        "exit_plan_mode" => new("proposed", "a plan", false),
        "artifact_read" => new("read", Plural(n, "an artifact", "artifacts"), false),
        "artifact_write" => new("updated", Plural(n, "an artifact", "artifacts"), false),
        _ => new("used", Plural(n, "a tool", "tools"), false),
    };

    /// <summary>
    /// The reference's <c>fE</c>. A clause every call of which failed is coloured
    /// rather than counted; a partial failure is counted and left in the ordinary
    /// colour.
    /// </summary>
    private static ToolSummarySegment Decorate(ToolSummarySegment segment, int n, int failed)
    {
        if (failed == 0)
        {
            return segment;
        }

        return failed >= n
            ? segment with { IsError = true }
            : segment with { Meta = $"{segment.Meta} ({failed} failed)" };
    }

    private static string Plural(int n, string one, string many) =>
        n == 1 ? one : $"{n} {many}";

    /// <summary>Intl.ListFormat's conjunction for en: "a", "a and b", "a, b, and c".</summary>
    private static string Conjoin(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => $"{string.Join(", ", list.Take(list.Count - 1))}, and {list[^1]}",
        };
    }

    // ---- one call's outcome (the reference's dE / pE / mE) ----

    private enum CallStatus
    {
        Failed,
        Succeeded,
    }

    /// <summary>
    /// The reference's <c>dE</c>. An interrupted or denied call reports nothing:
    /// it did not fail, it never ran, and counting it as a failure would put a
    /// "(1 failed)" on a group the user themselves stopped.
    /// </summary>
    private static CallStatus? StatusOf(IToolSummaryCall call)
    {
        if (call.IsInterrupted || call.IsDenied)
        {
            return null;
        }

        if (call.IsRunning)
        {
            return null;
        }

        return call.IsError ? CallStatus.Failed : CallStatus.Succeeded;
    }

    /// <summary>The reference's <c>pE</c>: the last settled word about a file wins.</summary>
    private static CallStatus? Merge(CallStatus? previous, CallStatus? next) => next ?? previous;

    private static int FailureCount(Dictionary<string, CallStatus?> seen) =>
        seen.Values.Count(status => status == CallStatus.Failed);

    // ---- files ----

    private static string? FilePathOf(IToolSummaryCall call)
    {
        var args = call.Args;
        if (args is null)
        {
            return null;
        }

        foreach (var key in new[] { "file_path", "notebook_path" })
        {
            if (args[key] is JsonValue value && value.TryGetValue<string>(out var path) &&
                !string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return null;
    }

    // ---- memory operations (the reference's Ho / Uo / Wo / _E) ----

    private static IEnumerable<ToolSummarySegment> MemorySegments(MemoryCounts counts)
    {
        if (counts.Read + counts.Search > 0)
        {
            yield return new ToolSummarySegment(
                "recalled", Plural(counts.Read + counts.Search, "a memory", "memories"), false);
        }

        if (counts.Write > 0)
        {
            yield return new ToolSummarySegment("saved", Plural(counts.Write, "a memory", "memories"), false);
        }
    }

    private readonly record struct MemoryCounts(int Read, int Write, int Search);

    private static (MemoryCounts Counts, HashSet<string> ToolIds)? CountMemoryOperations(
        IReadOnlyList<IToolSummaryCall> calls, string? memoryDirectory)
    {
        if (string.IsNullOrWhiteSpace(memoryDirectory))
        {
            return null;
        }

        var root = Normalize(memoryDirectory);
        if (!root.EndsWith('/'))
        {
            root += "/";
        }

        var read = 0;
        var write = 0;
        var search = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            var operation = MemoryOperation(KindOf(call.ToolName));
            if (operation is null || !TouchesMemory(call, root))
            {
                continue;
            }

            switch (operation)
            {
                case "read": read++; break;
                case "write": write++; break;
                default: search++; break;
            }

            ids.Add(call.CallId);
        }

        return read + write + search > 0 ? (new MemoryCounts(read, write, search), ids) : null;
    }

    /// <summary>The reference's <c>Ho</c>.</summary>
    private static string? MemoryOperation(string? kind) => kind switch
    {
        "read" or "view" => "read",
        "write" or "edit" or "notebook_edit" => "write",
        "grep" or "glob" or "bash" => "search",
        _ => null,
    };

    /// <summary>The reference's <c>Uo</c> over its <c>zo</c> path predicate.</summary>
    private static bool TouchesMemory(IToolSummaryCall call, string root)
    {
        var args = call.Args;
        if (args is null)
        {
            return false;
        }

        if (KindOf(call.ToolName) == "bash")
        {
            return args["command"] is JsonValue command && command.TryGetValue<string>(out var line) &&
                CommandPaths.Matches(line).Any(m => IsMemoryPath(m.Value.TrimEnd(',', ';', '|', '&', '>'), root));
        }

        foreach (var key in new[] { "file_path", "path", "notebook_path" })
        {
            if (args[key] is JsonValue value && value.TryGetValue<string>(out var path))
            {
                return IsMemoryPath(path, root);
            }
        }

        return false;
    }

    private static bool IsMemoryPath(string path, string root)
    {
        var normalized = Normalize(path);
        // The reference excludes only the instruction file by name; its own index
        // file counts, which is what makes "recalled a memory" cover a read of it.
        if (string.Equals(Path.GetFileName(normalized), "claude.md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(normalized), "jarvis.md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>The header capitalises only the first clause (the reference's <c>bE</c>).</summary>
    public static string Sentence(IReadOnlyList<ToolSummarySegment> segments)
    {
        if (segments.Count == 0)
        {
            return "";
        }

        var parts = segments.Select((segment, index) =>
            index == 0 ? Capitalize(segment.Text) : segment.Text);
        return string.Join(", ", parts);
    }

    public static string Capitalize(string text) => text.Length == 0
        ? text
        : char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
}
