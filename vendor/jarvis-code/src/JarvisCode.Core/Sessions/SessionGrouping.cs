namespace JarvisCode.Core.Sessions;

/// <summary>Buckets session summaries by project (working directory).</summary>
public static class SessionGrouping
{
    public const string UngroupedName = "Ungrouped";

    /// <summary>A project bucket. Key is the normalized directory, "" for sessions without one.</summary>
    public sealed record Group(string Key, string DisplayName, IReadOnlyList<SessionSummary> Sessions);

    /// <summary>
    /// Groups sessions by working directory (newest first inside each group), orders the
    /// groups by their most recent activity, and keeps the directoryless bucket last.
    /// A non-empty filter keeps sessions whose title contains it — or, when the project
    /// name itself matches, the whole group — case-insensitively.
    /// </summary>
    public static IReadOnlyList<Group> Build(IEnumerable<SessionSummary> sessions, string? filter = null)
    {
        var query = filter?.Trim() ?? "";

        var groups = sessions
            .GroupBy(s => NormalizeKey(s.WorkingDirectory), StringComparer.OrdinalIgnoreCase)
            .Select(bucket =>
            {
                var name = DisplayNameFor(bucket.Key);
                var matches = query.Length > 0 && !name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    ? bucket.Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    : bucket.AsEnumerable();
                return new Group(bucket.Key, name, [.. matches.OrderByDescending(s => s.UpdatedAt)]);
            })
            .Where(g => g.Sessions.Count > 0);

        return
        [
            .. groups
                .OrderBy(g => g.Key.Length == 0) // the directoryless bucket sinks to the end
                .ThenByDescending(g => g.Sessions[0].UpdatedAt),
        ];
    }

    private static string NormalizeKey(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return "";
        var trimmed = workingDirectory.Trim();
        var withoutSeparators = trimmed.TrimEnd('\\', '/');
        // A bare root ("/", "D:\") must not collapse into the directoryless bucket.
        return withoutSeparators.Length > 0 ? withoutSeparators : trimmed;
    }

    private static string DisplayNameFor(string key)
    {
        if (key.Length == 0)
            return UngroupedName;
        int cut = key.LastIndexOfAny(['\\', '/']);
        var leaf = cut >= 0 ? key[(cut + 1)..] : key;
        return leaf.Length > 0 ? leaf : key;
    }
}
