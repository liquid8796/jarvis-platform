using System.Text.Json.Nodes;
using JarvisCode.Core.Routines;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference CronCreate/CronList/CronDelete, mapped onto the app's Scheduled
/// routines: the model can create, list and delete routines when the user asks
/// for recurring work. The minute scheduler (RoutineRunner) picks changes up on
/// its next tick; routines also show on the Scheduled page.
/// </summary>
public static class CronTools
{
    public static bool SameWorkingDirectory(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(first)).Equals(
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool VisibleTo(Routine routine, ToolExecutionContext context) =>
        (routine.OwnerSessionId is not null && routine.OwnerSessionId == context.SessionId) || (routine.OwnerSessionId is null &&
            (routine.CronSessionId == context.SessionId || SameWorkingDirectory(routine.WorkingDirectory, context.WorkingDirectory)));
    public static IReadOnlyList<ITool> Create(RoutineStore store) =>
        [new CronCreateTool(store), new CronListTool(store), new CronDeleteTool(store)];

    internal static string Describe(Routine routine)
    {
        var time = TimeSpan.FromMinutes(routine.TimeOfDayMinutes);
        var schedule = routine.CronExpression is { Length: > 0 } cron
            ? (routine.OneShot ? $"once at the next match of \"{cron}\"" : $"cron \"{cron}\"") +
              (routine.OwnerSessionId is null ? " · durable" : " · this session only")
            : routine.Schedule switch
            {
                RoutineSchedule.Hourly => "hourly (top of the hour)",
                RoutineSchedule.Daily => $"daily at {time:hh\\:mm}",
                RoutineSchedule.Weekdays => $"weekdays at {time:hh\\:mm}",
                RoutineSchedule.Weekly => $"every {routine.Day} at {time:hh\\:mm}",
                _ => "manual (Run now only)",
            };
        return $"{routine.Id[..8]} · {routine.Name} · {schedule}{(routine.Enabled ? "" : " · disabled")}" +
               (routine.LastRunAt is { } last ? $" · last ran {last:yyyy-MM-dd HH:mm}" : "");
    }

    internal static RoutineSchedule? ParseSchedule(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "hourly" => RoutineSchedule.Hourly,
        "daily" => RoutineSchedule.Daily,
        "weekdays" => RoutineSchedule.Weekdays,
        "weekly" => RoutineSchedule.Weekly,
        "manual" => RoutineSchedule.Manual,
        _ => null,
    };

    private sealed class CronCreateTool(RoutineStore store) : ITool
    {
        /// <summary>The reference's auto-expiry for a recurring job.</summary>
        private static readonly TimeSpan RecurringLifetime = TimeSpan.FromDays(7);

        public string Name => "CronCreate";

        // The description is the reference's (CapturedToolDocs); this is the fallback
        // for a host that wraps nothing.
        public string Description =>
            "Schedules a prompt on a 5-field cron expression (local time). recurring (default true) fires on every " +
            "match until deleted or auto-expired after 7 days; recurring: false fires once at the next match. " +
            "durable: true survives restarts; the default lives only as long as this session.";

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["cron"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] =
                        "Standard 5-field cron expression in local time: \"M H DoM Mon DoW\" (e.g. \"*/5 * * * *\" = " +
                        "every 5 minutes, \"30 14 28 2 *\" = Feb 28 at 2:30pm local once).",
                },
                ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "The prompt to enqueue at each fire time." },
                ["recurring"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] =
                        "true (default) = fire on every cron match until deleted or auto-expired after 7 days. false = " +
                        "fire once at the next match, then auto-delete.",
                },
                ["durable"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] =
                        "true = persist and survive restarts. false (default) = lives only as long as this session.",
                },
            },
            ["required"] = new JsonArray("cron", "prompt"),
        };

        public bool IsReadOnly => false;

        public string DescribeCall(JsonObject arguments) =>
            $"CronCreate({JsonArgs.GetString(arguments, "cron") ?? "?"})";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var cron = JsonArgs.GetString(arguments, "cron")?.Trim();
            var prompt = JsonArgs.GetString(arguments, "prompt")?.Trim();
            if (string.IsNullOrWhiteSpace(cron) || string.IsNullOrWhiteSpace(prompt))
                return Task.FromResult(ToolResult.Error("cron and prompt are required."));
            if (CronSchedule.TryParse(cron) is not { } schedule)
                return Task.FromResult(ToolResult.Error(
                    $"\"{cron}\" is not a 5-field cron expression (\"M H DoM Mon DoW\"; lists, ranges and steps allowed)."));

            var recurring = arguments["recurring"] is null || JsonArgs.GetBool(arguments, "recurring");
            var durable = JsonArgs.GetBool(arguments, "durable");
            if (string.IsNullOrWhiteSpace(context.SessionId))
                return Task.FromResult(ToolResult.Error("This host has no session identity for CronCreate."));
            var now = DateTime.Now;
            var next = schedule.NextAfter(now);
            if (next is null)
                return Task.FromResult(ToolResult.Error($"\"{cron}\" never matches a future minute."));

            var firstLine = prompt.Split('\n')[0].Trim();
            var routine = new Routine
            {
                Name = firstLine.Length > 48 ? firstLine[..48] + "…" : firstLine,
                Instruction = prompt,
                Schedule = RoutineSchedule.Manual,
                CronExpression = cron,
                OneShot = !recurring,
                OwnerSessionId = durable ? null : context.SessionId ?? "this-session",
                CronSessionId = context.SessionId,
                ExpiresAt = recurring ? now + RecurringLifetime : null,
                WorkingDirectory = context.WorkingDirectory,
            };
            store.Update(routines => routines.Add(routine));
            next = SessionCronTiming.NextDispatch(routine, now);
            var lifetime = recurring
                ? (durable ? "It recurs until deleted, and expires " : "It recurs while this session's app is open, and expires ") +
                  $"{routine.ExpiresAt:yyyy-MM-dd HH:mm}."
                : "It fires once and is then deleted.";
            return Task.FromResult(ToolResult.Success(
                $"Scheduled {routine.Id[..8]}: {Describe(routine)}. Next run {next:yyyy-MM-dd HH:mm} local. {lifetime} " +
                "The user sees it on the Scheduled page; CronDelete removes it."));
        }
    }

    private sealed class CronListTool(RoutineStore store) : ITool
    {
        public string Name => "CronList";

        public string Description => "Lists the scheduled routines (name, schedule, last run).";

        public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) => "CronList()";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var routines = store.Load().Where(r => VisibleTo(r, context)).ToList();
            return Task.FromResult(ToolResult.Success(routines.Count == 0
                ? "No routines are scheduled."
                : string.Join('\n', routines.Select(Describe))));
        }
    }

    private sealed class CronDeleteTool(RoutineStore store) : ITool
    {
        public string Name => "CronDelete";

        public string Description =>
            "Deletes one scheduled routine by the id prefix or exact name CronList shows. Only delete what the " +
            "user asked to remove.";

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Routine id prefix or exact name" },
            },
            ["required"] = new JsonArray("id"),
        };

        public bool IsReadOnly => false;

        public string DescribeCall(JsonObject arguments) => $"CronDelete({JsonArgs.GetString(arguments, "id") ?? "?"})";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var key = JsonArgs.GetString(arguments, "id")?.Trim();
            if (string.IsNullOrWhiteSpace(key))
                return Task.FromResult(ToolResult.Error("id is required."));

            var routines = store.Load().ToList();
            var matches = routines.Where(r =>
                VisibleTo(r, context) &&
                (r.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Equals(key, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count == 0)
                return Task.FromResult(ToolResult.Error($"No routine matches '{key}'."));
            if (matches.Count > 1)
                return Task.FromResult(ToolResult.Error(
                    $"'{key}' matches {matches.Count} routines — use a longer id prefix:\n" +
                    string.Join('\n', matches.Select(Describe))));

            store.Update(rows => rows.RemoveAll(r => r.Id == matches[0].Id));
            return Task.FromResult(ToolResult.Success($"Deleted routine '{matches[0].Name}'."));
        }
    }
}
