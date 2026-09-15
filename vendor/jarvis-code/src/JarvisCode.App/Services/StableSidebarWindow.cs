namespace JarvisCode.App.Services;

/// <summary>Keep the visible row identities/order while the reader is interacting with a bucket.</summary>
public static class StableSidebarWindow
{
    public static IReadOnlyList<SidebarSessionInput> Select(IReadOnlyList<SidebarSessionInput> rows,
        IReadOnlyList<string>? previous, int count, bool hold)
    {
        if (!hold || previous is null) return rows.Take(count).ToArray();
        var byId = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var kept = previous.Where(byId.ContainsKey).Take(count).Select(id => byId[id]).ToList();
        var ids = kept.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        kept.AddRange(rows.Where(row => !ids.Contains(row.Id)).Take(Math.Max(0, count - kept.Count)));
        return kept;
    }
}
