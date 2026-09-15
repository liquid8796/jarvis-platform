using System.Globalization;
using System.IO;
using System.Text;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Services;

/// <summary>
/// One scheduled task, stored the reference's way: <c>{taskId}/SKILL.md</c> with
/// the schedule in frontmatter and the prompt as the body, so the prompt stays
/// readable and editable on disk.
/// </summary>
public sealed class ScheduledTask
{
    public required string TaskId { get; init; }

    public required string Description { get; set; }

    public required string Prompt { get; set; }

    /// <summary>A 5-field cron expression in local time, or null for one-shot / ad-hoc.</summary>
    public string? CronExpression { get; set; }

    /// <summary>A one-time fire moment, or null. Mutually exclusive with the cron expression.</summary>
    public DateTimeOffset? FireAt { get; set; }

    public bool Enabled { get; set; } = true;

    public bool NotifyOnCompletion { get; set; } = true;

    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>The path of the SKILL.md the task is stored in.</summary>
    public string Path { get; set; } = "";

    /// <summary>Removed from the scheduler; the file stays so the prompt survives.</summary>
    public bool Deleted { get; set; }

    /// <summary>Ad-hoc: no schedule at all, so it can only be started by hand.</summary>
    public bool IsAdHoc => CronExpression is null && FireAt is null;

    public DateTimeOffset? NextRunAt(DateTimeOffset now)
    {
        if (!Enabled)
        {
            return null;
        }

        if (FireAt is { } once)
        {
            return once > now ? once : null;
        }

        if (CronSchedule.TryParse(CronExpression) is not { } cron)
        {
            return null;
        }

        if (cron.NextAfter(now.LocalDateTime) is not { } next)
        {
            return null;
        }

        // Unspecified, so the constructor takes the offset we hand it rather than
        // re-deriving one and disagreeing inside a daylight-saving fold.
        return new DateTimeOffset(
            DateTime.SpecifyKind(next, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(next));
    }

    /// <summary>The human-readable schedule list_scheduled_tasks reports.</summary>
    public string Schedule => FireAt is { } once
        ? $"once at {once.LocalDateTime:yyyy-MM-dd HH:mm}"
        : CronExpression is { } cron
            ? $"cron {cron} (local time)"
            : "ad-hoc (manual runs only)";
}

/// <summary>
/// The store behind mcp__scheduled-tasks__*: one directory per task under
/// <c>scheduled-tasks/</c>, each holding a SKILL.md. Pure file I/O so the
/// frontmatter round-trip is unit-testable.
/// </summary>
public sealed class ScheduledTaskStore(string rootDirectory)
{
    public string RootDirectory => rootDirectory;

    /// <summary>
    /// The reference sanitises the id to a kebab-case directory name "as a safety
    /// net". Anything that is not a letter or digit becomes a separator rather
    /// than being dropped, so "weird//name" cannot silently glue into one word —
    /// and a path separator can never survive into the directory name.
    /// </summary>
    public static string SanitizeId(string taskId)
    {
        var text = new StringBuilder();
        foreach (var c in taskId.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                text.Append(c);
            }
            else if (text.Length > 0 && text[^1] != '-')
            {
                text.Append('-');
            }
        }

        return text.ToString().Trim('-');
    }

    public IReadOnlyList<ScheduledTask> Load()
    {
        List<ScheduledTask> tasks = [];
        if (!Directory.Exists(rootDirectory))
        {
            return tasks;
        }

        foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
        {
            var path = System.IO.Path.Combine(directory, "SKILL.md");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (Parse(System.IO.Path.GetFileName(directory), File.ReadAllText(path)) is { } task)
                {
                    task.Path = path;
                    tasks.Add(task);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A task we cannot read is left out rather than failing the listing.
            }
        }

        return [.. tasks.OrderBy(static t => t.TaskId, StringComparer.Ordinal)];
    }

    public ScheduledTask? Find(string taskId) =>
        Load().FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.Ordinal));

    public void Save(ScheduledTask task)
    {
        var directory = System.IO.Path.Combine(rootDirectory, task.TaskId);
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "SKILL.md");
        File.WriteAllText(path, Render(task));
        task.Path = path;
    }

    /// <summary>
    /// Removes the task from the scheduler. The reference leaves the SKILL.md on
    /// disk "so the prompt can be recovered", so only the schedule is cleared.
    /// </summary>
    public bool Delete(string taskId)
    {
        if (Find(taskId) is not { } task)
        {
            return false;
        }

        task.CronExpression = null;
        task.FireAt = null;
        task.Enabled = false;
        task.Deleted = true;
        Save(task);
        return true;
    }

    internal static string Render(ScheduledTask task)
    {
        var text = new StringBuilder();
        text.AppendLine("---");
        text.AppendLine($"name: {task.TaskId}");
        text.AppendLine($"description: {task.Description}");
        if (task.CronExpression is { } cron)
        {
            text.AppendLine($"cron: {cron}");
        }

        if (task.FireAt is { } fireAt)
        {
            text.AppendLine($"fire-at: {fireAt:O}");
        }

        text.AppendLine($"enabled: {(task.Enabled ? "true" : "false")}");
        text.AppendLine($"notify-on-completion: {(task.NotifyOnCompletion ? "true" : "false")}");
        if (task.LastRunAt is { } last)
        {
            text.AppendLine($"last-run-at: {last:O}");
        }

        if (task.Deleted)
        {
            text.AppendLine("deleted: true");
        }

        text.AppendLine("---");
        text.AppendLine();
        text.Append(task.Prompt);
        return text.ToString();
    }

    internal static ScheduledTask? Parse(string taskId, string markdown)
    {
        var (fields, body) = Frontmatter.Parse(markdown);
        if (fields.Count == 0)
        {
            return null;
        }

        // A deleted task keeps its file so the prompt can be recovered, but it is
        // gone from the scheduler and from every listing.
        if (Frontmatter.GetBool(fields, "deleted") == true)
        {
            return null;
        }

        return new ScheduledTask
        {
            TaskId = taskId,
            Description = Frontmatter.Get(fields, "description") ?? "",
            Prompt = body.TrimStart('\n', '\r'),
            CronExpression = Frontmatter.Get(fields, "cron"),
            FireAt = ParseTime(Frontmatter.Get(fields, "fire-at")),
            Enabled = Frontmatter.GetBool(fields, "enabled") != false,
            NotifyOnCompletion = Frontmatter.GetBool(fields, "notify-on-completion") != false,
            LastRunAt = ParseTime(Frontmatter.Get(fields, "last-run-at")),
        };
    }

    internal static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
