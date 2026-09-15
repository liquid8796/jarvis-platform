using System.Globalization;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Services;

/// <summary>One labelled run of chats in the sidebar.</summary>
/// <param name="Label">The header above it.</param>
/// <param name="Sessions">The chats, newest first.</param>
/// <param name="Collapsible">Whether the header carries the Show/Hide affordance.</param>
public sealed record ChatListSection(string Label, IReadOnlyList<SessionSummary> Sessions, bool Collapsible = false);

/// <summary>
/// The Chat sidebar's sections, composed from the two lists the reference builds:
/// its starred/recents split (<c>c71dee58b-DV0t1uRZ.js</c> — a collapsible
/// "Starred" section with a hover-revealed Show/Hide, then "Recents", both hidden
/// while a search is running, and a "Show more" that lengthens Recents twenty at a
/// time) and its day bucketing (<c>shared-17-BG9iAXbK.js</c>, its <c>OD</c>: seven
/// local midnights back from today, labelled Today, Yesterday, then the date, and
/// everything older in one "Older").
/// </summary>
public static class ChatListSections
{
    public const string Starred = "Starred";       // V7cUPvWFo3
    public const string Recents = "Recents";       // wA4FIMmtlS
    public const string Today = "Today";           // zWgbGgjUUg
    public const string Yesterday = "Yesterday";   // 6dIxDP1C8c
    public const string Older = "Older";           // iIeVwABUWb
    public const string ShowMore = "Show more";    // aWpBzjCXKS
    public const string Show = "Show";             // K7AkdLoAj6
    public const string Hide = "Hide";             // VA/Z1SW3vH
    public const string Star = "Star";             // VwapsmdMes
    public const string Unstar = "Unstar";         // Y5zRLWgCMP
    public const string RenameChat = "Rename chat"; // Oi9LAY6C5H
    public const string DeleteChat = "Delete chat"; // ngIzbjIFxF
    public const string UntitledChat = "Untitled chat"; // lfYALmQol/

    /// <summary>How many more chats a "Show more" click reveals.</summary>
    public const int PageSize = 20;

    /// <summary>
    /// The sections to draw. A search collapses everything into one flat list, as
    /// the reference does — its starred and recents sections both stand down while
    /// <c>searchActive</c>.
    /// </summary>
    public static IReadOnlyList<ChatListSection> Build(
        IReadOnlyList<SessionSummary> sessions,
        IReadOnlyCollection<string> starredIds,
        string? query,
        int shown,
        DateTimeOffset now)
    {
        var trimmed = query?.Trim() ?? "";
        var ordered = sessions.OrderByDescending(static s => s.UpdatedAt).ToList();
        if (trimmed.Length > 0)
        {
            return
            [
                new ChatListSection(
                    Recents,
                    [.. ordered.Where(s => s.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase))]),
            ];
        }

        var starred = ordered.Where(s => starredIds.Contains(s.Id)).ToList();
        var rest = ordered.Where(s => !starredIds.Contains(s.Id)).Take(shown).ToList();

        var sections = new List<ChatListSection>();
        if (starred.Count > 0)
        {
            sections.Add(new ChatListSection(Starred, starred, Collapsible: true));
        }

        sections.AddRange(DayBuckets(rest, now));
        return sections;
    }

    /// <summary>Whether the list has more chats than are shown, so "Show more" appears.</summary>
    public static bool HasMore(
        IReadOnlyList<SessionSummary> sessions, IReadOnlyCollection<string> starredIds, int shown) =>
        sessions.Count(s => !starredIds.Contains(s.Id)) > shown;

    /// <summary>
    /// The reference's day buckets: index 0 is today, 1 yesterday, 2..6 that day's
    /// date, and anything older than the seventh midnight goes to "Older".
    /// </summary>
    public static IReadOnlyList<ChatListSection> DayBuckets(
        IReadOnlyList<SessionSummary> sessions, DateTimeOffset now)
    {
        var midnight = new DateTimeOffset(now.Date, now.Offset);
        var boundaries = Enumerable.Range(0, 7).Select(day => midnight.AddDays(-day)).ToArray();
        var days = new List<SessionSummary>[7];
        for (var i = 0; i < days.Length; i++)
        {
            days[i] = [];
        }

        var older = new List<SessionSummary>();
        foreach (var session in sessions)
        {
            var index = Array.FindIndex(boundaries, boundary => session.UpdatedAt >= boundary);
            if (index < 0)
            {
                older.Add(session);
            }
            else
            {
                days[index].Add(session);
            }
        }

        var sections = new List<ChatListSection>();
        for (var day = 0; day < days.Length; day++)
        {
            if (days[day].Count == 0)
            {
                continue;
            }

            sections.Add(new ChatListSection(
                day == 0 ? Today
                : day == 1 ? Yesterday
                : boundaries[day].ToString("MMM d", CultureInfo.CurrentCulture),
                days[day]));
        }

        if (older.Count > 0)
        {
            sections.Add(new ChatListSection(Older, older));
        }

        return sections;
    }
}
