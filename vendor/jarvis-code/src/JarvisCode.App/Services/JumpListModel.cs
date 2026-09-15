using System.Globalization;
using System.IO;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>One row of the Windows jump list: what it says and which link it opens.</summary>
public readonly record struct JumpListEntry(string Label, string Url);

/// <summary>
/// A jump-list category. <see cref="Name"/> is null for the OS's own Tasks
/// section, which the reference pushes last and never titles.
/// </summary>
public readonly record struct JumpListCategory(string? Name, IReadOnlyList<JumpListEntry> Items);

/// <summary>
/// What the jump list would show, before it is turned into shell objects: the two
/// standing tasks, the continue row when there is a session to continue, one row
/// per recent folder and one per session waiting for an answer.
/// </summary>
public sealed record JumpListModel(
    JumpListEntry NewChat,
    JumpListEntry NewCodeSession,
    JumpListEntry? ContinueLast,
    IReadOnlyList<JumpListEntry> CodeSessionIn,
    IReadOnlyList<JumpListEntry> Waiting);

/// <summary>What the model needs to know about one session to place it.</summary>
/// <param name="SessionId">The id a continue or needs-input link carries.</param>
/// <param name="Title">The session's own title, empty when it has none.</param>
/// <param name="Cwd">Its working directory, used for the folder rows and as a title fallback.</param>
/// <param name="LastActivityAt">Orders the recent list and picks the one to continue.</param>
/// <param name="IsArchived">Archived sessions are left out entirely.</param>
/// <param name="IsWaiting">True while the session has a permission prompt up.</param>
/// <param name="FolderExists">False once the folder is gone, which drops its folder row.</param>
public readonly record struct JumpListSession(
    string SessionId,
    string Title,
    string Cwd,
    DateTimeOffset LastActivityAt,
    bool IsArchived = false,
    bool IsWaiting = false,
    bool FolderExists = true);

/// <summary>
/// The Windows jump list, ported from the reference's own model and builder
/// (app.asar <c>index.chunk-DnlgCaT3.js</c>: <c>gZt</c> labels, <c>_Zt</c> model,
/// <c>vZt</c> categories, <c>fZt</c> label shaping, <c>mZt</c> waiting order,
/// <c>hZt</c> recency order, the caps <c>cZt</c>=60, <c>lZt</c>=3, <c>uZt</c>=5).
///
/// One deliberate delta, declared in the surface manifest: the reference's
/// <c>Other…</c> row is built by <c>gZt</c> but used only by the macOS dock menu —
/// <c>vZt</c> never emits it — so the Windows jump list has no such row here either.
/// </summary>
public static class JumpList
{
    public const string NewChatLabel = "New Chat";
    public const string NewCodeSessionLabel = "New Code Session";
    public const string NeedsYourInputLabel = "Needs Your Input";
    public const string CodeSessionLabel = "Jarvis Code Session";
    public const string LastSessionLabel = "Last Session";

    /// <summary>The reference's <c>cZt</c>: how long a jump-list label may be.</summary>
    public const int MaxLabelLength = 60;

    /// <summary>The reference's <c>lZt</c>: how many recent folders get a row.</summary>
    public const int MaxRecentFolders = 3;

    /// <summary>The reference's <c>uZt</c>: how many waiting sessions get a row.</summary>
    public const int MaxWaitingSessions = 5;

    public static string ContinueLabel(string title) => $"Continue “{title}”";

    /// <summary>
    /// The reference's <c>fZt</c>: normalize, collapse every run of whitespace to
    /// one space, trim, and cut a label longer than the cap to 59 characters plus
    /// an ellipsis. Counted in text elements rather than UTF-16 units, as the
    /// reference counts code points, so a label is never cut through a character.
    /// </summary>
    public static string ShapeLabel(string label)
    {
        var collapsed = new StringBuilder(label.Length);
        var pendingSpace = false;
        foreach (var ch in DisplayText.Sanitize(label))
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = collapsed.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                collapsed.Append(' ');
                pendingSpace = false;
            }

            collapsed.Append(ch);
        }

        var text = collapsed.ToString();
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            elements.Add((string)enumerator.Current);
        }

        return elements.Count > MaxLabelLength
            ? string.Concat(elements.Take(MaxLabelLength - 1)) + "…"
            : text;
    }

    /// <summary>
    /// Builds the model from the session list. <paramref name="source"/> is the
    /// <c>source</c> parameter every link carries — the reference stamps
    /// <c>jump_list</c> on the ones it hands the OS.
    /// </summary>
    public static JumpListModel Build(IEnumerable<JumpListSession> sessions, string source)
    {
        // The reference drops archived sessions first, then orders by recency and
        // reads the folder rows, the waiting rows and the continue row off that
        // one ordered list.
        var live = sessions
            .Where(static s => !s.IsArchived)
            .OrderByDescending(static s => s.LastActivityAt)
            .ToList();

        var folders = new List<JumpListEntry>();
        var seenFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in live)
        {
            if (!session.FolderExists || string.IsNullOrEmpty(session.Cwd) ||
                !seenFolders.Add(session.Cwd))
            {
                continue;
            }

            if (folders.Count == MaxRecentFolders)
            {
                break;
            }

            var name = Path.GetFileName(session.Cwd.TrimEnd(Path.DirectorySeparatorChar, '/'));
            folders.Add(new JumpListEntry(
                ShapeLabel(string.IsNullOrEmpty(name) ? session.Cwd : name),
                DeepLinks.Url("code", "/new", source, ("folder", session.Cwd))));
        }

        // The reference orders the waiting rows by activity *ascending*, so the
        // session that has waited longest is the one at the top.
        var waiting = live
            .Where(static s => s.IsWaiting)
            .OrderBy(static s => s.LastActivityAt)
            .Take(MaxWaitingSessions)
            .Select(s => new JumpListEntry(
                ShapeLabel(FallbackTitle(s)),
                DeepLinks.Url("code", "/needs-input", source, ("session", s.SessionId))))
            .ToList();

        var newest = live.Count > 0 ? live[0] : (JumpListSession?)null;
        return new JumpListModel(
            NewChat: new JumpListEntry(
                NewChatLabel,
                DeepLinks.Url("claude.ai", "/new", source, ("surface", "chat"))),
            NewCodeSession: new JumpListEntry(
                NewCodeSessionLabel,
                DeepLinks.Url("code", "/new", source)),
            ContinueLast: newest is { } last
                ? new JumpListEntry(
                    ContinueLabel(ShapeLabel(ContinueTitle(last))),
                    DeepLinks.Url("code", "/continue", source, ("session", last.SessionId)))
                : null,
            CodeSessionIn: folders,
            Waiting: waiting);
    }

    /// <summary>
    /// Turns the model into the categories the shell is handed, the reference's
    /// <c>vZt</c>: the waiting category first when it has rows, then the recent
    /// folders under the New Code Session label, then the Tasks section — which is
    /// the only one always present. <paramref name="removedUrls"/> are the rows the
    /// user removed from the list by hand, which the reference refuses to re-add.
    /// </summary>
    public static IReadOnlyList<JumpListCategory> Categories(
        JumpListModel model,
        IReadOnlySet<string>? removedUrls = null)
    {
        var removed = removedUrls ?? new HashSet<string>(StringComparer.Ordinal);
        var categories = new List<JumpListCategory>();

        var waiting = model.Waiting.Where(e => !removed.Contains(e.Url)).ToList();
        if (waiting.Count > 0)
        {
            categories.Add(new JumpListCategory(NeedsYourInputLabel, waiting));
        }

        var folders = model.CodeSessionIn.Where(e => !removed.Contains(e.Url)).ToList();
        if (folders.Count > 0)
        {
            categories.Add(new JumpListCategory(model.NewCodeSession.Label, folders));
        }

        var tasks = new List<JumpListEntry> { model.NewChat, model.NewCodeSession };
        if (model.ContinueLast is { } continueLast)
        {
            tasks.Add(continueLast);
        }

        categories.Add(new JumpListCategory(null, tasks));
        return categories;
    }

    private static string FallbackTitle(JumpListSession session) =>
        !string.IsNullOrEmpty(session.Title) ? session.Title
        : DirectoryName(session.Cwd) is { Length: > 0 } folder ? folder
        : CodeSessionLabel;

    private static string ContinueTitle(JumpListSession session) =>
        !string.IsNullOrEmpty(session.Title) ? session.Title
        : DirectoryName(session.Cwd) is { Length: > 0 } folder ? folder
        : LastSessionLabel;

    private static string DirectoryName(string cwd) =>
        string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, '/'));
}
