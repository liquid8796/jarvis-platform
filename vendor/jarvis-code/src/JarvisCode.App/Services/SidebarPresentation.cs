namespace JarvisCode.App.Services;

/// <summary>What the sidebar reads about one session before it decides where the row goes.</summary>
public sealed record SidebarSessionInput(
    string Id,
    string Title,
    string WorkingDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsArchived { get; init; }

    public bool IsPinned { get; init; }

    public bool IsUnread { get; init; }

    /// <summary>The user marked it unread by hand — the reference's <c>explicitUnread</c>.</summary>
    public bool ExplicitUnread { get; init; }

    public bool IsRunning { get; init; }

    /// <summary>The session is waiting on the user — the reference's <c>awaiting</c> live status.</summary>
    public bool NeedsInput { get; init; }

    /// <summary>Whether that wait is a question rather than a permission card.</summary>
    public AwaitingKind Awaiting { get; init; } = AwaitingKind.Permission;

    /// <summary>The session's last turn failed — the reference's <c>hasError</c>.</summary>
    public bool HasError { get; init; }

    /// <summary>The session has produced an answer at least once (its <c>hasCompleted</c>).</summary>
    public bool HasCompleted { get; init; }

    public string? GroupId { get; init; }

    public string? ColorKey { get; init; }

    public PrDisplayState PrState { get; init; } = PrDisplayState.None;

    public int? PrNumber { get; init; }

    /// <summary>The routine whose run started this session — the "Go to routine" row's gate.</summary>
    public string? RoutineId { get; init; }

    /// <summary>
    /// The session this one was spawned from as a side session; the sidebar files the
    /// row under that parent instead of beside it.
    /// </summary>
    public string? SideParentId { get; init; }

    /// <summary>The row's status in the reference's vocabulary.</summary>
    public SessionRowStatus Status => new()
    {
        IsArchived = IsArchived,
        HasError = HasError,
        IsRunning = IsRunning,
        IsAwaiting = NeedsInput,
        Awaiting = Awaiting,
        HasCompleted = HasCompleted,
        IsUnread = IsUnread,
        ExplicitUnread = ExplicitUnread,
        PrState = PrState,
    };

    /// <summary>The state its mark reports, run through the reference's ladder.</summary>
    public SessionRowState State => SessionRowStates.Resolve(Status);
}

/// <summary>
/// One run of rows the list draws together: a top-level row on its own, or a parent's
/// children under the accordion guide. The reference's <c>DD</c> emits exactly this —
/// consecutive rows sharing a parent become one guided block.
/// </summary>
public sealed record SidebarRowRun(string? ParentId, IReadOnlyList<SidebarSessionInput> Rows);

/// <summary>One section of the list: a header and the rows under it.</summary>
public sealed record SidebarSection(
    string Key,
    string Title,
    IReadOnlyList<SidebarSessionInput> Rows,
    IReadOnlyDictionary<string, string> ParentOf,
    string? GroupId = null,
    bool Collapsed = false,
    bool AcceptsDrop = false,
    bool Archived = false,
    bool Pinned = false,
    bool CarriesHeaderTrailing = false,
    string? EmptyBody = null)
{
    /// <summary>Whether the header draws the collapse caret; every bucket the reference builds does.</summary>
    public bool Collapsible { get; init; } = true;
}

/// <summary>
/// How the Code sidebar arranges its rows, ported from the reference desktop
/// 1.44121.2.0 (<c>shared-19-BctYnjt1.js</c>): the bucket builders <c>kO</c> (custom
/// groups), <c>IO</c> (date), <c>fk</c> (state) and <c>yO</c> (ungrouped), the
/// side-session nesting <c>Ro</c>/<c>ED</c>/<c>DD</c>, and the per-bucket truncation
/// its code sidebar runs at <c>cap = Infinity, truncateAt = 20</c> (@231097).
///
/// Two structural rules ride here rather than in the view, because they are what the
/// list <em>is</em>: the filter icon and the header menu are the trailing slot of the
/// <em>first</em> section (its <c>trailing: index === 0 ? headerTrailing : undefined</c>),
/// so there is no standing "Recents" row; and "Recents" is a bucket label only when
/// the list is ungrouped, which is the one case <c>yO</c> renders.
///
/// Pure so every grouping, every order and the nesting are unit-testable.
/// </summary>
public static class SidebarPresentation
{
    public const string RecentsHeader = "Recents";
    public const string PinnedHeader = "Pinned";
    public const string NothingPinned = "Nothing pinned yet";
    public const string UngroupedHeader = "Ungrouped";
    public const string ArchivedHeader = "Archived";
    public const string NoMatches = "No matches";
    public const string DragOrMove = "Drag or move sessions here";
    public const string NoActiveSessions = "No active sessions";
    public const string SearchRecents = "Search recents...";
    public const string SessionActions = "Session actions";
    public const string NewGroup = "New group…";
    public const string Today = "Today";
    public const string Yesterday = "Yesterday";
    public const string Older = "Older";

    /// <summary>The reference's fallback title for a session that has none.</summary>
    public const string UntitledSession = "General coding session";

    /// <summary>The reference's own bucket keys for the sections that are not a group.</summary>
    public const string RecentsKey = "recents";
    public const string PinnedKey = "pinned";
    public const string UngroupedKey = "::ungrouped";
    public const string ArchivedKey = "::archived";

    /// <summary>
    /// How many rows a bucket shows before it offers "Show more" — the reference's
    /// <c>truncateAt</c> for the code sidebar. Its <c>cap</c> there is unbounded, so
    /// nothing is dropped, only folded.
    /// </summary>
    public const int TruncateAt = 20;

    /// <summary>The reference's three date headings for a session list (its `headingKey` set).</summary>
    public static string DateBucket(DateTimeOffset at, DateTimeOffset now)
    {
        var days = (now.Date - at.Date).Days;
        return days switch
        {
            <= 0 => Today,
            1 => Yesterday,
            _ => Older,
        };
    }

    /// <summary>The name a folder section takes: the directory's own name, else its whole path.</summary>
    public static string FolderName(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : path;
    }

    /// <summary>The title a row shows: its own, or the reference's fallback.</summary>
    public static string TitleOf(SidebarSessionInput session) =>
        string.IsNullOrWhiteSpace(session.Title) ? UntitledSession : session.Title;

    /// <summary>
    /// The whole list: the status filter and the search text first, then Pinned, then
    /// the chosen grouping, then Archived — which is a section of its own unless the
    /// status filter is already showing nothing else. The first section returned
    /// carries the header's filter icon and menu.
    /// </summary>
    public static IReadOnlyList<SidebarSection> Build(
        IReadOnlyList<SidebarSessionInput> sessions,
        SidebarFilterState state,
        IReadOnlyList<(string Id, string Name, bool Collapsed)> groups,
        string filter,
        Func<string, bool> isCollapsed,
        DateTimeOffset now)
    {
        var visible = sessions
            .Where(s => SidebarFilterModel.PassesStatus(state.Status, s.IsArchived))
            .Where(s => PassesActivity(s, state, now))
            .Where(s => Matches(s, filter))
            .ToList();

        var sections = new List<SidebarSection>();
        var pinned = Order(visible.Where(static s => s.IsPinned && !s.IsArchived), state.SortBy).ToList();
        if (pinned.Count > 0 || (string.IsNullOrWhiteSpace(filter) && state.Status != "archived"))
        {
            sections.Add(Section(PinnedKey, PinnedHeader, pinned, isCollapsed, pinnedSection: true, emptyBody: NothingPinned));
        }

        var body = visible.Where(s => !s.IsPinned && !s.IsArchived).ToList();
        switch (state.GroupBy)
        {
            case "custom":
                AddCustomGroups(sections, body, groups, state.SortBy, isCollapsed);
                break;
            case "project":
                AddKeyed(sections, body, state.SortBy, isCollapsed,
                    s => FolderName(s.WorkingDirectory), emptyBody: NoActiveSessions);
                break;
            case "date":
                AddKeyed(sections, body, state.SortBy, isCollapsed,
                    s => DateBucket(s.UpdatedAt, now), rank: DateOrder);
                break;
            case "state":
                AddStateBuckets(sections, body, state.SortBy, isCollapsed);
                break;
            default:
                if (body.Count > 0)
                {
                    sections.Add(Section(RecentsKey, RecentsHeader,
                        Order(body, state.SortBy).ToList(), isCollapsed));
                }

                break;
        }

        // Archived rows are their own section at the end, and only while the status
        // filter lets them through at all - under the reference's default "Active"
        // they are simply not listed.
        var archived = Order(visible.Where(static s => s.IsArchived), state.SortBy).ToList();
        if (archived.Count > 0)
        {
            sections.Add(Section(ArchivedKey, ArchivedHeader, archived, isCollapsed, archived: true));
        }

        if (visible.Count == 0 && sections.Count == 1 && sections[0].Pinned)
        {
            sections.Add(Section(RecentsKey, RecentsHeader, [], isCollapsed, emptyBody: NoActiveSessions) with { Collapsible = false });
        }

        return sections.Count == 0
            ? sections
            : [sections[0] with { CarriesHeaderTrailing = true }, .. sections.Skip(1)];
    }

    /// <summary>
    /// The reference's Last-activity filter, which only the State grouping offers:
    /// a row whose last activity is older than the chosen window drops out. Zero is
    /// its "All".
    /// </summary>
    private static bool PassesActivity(SidebarSessionInput session, SidebarFilterState state, DateTimeOffset now) =>
        state.GroupBy != "state" || state.StateActivityDays <= 0 ||
        (now - session.UpdatedAt) <= TimeSpan.FromDays(state.StateActivityDays);

    private static SidebarSection Section(
        string key,
        string title,
        IReadOnlyList<SidebarSessionInput> rows,
        Func<string, bool> isCollapsed,
        string? groupId = null,
        bool acceptsDrop = false,
        bool archived = false,
        bool pinnedSection = false,
        string? emptyBody = null)
    {
        var (ordered, parentOf) = Nest(rows);
        return new SidebarSection(key, title, ordered, parentOf, groupId, isCollapsed(key), acceptsDrop,
            archived, pinnedSection, EmptyBody: emptyBody);
    }

    private static void AddCustomGroups(
        List<SidebarSection> sections,
        IReadOnlyList<SidebarSessionInput> body,
        IReadOnlyList<(string Id, string Name, bool Collapsed)> groups,
        string sortBy,
        Func<string, bool> isCollapsed)
    {
        foreach (var (id, name, collapsed) in groups)
        {
            var rows = Order(body.Where(s => s.GroupId == id), sortBy).ToList();
            var (ordered, parentOf) = Nest(rows);
            sections.Add(new SidebarSection(id, name, ordered, parentOf, GroupId: id, Collapsed: collapsed,
                AcceptsDrop: true, EmptyBody: DragOrMove));
        }

        var ungrouped = Order(body.Where(static s => s.GroupId is null), sortBy).ToList();
        var (ungroupedRows, ungroupedParents) = Nest(ungrouped);
        sections.Add(new SidebarSection(UngroupedKey, UngroupedHeader, ungroupedRows, ungroupedParents,
            Collapsed: isCollapsed(UngroupedKey), AcceptsDrop: true, EmptyBody: DragOrMove));
    }

    /// <summary>
    /// The State grouping: the reference's four buckets in its own order, each named
    /// by its own heading, and a bucket with nothing in it is not drawn.
    /// </summary>
    private static void AddStateBuckets(
        List<SidebarSection> sections,
        IReadOnlyList<SidebarSessionInput> body,
        string sortBy,
        Func<string, bool> isCollapsed)
    {
        var buckets = new Dictionary<SessionStateBucket, List<SidebarSessionInput>>();
        foreach (var session in body)
        {
            var bucket = SessionRowStates.Bucket(session.Status);
            if (!buckets.TryGetValue(bucket, out var rows))
            {
                rows = [];
                buckets[bucket] = rows;
            }

            rows.Add(session);
        }

        foreach (var bucket in SessionRowStates.BucketOrder)
        {
            if (buckets.TryGetValue(bucket, out var rows))
            {
                sections.Add(Section(SessionRowStates.BucketKey(bucket), SessionRowStates.Heading(bucket),
                    Order(rows, sortBy).ToList(), isCollapsed));
            }
        }
    }

    private static void AddKeyed(
        List<SidebarSection> sections,
        IReadOnlyList<SidebarSessionInput> body,
        string sortBy,
        Func<string, bool> isCollapsed,
        Func<SidebarSessionInput, string> key,
        Func<string, int>? rank = null,
        string? emptyBody = null)
    {
        var buckets = new List<(string Title, List<SidebarSessionInput> Rows)>();
        foreach (var session in body)
        {
            var title = key(session);
            var bucket = buckets.FirstOrDefault(b => b.Title == title);
            if (bucket.Rows is null)
            {
                bucket = (title, []);
                buckets.Add(bucket);
            }

            bucket.Rows.Add(session);
        }

        IEnumerable<(string Title, List<SidebarSessionInput> Rows)> ordered = rank is null
            ? buckets.OrderBy(static b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            : buckets.OrderBy(b => rank(b.Title));
        foreach (var (title, rows) in ordered)
        {
            sections.Add(Section(title, title, Order(rows, sortBy).ToList(), isCollapsed, emptyBody: emptyBody));
        }
    }

    private static int DateOrder(string title) => title == Today ? 0 : title == Yesterday ? 1 : 2;

    /// <summary>The chosen sort applied to one bucket.</summary>
    public static IEnumerable<SidebarSessionInput> Order(IEnumerable<SidebarSessionInput> rows, string sortBy) =>
        SidebarFilterModel.Sort(rows, sortBy,
            static s => s.Title,
            static s => s.UpdatedAt,
            static s => s.CreatedAt);

    private static bool Matches(SidebarSessionInput session, string filter) =>
        filter.Length == 0 || session.Title.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The reference's <c>Ro</c> followed by its <c>ED</c>: resolve each row's side
    /// parent to the <em>root</em> of its chain, drop a parent the bucket does not
    /// hold, then emit each parent immediately followed by its children.
    /// </summary>
    public static (IReadOnlyList<SidebarSessionInput> Ordered, IReadOnlyDictionary<string, string> ParentOf) Nest(
        IReadOnlyList<SidebarSessionInput> rows)
    {
        var byId = rows.ToDictionary(static r => r.Id, StringComparer.Ordinal);
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            // Walk to the root of the chain, guarding against a cycle, exactly as its
            // `Ro` does - a grandchild is filed under the top-most ancestor.
            var parent = RootParent(row, byId);
            if (parent is not null && parent != row.Id && byId.ContainsKey(parent))
            {
                parentOf[row.Id] = parent;
            }
        }

        // A parent that is itself a child cannot host a block (its `for([e,t] of n)`).
        foreach (var child in parentOf.Keys.ToList())
        {
            if (parentOf.ContainsKey(parentOf[child]))
            {
                parentOf.Remove(child);
            }
        }

        if (parentOf.Count == 0)
        {
            return (rows, parentOf);
        }

        var childrenOf = new Dictionary<string, List<SidebarSessionInput>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (parentOf.TryGetValue(row.Id, out var parent))
            {
                if (!childrenOf.TryGetValue(parent, out var list))
                {
                    list = [];
                    childrenOf[parent] = list;
                }

                list.Add(row);
            }
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<SidebarSessionInput>(rows.Count);
        foreach (var row in rows)
        {
            var key = parentOf.TryGetValue(row.Id, out var parent) ? parent : row.Id;
            if (!emitted.Add(key))
            {
                continue;
            }

            if (byId.TryGetValue(key, out var head))
            {
                ordered.Add(head);
            }

            if (childrenOf.TryGetValue(key, out var children))
            {
                ordered.AddRange(children);
            }
        }

        return (ordered, parentOf);
    }

    private static string? RootParent(
        SidebarSessionInput row,
        IReadOnlyDictionary<string, SidebarSessionInput> byId)
    {
        var parent = row.SideParentId;
        if (parent is null || parent == row.Id)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { row.Id, parent };
        while (byId.TryGetValue(parent, out var next) &&
               next.SideParentId is { } grandparent &&
               grandparent != parent &&
               seen.Add(grandparent) &&
               byId.ContainsKey(grandparent))
        {
            parent = grandparent;
        }

        return parent;
    }

    /// <summary>
    /// The reference's <c>DD</c>: consecutive rows sharing a parent become one guided
    /// block, and everything else stays a top-level run.
    /// </summary>
    public static IReadOnlyList<SidebarRowRun> Runs(
        IReadOnlyList<SidebarSessionInput> rows,
        IReadOnlyDictionary<string, string> parentOf)
    {
        if (parentOf.Count == 0)
        {
            return rows.Count == 0 ? [] : [new SidebarRowRun(null, rows)];
        }

        var runs = new List<(string? ParentId, List<SidebarSessionInput> Rows)>();
        string? lastTop = null;
        foreach (var row in rows)
        {
            parentOf.TryGetValue(row.Id, out var parent);
            var last = runs.Count > 0 ? runs[^1] : default;
            if (parent is not null && runs.Count > 0 && last.ParentId == parent)
            {
                last.Rows.Add(row);
            }
            else if (parent is not null && lastTop == parent)
            {
                runs.Add((parent, [row]));
            }
            else if (runs.Count > 0 && last.ParentId is null)
            {
                last.Rows.Add(row);
                lastTop = row.Id;
            }
            else
            {
                runs.Add((null, [row]));
                lastTop = row.Id;
            }
        }

        return [.. runs.Select(r => new SidebarRowRun(r.ParentId, r.Rows))];
    }

    /// <summary>
    /// The folders a session has run in, newest use first and nothing repeated. This
    /// feeds the <em>home view's</em> folder section; the sidebar has no folder list of
    /// its own, which is the reference's own shape.
    /// </summary>
    public static IReadOnlyList<string> RecentFolders(IEnumerable<SidebarSessionInput> sessions, int limit = 8)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        foreach (var session in sessions.OrderByDescending(static s => s.UpdatedAt))
        {
            if (session.WorkingDirectory.Length > 0 && seen.Add(session.WorkingDirectory))
            {
                folders.Add(session.WorkingDirectory);
                if (folders.Count >= limit)
                {
                    break;
                }
            }
        }

        return folders;
    }

    /// <summary>
    /// The reference's <c>zD</c>: a bucket shows <see cref="TruncateAt"/> rows plus
    /// whatever "Show more" has already revealed, and reports how many are left. A
    /// collapsed bucket is not truncated because it is not drawn.
    /// </summary>
    public static (IReadOnlyList<SidebarSessionInput> Visible, int HiddenCount) Truncate(
        IReadOnlyList<SidebarSessionInput> rows,
        int extraRevealed,
        int truncateAt = TruncateAt)
    {
        var limit = truncateAt + Math.Max(0, extraRevealed);
        return rows.Count <= limit
            ? (rows, 0)
            : ([.. rows.Take(limit)], rows.Count - limit);
    }
}
