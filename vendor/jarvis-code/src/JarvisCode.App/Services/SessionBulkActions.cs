namespace JarvisCode.App.Services;

/// <summary>
/// The reference's "Bulk actions for older sessions" — the submenu its Recents header
/// menu carries, with its two questions and the lines it reports the result in
/// (desktop 1.44121.2.0, <c>shared-19-BctYnjt1.js</c>@138600, wording from the
/// catalogue under its own message ids).
///
/// One clause of the reference's copy is deliberately never rendered: its bodies close
/// with a <c>{truncated, select, true {…}}</c> arm saying that sessions it had not
/// loaded were left alone, which describes a paged session list. This build lists every
/// session on disk, so <c>truncated</c> is always false and that arm cannot be reached.
/// </summary>
public static class SessionBulkActions
{
    /// <summary>The submenu's own label (its <c>MyneLjoq5y</c>).</summary>
    public const string SubmenuLabel = "Bulk actions for older sessions";

    /// <summary>What counts as older — the reference says "older than a week" throughout.</summary>
    public static TimeSpan OlderThan { get; } = TimeSpan.FromDays(7);

    public const string ArchiveTitle = "Archive older sessions?";
    public const string DeleteTitle = "Delete older sessions?";
    public const string ArchiveConfirm = "Archive all";
    public const string DeleteConfirm = "Delete all";
    public const string Counting = "Counting sessions older than a week…";

    /// <summary>The submenu rows, which carry the count the action would move.</summary>
    public static string ArchiveAll(int count) => $"Archive all ({count})";

    public static string DeleteAll(int count) => $"Delete all ({count})";

    public static string ArchiveBody(int count) =>
        $"This will archive {Sessions(count)} older than a week. Pinned sessions are not affected.";

    public static string DeleteBody(int count) =>
        $"This will permanently delete {Sessions(count)} older than a week. Pinned sessions are not " +
        "affected. This cannot be undone.";

    public static string Archived(int count) => $"Archived {OlderSessions(count)}";

    public static string Deleted(int count) => $"Deleted {OlderSessions(count)}";

    private static string Sessions(int count) => count == 1 ? "1 session" : $"{count} sessions";

    private static string OlderSessions(int count) =>
        count == 1 ? "1 older session" : $"{count} older sessions";
}
