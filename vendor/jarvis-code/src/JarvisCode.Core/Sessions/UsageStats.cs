using System.Text.Json;

namespace JarvisCode.Core.Sessions;

/// <summary>One calendar day of recorded activity.</summary>
public sealed class DayActivity
{
    public int Messages { get; set; }
    public long Tokens { get; set; }
    public Dictionary<string, long> TokensByModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> MessagesByModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Message count per local hour (0–23), for the peak-hour stat.</summary>
    public Dictionary<int, int> Hours { get; set; } = [];

    /// <summary>
    /// Tokens per surface ("chat" / "code"), which the Usage page charts by tab.
    /// Absent for days recorded before the split existed.
    /// </summary>
    public Dictionary<string, long> TokensBySurface { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Input tokens of the day's turns; 0 for days recorded before the split.</summary>
    public long InputTokens { get; set; }

    /// <summary>Output tokens of the day's turns; 0 for days recorded before the split.</summary>
    public long OutputTokens { get; set; }
}

public sealed class UsageStatsData
{
    public Dictionary<string, DayActivity> Days { get; set; } = [];

    public static string KeyFor(DateOnly date) => date.ToString("yyyy-MM-dd");
}

/// <summary>
/// Persistent per-day usage log behind the welcome-card statistics (sessions,
/// streaks, peak hour, favorite model, activity heatmap). One small JSON file;
/// recording is load-merge-save and must never break a turn.
/// </summary>
public sealed class UsageStatsStore(string filePath)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly Lock _gate = new();

    public UsageStatsData Load()
    {
        try
        {
            if (!File.Exists(filePath))
                return new UsageStatsData();
            return JsonSerializer.Deserialize<UsageStatsData>(File.ReadAllText(filePath), SerializerOptions)
                ?? new UsageStatsData();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UsageStatsData();
        }
    }

    public void Record(DateTimeOffset when, string modelId, int messages, long tokens)
        => Record(when, modelId, messages, tokens, surface: null, inputTokens: 0, outputTokens: 0);

    /// <summary>
    /// Records a turn, splitting it by surface and by input/output the way the
    /// Usage page charts it. The four-argument overload above stays for callers
    /// that have neither.
    /// </summary>
    public void Record(
        DateTimeOffset when,
        string modelId,
        int messages,
        long tokens,
        string? surface,
        long inputTokens,
        long outputTokens)
    {
        lock (_gate)
        {
            try
            {
                var data = Load();
                var key = UsageStatsData.KeyFor(DateOnly.FromDateTime(when.LocalDateTime));
                if (!data.Days.TryGetValue(key, out var day))
                    data.Days[key] = day = new DayActivity();
                day.Messages += messages;
                day.Tokens += tokens;
                day.TokensByModel[modelId] = day.TokensByModel.GetValueOrDefault(modelId) + tokens;
                day.MessagesByModel[modelId] = day.MessagesByModel.GetValueOrDefault(modelId) + messages;
                int hour = when.LocalDateTime.Hour;
                day.Hours[hour] = day.Hours.GetValueOrDefault(hour) + messages;
                if (surface is { Length: > 0 })
                {
                    day.TokensBySurface[surface] = day.TokensBySurface.GetValueOrDefault(surface) + tokens;
                }

                day.InputTokens += inputTokens;
                day.OutputTokens += outputTokens;

                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                var tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(data, SerializerOptions));
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Statistics are a convenience; losing one data point is fine.
            }
        }
    }
}

public sealed record HeatmapCell(DateOnly Date, int Level, int Messages);

public sealed record ModelUsage(string ModelId, long Tokens, int Messages);

public sealed record UsageStatsSummary(
    long TotalTokens,
    int TotalMessages,
    int ActiveDays,
    int CurrentStreak,
    int LongestStreak,
    int PeakHour,
    string? FavoriteModel,
    IReadOnlyList<HeatmapCell> Heatmap,
    IReadOnlyList<ModelUsage> ByModel);

public static class UsageStats
{
    public const int HeatmapDays = 112; // 16 weeks, GitHub-contribution style

    /// <summary>
    /// Aggregates the day log into the welcome-card numbers. <paramref name="windowDays"/>
    /// filters to the trailing N days (null = everything). <paramref name="backfill"/>
    /// supplies (date, messages) pairs from sources that predate the log — e.g. session
    /// timestamps — and only fills days the log knows nothing about.
    /// </summary>
    public static UsageStatsSummary Summarize(
        UsageStatsData data,
        DateOnly today,
        int? windowDays = null,
        IEnumerable<(DateOnly Date, int Messages)>? backfill = null)
    {
        var days = new SortedDictionary<DateOnly, DayActivity>();
        foreach (var (key, activity) in data.Days)
        {
            if (DateOnly.TryParse(key, out var date))
                days[date] = activity;
        }
        if (backfill is not null)
        {
            foreach (var (date, messages) in backfill)
            {
                if (messages > 0 && !days.ContainsKey(date))
                    days[date] = new DayActivity { Messages = messages };
            }
        }
        if (windowDays is { } window)
        {
            var cutoff = today.AddDays(-(window - 1));
            foreach (var date in days.Keys.Where(d => d < cutoff || d > today).ToList())
                days.Remove(date);
        }

        long totalTokens = days.Values.Sum(d => d.Tokens);
        int totalMessages = days.Values.Sum(d => d.Messages);

        var active = days.Where(kv => kv.Value.Messages > 0 || kv.Value.Tokens > 0)
            .Select(kv => kv.Key).ToHashSet();

        int currentStreak = 0;
        // A quiet today does not break the streak; it just has not been extended yet.
        var cursor = active.Contains(today) ? today : today.AddDays(-1);
        while (active.Contains(cursor))
        {
            currentStreak++;
            cursor = cursor.AddDays(-1);
        }

        int longestStreak = 0;
        int run = 0;
        DateOnly? previous = null;
        foreach (var date in active.Order())
        {
            run = previous is { } p && date == p.AddDays(1) ? run + 1 : 1;
            longestStreak = Math.Max(longestStreak, run);
            previous = date;
        }

        var hourTotals = new int[24];
        foreach (var day in days.Values)
        {
            foreach (var (hour, count) in day.Hours)
            {
                if (hour is >= 0 and < 24)
                    hourTotals[hour] += count;
            }
        }
        int peakHour = hourTotals.Any(h => h > 0)
            ? Array.IndexOf(hourTotals, hourTotals.Max())
            : -1;

        var byModel = days.Values
            .SelectMany(d => d.TokensByModel.Keys.Concat(d.MessagesByModel.Keys))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(model => new ModelUsage(
                model,
                days.Values.Sum(d => d.TokensByModel.GetValueOrDefault(model)),
                days.Values.Sum(d => d.MessagesByModel.GetValueOrDefault(model))))
            .OrderByDescending(m => m.Tokens)
            .ThenByDescending(m => m.Messages)
            .ToList();
        var favorite = byModel.FirstOrDefault()?.ModelId;

        int heatmapDays = Math.Min(windowDays ?? HeatmapDays, HeatmapDays);
        int maxMessages = Math.Max(1, days.Values.Select(d => d.Messages).DefaultIfEmpty(0).Max());
        var heatmap = new List<HeatmapCell>(heatmapDays);
        for (var date = today.AddDays(-(heatmapDays - 1)); date <= today; date = date.AddDays(1))
        {
            int messages = days.TryGetValue(date, out var day) ? day.Messages : 0;
            int level = messages == 0 ? 0 : Math.Clamp((int)Math.Ceiling(messages * 4.0 / maxMessages), 1, 4);
            heatmap.Add(new HeatmapCell(date, level, messages));
        }

        return new UsageStatsSummary(
            totalTokens, totalMessages, active.Count, currentStreak, longestStreak,
            peakHour, favorite, heatmap, byModel);
    }
}
