using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>The session-side hooks ccd_session needs from the window.</summary>
public interface ICcdSessionHost
{
    BackgroundTaskSuggestions Suggestions { get; }

    /// <summary>Records a chapter divider in the transcript and its table of contents.</summary>
    void MarkChapter(string title, string? summary);

    /// <summary>
    /// The content a rendered widget currently holds, keyed by the tool that
    /// rendered it. Empty when nothing has been rendered this session.
    /// </summary>
    IReadOnlyList<WidgetToolState> WidgetStates { get; }
}

/// <summary>One rendered widget's current content, as read_widget_context returns it.</summary>
public sealed record WidgetToolState(string ToolName, IReadOnlyList<ContentBlock> Content);

/// <summary>
/// The reference desktop's <c>ccd_session</c> in-process MCP server: spawn_task
/// flags an out-of-scope issue as a chip, dismiss_task withdraws it,
/// mark_chapter divides the transcript, and read_widget_context reads back a
/// rendered widget. Docs, schemas, caps, id format and every result sentence are
/// the reference's own (desktop 1.40609.0.0, <c>index2.chunk-CEBgETf7.js</c>).
/// </summary>
public static class CcdSessionTools
{
    /// <summary>The reference's prompt cap: <c>Tr = 32e3</c>.</summary>
    public const int MaxPromptChars = 32_000;

    public const int MaxTitleChars = 200;
    public const int MaxTldrChars = 2_000;

    public const string PromptRequired = "prompt is required and cannot be empty.";

    public const string TitleRequired = "title is required and cannot be empty.";

    public const string BadTaskId =
        "task_id must be the id returned by spawn_task (format: task_xxxxxxxx).";

    public static string PromptTooLong(int length) =>
        $"prompt is too long ({length} chars; max {MaxPromptChars}). Trim it to the essentials — the spawned " +
        "session can read files itself.";

    public static string CwdNotAbsolute(string cwd) =>
        $"cwd must be an absolute path (got \"{cwd}\"). Omit cwd to use the current project.";

    public static string CwdNotADirectory(string cwd) =>
        $"cwd \"{cwd}\" does not exist or is not a directory. Omit cwd to use the current project.";

    /// <summary>
    /// The reference's <c>Xr</c> refusals, in the order it checks them. A spawned
    /// session inherits this path, so a share that disappears or a path that
    /// still has to be resolved would fail later and somewhere else.
    /// </summary>
    public const string CwdIsUnc =
        "cwd must be a local path, not a UNC network path. Omit cwd to use the current project.";

    public const string CwdHasDotSegments =
        "cwd must not contain \".\" or \"..\" segments. Pass the plain absolute path, or omit cwd to use the " +
        "current project.";

    public const string CwdUnderAutomountRoot =
        "cwd must not be under an automount root. Omit cwd to use the current project.";

    /// <summary>
    /// The reference's own order: UNC, then dot segments, then an automount
    /// root, before the path is touched at all. Null when the path is fine.
    /// </summary>
    public static string? CwdRefusal(string cwd)
    {
        if (JarvisCode.Core.Utilities.NetworkPaths.IsNetworkPath(cwd))
        {
            // NetworkPaths answers both of the reference's kinds, and they take
            // different sentences: an automount root is a mount, a share is UNC.
            return cwd.TrimStart().StartsWith("/net/", StringComparison.Ordinal)
                ? CwdUnderAutomountRoot
                : CwdIsUnc;
        }

        var segments = cwd.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(static segment => segment is "." or "..") ? CwdHasDotSegments : null;
    }

    public static string ChapterMarked(string title) =>
        $"Chapter marked: \"{title}\". Continue your current work.";

    public static string Dismissed(string id) =>
        $"Task {id} withdrawn — the chip is no longer shown to the user. Continue your current work.";

    public static string AlreadyStarted(string id) =>
        $"Task {id} was already started by the user — it's no longer pending and can't be withdrawn. Nothing was " +
        "changed.";

    public static string AlreadyDismissed(string id) => $"Task {id} was already dismissed. Nothing was changed.";

    public static string SpawnInFlight(string id) =>
        $"Task {id} is being started right now — it can't be withdrawn while the spawn is in flight. Nothing was " +
        "changed.";

    public static string NotFound(string id) =>
        $"No pending task with id {id} — possible causes include it never being queued in this session, being " +
        "cleared along with the conversation, or its resolution record aging out; any copy kept for later is " +
        "being withdrawn too. Do not re-flag it.";

    public static string NoWidget(string toolName, IReadOnlyList<string> available) =>
        $"No widget context available for tool '{toolName}'." +
        (available.Count > 0 ? $" Available widgets: {string.Join(", ", available)}" : "");

    public static InternalMcpServerDefinition Server(ICcdSessionHost host) =>
        new(InternalMcpServerNames.CcdSession, Create(host))
        {
            IsEnabled = static context =>
                context.SessionType == InternalMcpSessionContext.CodeSessionType,

            // The reference's Zr() sets alwaysLoad on each of these four.
            AlwaysLoad = true,
        };

    public static IReadOnlyList<ITool> Create(ICcdSessionHost host) =>
    [
        McpToolBuilder.Tool(
            "spawn_task",
            "Flag an out-of-scope issue for a separate background task.\n\nCall this when you notice something " +
            "worth fixing that would bloat the current change — dead code, stale docs, missing coverage, a " +
            "confirmed TODO, or a security issue spotted in passing. Don't flag vague code-smell observations, " +
            "trivial fixes you can do inline, or low-confidence hunches. A chip appears for the user; one click " +
            "spins it off into its own session. Your current turn continues uninterrupted.\n\nThe prompt must " +
            "stand alone — include file paths and enough context to act without this conversation.\n\nThe result " +
            "includes a task_id; call dismiss_task with it if the suggestion later becomes stale.",
            McpToolBuilder.Schema(
                new JsonObject
                {
                    ["title"] = McpToolBuilder.Prop(
                        "string",
                        "Under 60 chars. Imperative action phrase (start with a verb), e.g. \"Fix stale README " +
                        "badge\", \"Remove dead config option\". Shown as the chip label and the spawned session " +
                        "title."),
                    ["prompt"] = McpToolBuilder.Prop(
                        "string",
                        "The initial message for the spawned session. Self-contained — include file paths and " +
                        "enough context to act without this conversation. Not shown directly in the UI."),
                    ["tldr"] = McpToolBuilder.Prop(
                        "string",
                        "One or two plain-English sentences shown on the suggestion card under the title. Lead " +
                        "with why you are suggesting this now — name what you noticed in this session — then say " +
                        "what the new session will do. Keep it readable: no file paths or code."),
                    ["cwd"] = McpToolBuilder.Prop(
                        "string",
                        "Optional. Absolute path to a different project root than the current session's — a path " +
                        "on the host this session runs on. The spawned session gets a fresh worktree under this " +
                        "path. Defaults to the current project — only set this when the work clearly belongs in " +
                        "another repo on that host."),
                },
                "title", "prompt", "tldr"),
            isReadOnly: false,
            (args, _) => Spawn(host, args),
            static args => $"spawn_task({JsonArgs.GetString(args, "title")})"),

        McpToolBuilder.Tool(
            "dismiss_task",
            "Withdraw a background-task chip you previously created with spawn_task.\n\nCall this when a " +
            "suggestion you flagged is now stale, superseded, or irrelevant — e.g. you (or the user) already " +
            "fixed it in this session, or you spawned a better-scoped replacement. To replace a chip: call " +
            "spawn_task with the new suggestion first, then dismiss the old task_id.\n\nOnly chips the user " +
            "hasn't acted on can be withdrawn from the queue. If the user already started or dismissed the task, " +
            "the result says so (a dismissed task's copy kept for later is withdrawn too) — do not retry.",
            McpToolBuilder.Schema(
                new JsonObject
                {
                    ["task_id"] = McpToolBuilder.Prop(
                        "string", "The task_id returned by the spawn_task call that created the chip."),
                    ["reason"] = McpToolBuilder.Prop(
                        "string",
                        "Optional one-line reason the suggestion is no longer needed, e.g. \"fixed in this " +
                        "session\" or \"superseded by task_ab12cd34\"."),
                },
                "task_id"),
            isReadOnly: false,
            (args, _) => DismissTask(host, args),
            static args => $"dismiss_task({JsonArgs.GetString(args, "task_id")})"),

        McpToolBuilder.Tool(
            "mark_chapter",
            "Mark the start of a new chapter in this session.\n\nCall this when the work shifts to a meaningfully " +
            "different phase — e.g. after finishing exploration and starting implementation, after a fix lands " +
            "and you move to verification, or when the user pivots to an unrelated request. The user sees a " +
            "divider in the transcript and a floating table of contents for jumping between chapters.\n\nUse " +
            "sparingly: a chapter should cover a coherent stretch of work, not every tool call. A typical session " +
            "has 3–8 chapters. Do not mark a chapter for the very first message — the session start is " +
            "implicit.\n\nThe title is a short noun phrase (\"Codebase exploration\", \"Auth bug fix\", \"Test " +
            "verification\"), not a sentence.",
            McpToolBuilder.Schema(
                new JsonObject
                {
                    ["title"] = McpToolBuilder.Prop(
                        "string",
                        "Short noun-phrase title for the chapter (under 40 chars). Shown in the table of " +
                        "contents."),
                    ["summary"] = McpToolBuilder.Prop(
                        "string",
                        "Optional one-line summary of what this chapter covers. Shown on hover in the table of " +
                        "contents."),
                },
                "title"),
            isReadOnly: false,
            (args, _) =>
            {
                var title = JsonArgs.GetString(args, "title")?.Trim() ?? "";
                if (title.Length == 0)
                {
                    return ToolResult.Error(TitleRequired);
                }

                host.MarkChapter(title, JsonArgs.GetString(args, "summary")?.Trim());
                return ToolResult.Success(ChapterMarked(title));
            },
            static args => $"mark_chapter({JsonArgs.GetString(args, "title")})"),

        McpToolBuilder.Tool(
            "read_widget_context",
            "Read context from an embedded interactive widget. Widgets are rendered alongside chat from prior " +
            "tool calls and can be interacted with by the user. Call this when you need to know the current state " +
            "of a widget.",
            McpToolBuilder.Schema(
                new JsonObject
                {
                    ["tool_name"] = McpToolBuilder.Prop(
                        "string", "The name of the widget tool to get context for"),
                },
                "tool_name"),
            isReadOnly: true,
            (args, _) =>
            {
                var wanted = JsonArgs.GetString(args, "tool_name") ?? "";
                var states = host.WidgetStates;
                var matching = states
                    .Where(s => string.Equals(s.ToolName, wanted, StringComparison.Ordinal))
                    .ToList();
                if (matching.Count == 0)
                {
                    return ToolResult.Error(
                        NoWidget(wanted, [.. states.Select(static s => s.ToolName).Distinct(StringComparer.Ordinal)]));
                }

                // The reference returns the widget's own text and image blocks
                // straight through, in the order it stored them.
                var blocks = matching.SelectMany(static s => s.Content).ToList();
                var text = string.Join("\n", blocks.OfType<TextBlock>().Select(static b => b.Text));
                var images = blocks.OfType<ImageBlock>().ToList();
                return images.Count > 0
                    ? new ToolResult(text, IsError: false, images)
                    : ToolResult.Success(text);
            },
            static args => $"read_widget_context({JsonArgs.GetString(args, "tool_name")})"),
    ];

    private static ToolResult Spawn(ICcdSessionHost host, JsonObject args)
    {
        var title = JsonArgs.GetString(args, "title") ?? "";
        var prompt = JsonArgs.GetString(args, "prompt")?.Trim() ?? "";
        var tldr = JsonArgs.GetString(args, "tldr") ?? "";
        var cwd = JsonArgs.GetString(args, "cwd")?.Trim() ?? "";

        if (prompt.Length == 0)
        {
            return ToolResult.Error(PromptRequired);
        }

        if (prompt.Length > MaxPromptChars)
        {
            return ToolResult.Error(PromptTooLong(prompt.Length));
        }

        string? resolvedCwd = null;
        if (cwd.Length > 0)
        {
            if (!Path.IsPathRooted(cwd))
            {
                return ToolResult.Error(CwdNotAbsolute(cwd));
            }

            if (CwdRefusal(cwd) is { } refusal)
            {
                return ToolResult.Error(refusal);
            }

            if (!Directory.Exists(cwd))
            {
                return ToolResult.Error(CwdNotADirectory(cwd));
            }

            resolvedCwd = Path.GetFullPath(cwd);
        }

        var id = BackgroundTaskSuggestions.NewId();
        var queued = host.Suggestions.Enqueue(new TaskSuggestion(
            id,
            Clip(title.Trim(), MaxTitleChars),
            prompt,
            Clip(tldr.Trim(), MaxTldrChars),
            resolvedCwd));

        var evicted = queued.Evicted.Count > 0
            ? $"The queue holds at most {BackgroundTaskSuggestions.Capacity} pending suggestions, so the oldest " +
              $"{(queued.Evicted.Count == 1 ? "one was" : $"{queued.Evicted.Count} were")} dropped to make room: " +
              $"{Join(queued.Evicted)}. "
            : "";

        return ToolResult.Success(
            $"Noted (position {queued.Position}, task_id: {id}). A chip is showing for the user — they can start " +
            "it in a fresh worktree with one click, or dismiss it. If this suggestion becomes stale or " +
            "superseded, call dismiss_task with this task_id. " + evicted +
            $"Currently pending: {Join(queued.Pending)}. Continue your current work.");
    }

    private static ToolResult DismissTask(ICcdSessionHost host, JsonObject args)
    {
        var id = JsonArgs.GetString(args, "task_id")?.Trim() ?? "";
        if (!BackgroundTaskSuggestions.IsWellFormedId(id))
        {
            return ToolResult.Error(BadTaskId);
        }

        return host.Suggestions.Dismiss(id) switch
        {
            TaskDismissOutcome.Dismissed => ToolResult.Success(Dismissed(id)),
            TaskDismissOutcome.AlreadyStarted => ToolResult.Success(AlreadyStarted(id)),
            TaskDismissOutcome.AlreadyDismissed => ToolResult.Success(AlreadyDismissed(id)),
            TaskDismissOutcome.SpawnInFlight => ToolResult.Success(SpawnInFlight(id)),
            _ => ToolResult.Success(NotFound(id)),
        };
    }

    /// <summary>The reference's listing shape: <c>id "title"</c>, comma separated.</summary>
    private static string Join(IReadOnlyList<TaskSuggestion> tasks) =>
        string.Join(", ", tasks.Select(static t => t.Label));

    private static string? Clip(string value, int max) =>
        value.Length == 0 ? null : value.Length <= max ? value : value[..max];
}
