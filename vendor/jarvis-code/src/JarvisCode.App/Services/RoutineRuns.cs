using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarvisCode.App.Services;

/// <summary>Why a due run did not happen — the reference's <c>zs</c> enum, plus its unstated third case.</summary>
public static class RoutineSkipReasons
{
    /// <summary>"The previous run was still in progress."</summary>
    public const string PerTaskLimit = "per_task_limit";

    /// <summary>"Other routines were already running."</summary>
    public const string GlobalLimit = "global_limit";

    /// <summary>
    /// No reason recorded, which the reference explains with its keep-awake
    /// hint: the machine was asleep or the app was closed.
    /// </summary>
    public const string Asleep = "";
}

/// <summary>
/// One line of a routine's History. The reference's detail page merges two
/// sources — the sessions a routine started and the slots it missed — so an
/// entry is either a run (with the session it opened) or a skip (with a reason).
/// </summary>
public sealed record RoutineRunEntry
{
    /// <summary>"run" or "missed".</summary>
    public string Kind { get; init; } = RunKind;

    public DateTimeOffset Time { get; init; }

    /// <summary>The session a run opened, so History can open it.</summary>
    public string? SessionId { get; init; }

    /// <summary>What the run reported, shown as "Run reported: {summary}".</summary>
    public string? Summary { get; init; }

    /// <summary>One of <see cref="RoutineSkipReasons"/>; only meaningful for a skip.</summary>
    public string? Reason { get; init; }

    /// <summary>The run ended in an error.</summary>
    public bool Failed { get; init; }

    public const string RunKind = "run";

    public const string MissedKind = "missed";

    [JsonIgnore]
    public bool IsMissed => Kind == MissedKind;
}

/// <summary>
/// The run history behind the Scheduled detail page's History section, and the
/// count the list and the sidebar show.
///
/// Shape: one JSON file per routine at
/// <c>{profile}/routine-runs/{sanitised id}.json</c> holding a
/// <c>RoutineRunEntry[]</c>, newest last. Writes are atomic (temp + move) and
/// the file is capped at <see cref="MaxEntries"/>, oldest dropped — the
/// reference pages its own history at 100 with "Show more" and never promises
/// to keep every run for ever.
///
/// It is a separate store from the session files on purpose: a run's session
/// can be archived or deleted, and the history line for it must survive that.
/// </summary>
public sealed class RoutineRunStore(string rootDirectory)
{
    /// <summary>Entries kept per routine before the oldest are dropped.</summary>
    public const int MaxEntries = 500;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public string RootDirectory => rootDirectory;

    /// <summary>Newest first, which is the order History renders.</summary>
    public IReadOnlyList<RoutineRunEntry> Load(string routineId)
    {
        var path = FilePath(routineId);
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var entries = JsonSerializer.Deserialize<List<RoutineRunEntry>>(File.ReadAllText(path)) ?? [];
            return [.. entries.OrderByDescending(static e => e.Time)];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A history we cannot read must not take the detail page down with it.
            return [];
        }
    }

    /// <summary>How many runs a routine has actually made, for the row's count.</summary>
    public int RunCount(string routineId) =>
        Load(routineId).Count(static e => e.Kind == RoutineRunEntry.RunKind);

    public void Append(string routineId, RoutineRunEntry entry)
    {
        var existing = Load(routineId).Reverse().ToList();
        existing.Add(entry);
        if (existing.Count > MaxEntries)
        {
            existing.RemoveRange(0, existing.Count - MaxEntries);
        }

        Write(routineId, existing);
    }

    /// <summary>Drops a routine's history, which is what deleting the routine does.</summary>
    public void Delete(string routineId)
    {
        try
        {
            var path = FilePath(routineId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a history left behind is orphaned, not harmful.
        }
    }

    private void Write(string routineId, List<RoutineRunEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(rootDirectory);
            var path = FilePath(routineId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, SerializerOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a history line must never fail the run that produced it.
        }
    }

    private string FilePath(string routineId) =>
        Path.Combine(rootDirectory, SanitizeId(routineId) + ".json");

    /// <summary>
    /// A routine id is free text (the SKILL.md store's ids are kebab-case, but a
    /// CronCreate job's is a GUID and a hand-edited file could hold anything),
    /// so it is reduced to a file name that cannot escape the directory.
    /// </summary>
    internal static string SanitizeId(string routineId)
    {
        var name = new string([.. routineId.Select(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        return name.Trim('-') is { Length: > 0 } trimmed ? trimmed : "routine";
    }
}
