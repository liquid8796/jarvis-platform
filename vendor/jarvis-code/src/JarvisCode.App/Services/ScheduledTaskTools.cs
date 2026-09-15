using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference desktop's <c>scheduled-tasks</c> in-process MCP server: create,
/// list, update and delete the tasks that run on their own. Docs, schemas,
/// scheduling rules and storage shape are the reference's own (desktop
/// 1.40609.0.0, <c>index2.chunk-B3ZLT91c.js</c>).
///
/// This is a different surface from the CLI's CronCreate/CronList/CronDelete,
/// which this app also carries: the CLI's write the Scheduled routines the
/// Routines page shows, while these are the desktop's SKILL.md-backed tasks with
/// real cron expressions and one-shot fireAt moments.
/// </summary>
public static class ScheduledTaskTools
{
    public const string NoTasks = "No scheduled tasks found. Use create_scheduled_task to create one.";

    public const string BothSchedules =
        "Provide either cronExpression (recurring) or fireAt (one-time), not both.";

    /// <summary>
    /// The reference appends this to every create result: approvals a run
    /// collects are kept on the task, which is worth saying before the first
    /// unattended run stalls on a permission prompt nobody is there to answer.
    /// </summary>
    public const string ToolApprovals =
        "Tool approvals granted during a run are stored on the task and auto-applied to future runs. If this " +
        "task is likely to use remote connectors or browser control, recommend the user click \"Run now\" " +
        "first to pre-approve the tools it needs — this prevents future runs from pausing on permission " +
        "prompts.";

    /// <summary>The tail of both scheduling tool docs, describing the jitter.</summary>
    public const string TimingNote =
        "**Note on timing:** Recurring tasks apply a small deterministic delay of several minutes at " +
        "dispatch time to balance server load. One-time tasks fire without delay.";

    public static string FireAtMustBeFuture(string value, DateTimeOffset parsed) =>
        $"fireAt must be in the future. Got \"{value}\" which is {parsed.LocalDateTime:G}.";

    public static string BadCron(string expression) =>
        $"Invalid cron expression: \"{expression}\". Please provide a valid 5-field cron expression " +
        "(minute hour dayOfMonth month dayOfWeek).";

    public static string BadFireAt(string value) =>
        $"Invalid fireAt timestamp: \"{value}\". Provide an ISO 8601 string like " +
        "\"2026-03-05T14:30:00-08:00\".";

    public static string NotFound(string taskId) =>
        $"Scheduled task \"{taskId}\" not found. Use list_scheduled_tasks to see available tasks.";

    public static string AlreadyExists(string taskId) =>
        $"A scheduled task with ID \"{taskId}\" already exists. Use update_scheduled_task to modify it, or " +
        "choose a different taskId.";

    public static string NothingToUpdate(string taskId) =>
        $"No updates provided for task \"{taskId}\". Supply at least one of: title, prompt, description, " +
        "cronExpression, fireAt, enabled, notifyOnCompletion.";

    public static string AlreadyFired(string taskId, DateTimeOffset lastRun) =>
        $"Task \"{taskId}\" is a one-time task that already fired at {lastRun:O}. Provide a new fireAt " +
        "timestamp to re-arm it, or a cronExpression to make it recurring.";

    public static InternalMcpServerDefinition Server(ScheduledTaskStore store, Func<DateTimeOffset> now) =>
        new(InternalMcpServerNames.ScheduledTasks, Create(store, now))
        {
            IsEnabled = static context =>
                context.SessionType == InternalMcpSessionContext.CodeSessionType,
        };

    public static IReadOnlyList<ITool> Create(ScheduledTaskStore store, Func<DateTimeOffset> now) =>
    [
        McpToolBuilder.Tool(
            "create_scheduled_task",
            "Create a scheduled task that runs automatically — on a recurring schedule or once at a future " +
            "moment. Use this when the user asks for something to happen repeatedly (\"every day at 6am\", " +
            "\"each Monday\", \"hourly\") or at a specific later time (\"remind me in 20 minutes\", \"tomorrow at " +
            "3pm\"), rather than once right now. Go ahead and call it when the request clearly describes a " +
            "schedule; if the schedule or task content is ambiguous, confirm the details with the user first — an " +
            "approval prompt may or may not appear depending on the user's permission settings, so don't rely on " +
            "it as the confirmation step.\n\nTo modify an existing scheduled task's schedule or prompt, use " +
            "`update_scheduled_task` instead.\n\nThe task is stored as {taskId}/SKILL.md in " +
            $"{store.RootDirectory}/. Each run starts fresh with no memory of this conversation, so the prompt " +
            "must be fully self-contained: include which connectors to use, the output format, and any " +
            "preferences the user expressed here.\n\nScheduled tasks run while this app is open. If the app is " +
            "closed when a task is due, it runs on next launch — tell the user this so they aren't " +
            "surprised.\n\n**Scheduling options (pick at most one):**\n- cronExpression: recurring (daily, " +
            "weekly, etc.)\n- fireAt: one-time — runs once at the given moment, then auto-disables. Never use a " +
            "cron expression for a one-time task; cron has no one-shot semantics.\n- Omit both: \"ad-hoc\" — can " +
            "only be started manually\n\n**Recurring (cronExpression):** Cron is evaluated in the user's LOCAL " +
            "timezone, not UTC. Use local times directly. Format: minute hour dayOfMonth month dayOfWeek\n- " +
            "\"0 9 * * *\" — Every day at 9:00 AM local time\n- \"0 9 * * 1-5\" — Weekdays at 9:00 AM local " +
            "time\n- \"30 8 * * 1\" — Every Monday at 8:30 AM local time\n- \"0 0 1 * *\" — First day of every " +
            "month at midnight local time\n\n**One-time (fireAt):** An ISO 8601 timestamp with timezone offset. " +
            "The task fires once at that moment (or on next app launch if it was closed), then disables itself.\n" +
            "- \"2026-03-05T14:30:00-08:00\" — Runs once on March 5 at 2:30 PM Pacific\n- Use this for reminders " +
            "(\"remind me in 5 minutes\"), one-off future actions (\"tomorrow at 3pm\"), or specific dates\n\n" +
            TimingNote,
            McpToolBuilder.Schema(
                new JsonObject
                {
                    ["taskId"] = McpToolBuilder.Prop(
                        "string",
                        "Kebab-case identifier for the task (e.g., 'check-inbox', 'daily-standup'). Used as the " +
                        "directory name and storage key. Auto-sanitized as a safety net."),
                    ["title"] = McpToolBuilder.Prop(
                        "string",
                        "Display name shown in the task list, in the user's own words and language (e.g. " +
                        "'【毎朝】freee経理チェック'). Unlike taskId it may contain any characters. " +
                        "Omit to show the taskId in sentence case."),
                    ["prompt"] = McpToolBuilder.Prop(
                        "string",
                        "The full task prompt/instructions that will be executed each time the task runs. Write " +
                        "this as a complete prompt describing what Claude should do."),
                    ["description"] = McpToolBuilder.Prop(
                        "string", "A short one-line description of what this task does (used in skill frontmatter)."),
                    ["cronExpression"] = McpToolBuilder.Prop(
                        "string",
                        "Standard 5-field cron expression for recurring runs, in LOCAL time (not UTC). For " +
                        "example, '0 9 * * *' means 9am daily in the user's local timezone. Mutually exclusive " +
                        "with fireAt."),
                    ["fireAt"] = McpToolBuilder.Prop(
                        "string",
                        "ISO 8601 timestamp with timezone offset for a one-time run (e.g. " +
                        "'2026-03-05T14:30:00-08:00'). Mutually exclusive with cronExpression. Must be in the " +
                        "future. Task auto-disables after firing."),
                    ["notifyOnCompletion"] = McpToolBuilder.Prop(
                        "boolean",
                        "When true (default), this session receives a notification each time the task finishes a " +
                        "run. Pass false to opt out."),
                },
                "taskId", "prompt", "description"),
            isReadOnly: false,
            (args, _) => CreateTask(store, now, args),
            static args => $"create_scheduled_task({JsonArgs.GetString(args, "taskId")})"),

        McpToolBuilder.Tool(
            "list_scheduled_tasks",
            "List all scheduled tasks with their current state. Use this to discover existing tasks and their " +
            "IDs before updating them.\n\nReturns each task's taskId, title (when one is set), description, " +
            "schedule (human-readable), " +
            "cronExpression, fireAt (ISO timestamp if one-time), enabled state, nextRunAt (ISO timestamp), and " +
            "lastRunAt (ISO timestamp). Each entry also includes a `path` to the task's SKILL.md — Read it to see " +
            "the current prompt.",
            McpToolBuilder.Schema(),
            isReadOnly: true,
            (_, _) =>
            {
                var tasks = store.Load();
                if (tasks.Count == 0)
                {
                    return ToolResult.Success(NoTasks);
                }

                var moment = now();
                var rows = new JsonArray([.. tasks.Select(t =>
                {
                    // nextRunAt is when the task actually dispatches, which is
                    // its scheduled moment plus the deterministic jitter.
                    var jitter = ScheduledTaskJitter.SecondsFor(t.TaskId, t.CronExpression);
                    var next = t.NextRunAt(moment)?.AddSeconds(jitter);
                    return (JsonNode)new JsonObject
                    {
                        ["taskId"] = t.TaskId,
                        ["description"] = t.Description,
                        ["schedule"] = t.Schedule,
                        ["cronExpression"] = t.CronExpression,
                        ["fireAt"] = t.FireAt?.ToString("O"),
                        ["enabled"] = t.Enabled,
                        ["nextRunAt"] = next?.ToString("O"),
                        ["lastRunAt"] = t.LastRunAt?.ToString("O"),
                        ["jitterSeconds"] = jitter,
                        ["path"] = t.Path,
                    };
                })]);
                return ToolResult.Success(
                    rows.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            },
            static _ => "list_scheduled_tasks()"),

        McpToolBuilder.Tool(
            "update_scheduled_task",
            "Update an existing scheduled task. taskId must be an exact ID from list_scheduled_tasks. To see the " +
            "current prompt before editing it, Read the `path` returned by list_scheduled_tasks.\n\nSupports " +
            "partial updates — only supply the fields you want to change:\n- title: Rename the task as shown " +
            "in the task list (any characters; empty string reverts to the taskId in sentence case)\n" +
            "- prompt: Replace the instructions " +
            "Claude executes on each run\n- description: Replace the one-line summary shown in the sidebar\n- " +
            "cronExpression: Change or set a recurring schedule (5-field cron string in LOCAL time, not UTC). " +
            "Clears any one-time fireAt.\n- fireAt: Change or set a one-time run (ISO 8601 timestamp with offset, " +
            "must be in the future). Clears any cron schedule and re-arms the task.\n- enabled: Pass false to " +
            "pause automatic runs, true to resume them\n- notifyOnCompletion: Pass true to receive a notification " +
            "each time the task finishes a run; pass false to stop\n\n" +
            TimingNote,
            McpToolBuilder.Schema(
                new JsonObject
                {
                    ["taskId"] = McpToolBuilder.Prop(
                        "string", "The exact ID of the task to update (from list_scheduled_tasks)."),
                    ["title"] = McpToolBuilder.Prop(
                        "string",
                        "New display name for the task list (any characters). Empty string reverts to the " +
                        "taskId in sentence case."),
                    ["prompt"] = McpToolBuilder.Prop(
                        "string", "New prompt/instructions to replace the current ones."),
                    ["description"] = McpToolBuilder.Prop("string", "New one-line description for the task."),
                    ["cronExpression"] = McpToolBuilder.Prop(
                        "string",
                        "New 5-field cron expression for recurring runs in LOCAL time (not UTC). For example, " +
                        "'0 9 * * *' means 9am in the user's local timezone. Mutually exclusive with fireAt."),
                    ["fireAt"] = McpToolBuilder.Prop(
                        "string",
                        "New ISO 8601 timestamp with timezone offset for a one-time run. Mutually exclusive with " +
                        "cronExpression. Must be in the future. Re-arms and auto-enables the task."),
                    ["enabled"] = McpToolBuilder.Prop(
                        "boolean",
                        "Set to false to pause automatic runs, true to resume. Does not affect manual runs."),
                    ["notifyOnCompletion"] = McpToolBuilder.Prop(
                        "boolean",
                        "Pass true to have this session notified each time the task finishes a run (replaces any " +
                        "prior subscriber). Pass false to clear the subscription."),
                },
                "taskId"),
            isReadOnly: false,
            (args, _) => UpdateTask(store, now, args),
            static args => $"update_scheduled_task({JsonArgs.GetString(args, "taskId")})"),

        McpToolBuilder.Tool(
            "delete_scheduled_task",
            "Delete an existing scheduled task. taskId must be an exact ID from list_scheduled_tasks.\n\nThis " +
            "removes the task from the scheduler so it will no longer run. The task's SKILL.md file is left on " +
            "disk so the prompt can be recovered. To pause a task without deleting it, use update_scheduled_task " +
            "with enabled: false instead.",
            McpToolBuilder.Schema(
                new JsonObject
                {
                    ["taskId"] = McpToolBuilder.Prop(
                        "string", "The exact ID of the task to delete (from list_scheduled_tasks)."),
                },
                "taskId"),
            isReadOnly: false,
            (args, _) =>
            {
                var taskId = JsonArgs.GetString(args, "taskId")?.Trim() ?? "";
                if (store.Find(taskId) is not { } doomed)
                {
                    return ToolResult.Error(NotFound(taskId));
                }

                return store.Delete(taskId)
                    ? ToolResult.Success(
                        $"Scheduled task \"{taskId}\" deleted. Its SKILL.md file was left in place at " +
                        $"{doomed.Path} in case you need to recover the prompt.")
                    : ToolResult.Error($"Failed to delete task \"{taskId}\": task not found during delete.");
            },
            static args => $"delete_scheduled_task({JsonArgs.GetString(args, "taskId")})"),
    ];

    private static ToolResult CreateTask(ScheduledTaskStore store, Func<DateTimeOffset> now, JsonObject args)
    {
        var taskId = ScheduledTaskStore.SanitizeId(JsonArgs.GetString(args, "taskId") ?? "");
        var prompt = JsonArgs.GetString(args, "prompt")?.Trim() ?? "";
        var description = JsonArgs.GetString(args, "description")?.Trim() ?? "";
        if (taskId.Length == 0 || prompt.Length == 0 || description.Length == 0)
        {
            return ToolResult.Error("taskId, prompt and description are required.");
        }

        if (ReadSchedule(args, now(), out var cron, out var fireAt) is { } refusal)
        {
            return ToolResult.Error(refusal);
        }

        // The reference refuses rather than overwriting: an id that already
        // names a task is a different task, and create is not update.
        if (store.Find(taskId) is not null)
        {
            return ToolResult.Error(AlreadyExists(taskId));
        }

        var task = new ScheduledTask
        {
            TaskId = taskId,
            Description = description,
            Prompt = prompt,
            CronExpression = cron,
            FireAt = fireAt,
            // "When true (default)" — an absent key must not read as false.
            NotifyOnCompletion = OptionalBool(args, "notifyOnCompletion") != false,
        };
        store.Save(task);

        return ToolResult.Success(Created(task, now()));
    }

    private static ToolResult UpdateTask(ScheduledTaskStore store, Func<DateTimeOffset> now, JsonObject args)
    {
        var taskId = JsonArgs.GetString(args, "taskId")?.Trim() ?? "";
        if (store.Find(taskId) is not { } task)
        {
            return ToolResult.Error(NotFound(taskId));
        }

        if (ReadSchedule(args, now(), out var cron, out var fireAt) is { } refusal)
        {
            return ToolResult.Error(refusal);
        }

        var promptText = JsonArgs.GetString(args, "prompt");
        var descriptionText = JsonArgs.GetString(args, "description");
        var enabledFlag = OptionalBool(args, "enabled");
        var notifyFlag = OptionalBool(args, "notifyOnCompletion");
        var promptChanged = promptText is { } p && p.Trim().Length > 0;
        var descriptionChanged = descriptionText is { } d && d.Trim().Length > 0;

        // "Supports partial updates" — but an update that changes nothing is a
        // mistake worth naming rather than a no-op worth reporting as success.
        if (!promptChanged && !descriptionChanged &&
            cron is null && fireAt is null && enabledFlag is null && notifyFlag is null)
        {
            return ToolResult.Error(NothingToUpdate(taskId));
        }

        // Re-enabling a spent one-time task without re-arming it would report
        // success for a task that can never run again.
        if (enabledFlag == true && cron is null && fireAt is null &&
            task.FireAt is not null && task.LastRunAt is { } lastRun)
        {
            return ToolResult.Error(AlreadyFired(taskId, lastRun));
        }

        if (promptChanged)
        {
            task.Prompt = promptText!.Trim();
        }

        if (descriptionChanged)
        {
            task.Description = descriptionText!.Trim();
        }

        // Either schedule replaces the other, and setting fireAt re-arms the task.
        if (cron is not null)
        {
            task.CronExpression = cron;
            task.FireAt = null;
        }

        if (fireAt is not null)
        {
            task.FireAt = fireAt;
            task.CronExpression = null;
            task.Enabled = true;
        }

        // Partial update: a flag the caller did not send keeps its stored value.
        if (enabledFlag is { } enabled)
        {
            task.Enabled = enabled;
        }

        if (notifyFlag is { } notify)
        {
            task.NotifyOnCompletion = notify;
        }

        store.Save(task);
        return ToolResult.Success(Updated(
            task, now(), promptChanged, descriptionChanged, cron, fireAt, enabledFlag, notifyFlag));
    }

    /// <summary>
    /// A boolean that distinguishes "absent" from "false".
    /// <see cref="JsonArgs.GetBool"/> reads a missing key as false, which is what
    /// a required flag wants and what a partial update must never do.
    /// </summary>
    internal static bool? OptionalBool(JsonObject args, string name) =>
        args[name] is JsonValue value
            ? value.TryGetValue<bool>(out var flag)
                ? flag
                : value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed)
                    ? parsed
                    : null
            : null;

    /// <summary>
    /// Reads the two mutually exclusive schedule fields. Returns the refusal to
    /// send, or null when the pair is acceptable.
    /// </summary>
    private static string? ReadSchedule(
        JsonObject args, DateTimeOffset now, out string? cron, out DateTimeOffset? fireAt)
    {
        cron = null;
        fireAt = null;

        var cronText = JsonArgs.GetString(args, "cronExpression")?.Trim();
        var fireAtText = JsonArgs.GetString(args, "fireAt")?.Trim();
        var hasCron = !string.IsNullOrEmpty(cronText);
        var hasFireAt = !string.IsNullOrEmpty(fireAtText);

        if (hasCron && hasFireAt)
        {
            return BothSchedules;
        }

        if (hasCron)
        {
            if (CronSchedule.TryParse(cronText) is null)
            {
                return BadCron(cronText!);
            }

            cron = cronText;
        }

        if (hasFireAt)
        {
            if (ScheduledTaskStore.ParseTime(fireAtText) is not { } parsed)
            {
                return BadFireAt(fireAtText!);
            }

            if (parsed <= now)
            {
                return FireAtMustBeFuture(fireAtText!, parsed);
            }

            fireAt = parsed;
        }

        return null;
    }

    /// <summary>
    /// The reference's three create bodies, picked by which schedule the task
    /// got, each closing with the tool-approvals paragraph.
    /// </summary>
    internal static string Created(ScheduledTask task, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.Append($"Scheduled task \"{task.TaskId}\" created.\n\n**Task file:** {task.Path}\n");

        if (task.FireAt is { } once)
        {
            text.Append($"**Will run once at:** {once.LocalDateTime:G} ({Relative(once, now)})\n\n");
            text.Append("The task will auto-disable after running. You can manage it from the " +
                        "\"Scheduled\" section in the sidebar.");
        }
        else if (task.CronExpression is not null)
        {
            // The moment it actually dispatches, jitter included — the same
            // number list_scheduled_tasks reports as nextRunAt.
            var jitter = ScheduledTaskJitter.SecondsFor(task.TaskId, task.CronExpression);
            text.Append($"**Schedule:** {task.Schedule}\n");
            text.Append($"**Next run:** {Relative(task.NextRunAt(now)?.AddSeconds(jitter), now)}\n\n");
            text.Append("The task will run automatically according to the schedule. You can manage it from " +
                        "the \"Scheduled\" section in the sidebar.");
        }
        else
        {
            text.Append("**Schedule:** Manual only (no automatic schedule)\n\n");
            text.Append("This task will not run automatically. You can start it manually from the " +
                        "\"Scheduled\" section in the sidebar.");
        }

        return text.Append("\n\n").Append(ToolApprovals).ToString();
    }

    /// <summary>
    /// The reference's update body: the fields that changed, listed in its
    /// order, with the tool-approvals paragraph appended only when the prompt
    /// itself was replaced.
    /// </summary>
    internal static string Updated(
        ScheduledTask task,
        DateTimeOffset now,
        bool promptChanged,
        bool descriptionChanged,
        string? cron,
        DateTimeOffset? fireAt,
        bool? enabled,
        bool? notify)
    {
        List<string> changed = [];
        if (promptChanged)
        {
            changed.Add("prompt");
        }

        if (descriptionChanged)
        {
            changed.Add("description");
        }

        if (cron is not null)
        {
            changed.Add($"schedule ({task.Schedule})");
        }

        if (fireAt is { } once)
        {
            changed.Add($"one-time run at {once.LocalDateTime:G} ({Relative(once, now)})");
        }

        if (enabled is { } on)
        {
            changed.Add(on ? "enabled" : "disabled");
        }

        if (notify is { } notifying)
        {
            changed.Add(notifying ? "completion notifications enabled" : "completion notifications disabled");
        }

        var tail = promptChanged ? $"\n\n{ToolApprovals}" : "";
        return $"Scheduled task \"{task.TaskId}\" updated: {string.Join(", ", changed)}.{tail}";
    }

    /// <summary>A moment as "in 4 minutes" / "in 2 hours", the reference's relative stamp.</summary>
    private static string Relative(DateTimeOffset? moment, DateTimeOffset now)
    {
        if (moment is not { } at)
        {
            return "not scheduled";
        }

        var span = at - now;
        if (span <= TimeSpan.Zero)
        {
            return "now";
        }

        if (span < TimeSpan.FromMinutes(1))
        {
            return "in less than a minute";
        }

        if (span < TimeSpan.FromHours(1))
        {
            var minutes = (int)Math.Round(span.TotalMinutes);
            return $"in {minutes} minute{(minutes == 1 ? "" : "s")}";
        }

        if (span < TimeSpan.FromDays(1))
        {
            var hours = (int)Math.Round(span.TotalHours);
            return $"in {hours} hour{(hours == 1 ? "" : "s")}";
        }

        var days = (int)Math.Round(span.TotalDays);
        return $"in {days} day{(days == 1 ? "" : "s")}";
    }
}
