using System.IO;
using System.Text.Json;

namespace JarvisCode.App.Services;

public sealed class SessionGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
    public List<string> SessionIds { get; set; } = [];
}

/// <summary>
/// Where a session came from, when a spawn_task chip started it. The reference
/// records this on the <em>spawned</em> session rather than on the chip, which is
/// what lets the suggesting session's transcript still say "Started session"
/// after the chip has been evicted from its twenty-deep queue, or after the
/// transcript has been reopened.
/// </summary>
public sealed class SpawnedFrom
{
    /// <summary>The session whose spawn_task call suggested it.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The task id that call answered with.</summary>
    public string TaskId { get; set; } = "";
}

public sealed class SessionGroupsData
{
    public List<SessionGroup> Groups { get; set; } = [];
    public List<string> ArchivedSessionIds { get; set; } = [];
    public List<string> PinnedSessionIds { get; set; } = [];
    public List<string> UnreadSessionIds { get; set; } = [];

    /// <summary>
    /// The sessions the user marked unread by hand. The reference keeps that apart
    /// from the unread a background turn raises (its `explicitUnread`), because an
    /// explicit one outranks a pull request in the row's status ladder.
    /// </summary>
    public List<string> ExplicitUnreadSessionIds { get; set; } = [];

    public List<string> CoordinatorSessionIds { get; set; } = [];

    /// <summary>
    /// Which sections the user has collapsed, keyed the reference's way by bucket key
    /// — its one `toggleGroupCollapsed(bucketKey)` covers every kind of section, not
    /// only a custom group.
    /// </summary>
    public Dictionary<string, bool> CollapsedBuckets { get; set; } = [];

    /// <summary>
    /// The session colour the reference stores as a `color:{key}` tag; kept here
    /// beside the other sidebar state, since the engine's Session model is untouched.
    /// </summary>
    public Dictionary<string, string> SessionColors { get; set; } = [];

    /// <summary>Which sessions the home view's attention list should skip, and until when.</summary>
    public Dictionary<string, DateTimeOffset> DismissedAt { get; set; } = [];

    /// <summary>
    /// Keyed by the spawned session's own id, so deleting that session takes the
    /// record with it and the row that suggested it reads as a suggestion again —
    /// which is what the reference's own walk of the live session list does.
    /// </summary>
    public Dictionary<string, SpawnedFrom> SpawnedFrom { get; set; } = [];
    public bool UngroupedCollapsed { get; set; }
    public bool ArchivedCollapsed { get; set; } = true;
}

/// <summary>
/// Sidebar organization for Code sessions: user-created groups, drag/move
/// membership, and archived sessions. Presentation state, so it lives in the
/// App's own file — the engine's Session model stays untouched.
/// </summary>
public sealed class SessionGroupsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;

    public SessionGroupsStore(string filePath)
    {
        _filePath = filePath;
        Data = LoadFrom(filePath);
    }

    public SessionGroupsData Data { get; }

    public event EventHandler? Changed;

    public SessionGroup CreateGroup(string name)
    {
        var group = new SessionGroup { Name = name.Trim() };
        Data.Groups.Add(group);
        Save();
        return group;
    }

    public void RenameGroup(string groupId, string newName)
    {
        if (Find(groupId) is { } group && newName.Trim() is { Length: > 0 } name)
        {
            group.Name = name;
            Save();
        }
    }

    /// <summary>Deletes the group; its sessions fall back to Ungrouped.</summary>
    public void DeleteGroup(string groupId)
    {
        if (Data.Groups.RemoveAll(g => g.Id == groupId) > 0)
        {
            Save();
        }
    }

    public void SetCollapsed(string groupId, bool collapsed)
    {
        if (Find(groupId) is { } group)
        {
            group.Collapsed = collapsed;
            Save();
        }
    }

    /// <summary>Moves a session into a group; null clears it back to Ungrouped.</summary>
    public void MoveSession(string sessionId, string? groupId)
    {
        foreach (var group in Data.Groups)
        {
            group.SessionIds.Remove(sessionId);
        }

        if (groupId is not null && Find(groupId) is { } target)
        {
            target.SessionIds.Add(sessionId);
        }

        Save();
    }

    public SessionGroup? GroupOf(string sessionId)
        => Data.Groups.FirstOrDefault(g => g.SessionIds.Contains(sessionId));

    public bool IsArchived(string sessionId) => Data.ArchivedSessionIds.Contains(sessionId);

    public void SetArchived(string sessionId, bool archived)
        => SetMembership(Data.ArchivedSessionIds, sessionId, archived);

    /// <summary>Pinned sessions render in their own section at the top, newest pin last.</summary>
    public bool IsPinned(string sessionId) => Data.PinnedSessionIds.Contains(sessionId);

    public void SetPinned(string sessionId, bool pinned)
        => SetMembership(Data.PinnedSessionIds, sessionId, pinned);

    /// <summary>
    /// Unread: a turn finished in this session while the user was looking
    /// elsewhere. Opening the session clears it; Ctrl+Alt+U toggles it by hand.
    /// </summary>
    public bool IsUnread(string sessionId) => Data.UnreadSessionIds.Contains(sessionId);

    public void SetUnread(string sessionId, bool unread)
    {
        if (!unread)
        {
            Data.ExplicitUnreadSessionIds.Remove(sessionId);
        }

        SetMembership(Data.UnreadSessionIds, sessionId, unread);
    }

    /// <summary>
    /// The user marked it unread from a menu, which the reference tracks apart from a
    /// background turn's unread. Marking read clears both.
    /// </summary>
    public bool IsExplicitUnread(string sessionId) => Data.ExplicitUnreadSessionIds.Contains(sessionId);

    public void SetExplicitUnread(string sessionId, bool unread)
    {
        SetMembership(Data.ExplicitUnreadSessionIds, sessionId, unread);
        SetMembership(Data.UnreadSessionIds, sessionId, unread);
    }

    /// <summary>Whether a section is folded shut, by the reference's own bucket key.</summary>
    public bool IsBucketCollapsed(string bucketKey) =>
        Data.CollapsedBuckets.TryGetValue(bucketKey, out var collapsed) && collapsed;

    public void SetBucketCollapsed(string bucketKey, bool collapsed)
    {
        if (IsBucketCollapsed(bucketKey) == collapsed)
        {
            return;
        }

        if (collapsed)
        {
            Data.CollapsedBuckets[bucketKey] = true;
        }
        else
        {
            Data.CollapsedBuckets.Remove(bucketKey);
        }

        Save();
    }

    /// <summary>Moves a custom group one place along the list — the reference's `moveCustomGroup`.</summary>
    public bool MoveGroup(string groupId, int delta)
    {
        var index = Data.Groups.FindIndex(g => g.Id == groupId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Data.Groups.Count)
        {
            return false;
        }

        var group = Data.Groups[index];
        Data.Groups.RemoveAt(index);
        Data.Groups.Insert(target, group);
        Save();
        return true;
    }

    /// <summary>
    /// The session's colour key, or null for the reference's "Default" row. Only the
    /// eight names the reference ships are stored, so a hand-edited file cannot paint
    /// a row with something the picker has no row for.
    /// </summary>
    public string? ColorOf(string sessionId) =>
        Data.SessionColors.TryGetValue(sessionId, out var key) && Services.SessionColors.IsKnown(key) ? key : null;

    public void SetColor(string sessionId, string? colorKey)
    {
        if (colorKey is null)
        {
            if (Data.SessionColors.Remove(sessionId))
            {
                Save();
            }

            return;
        }

        if (!Services.SessionColors.IsKnown(colorKey) ||
            (Data.SessionColors.TryGetValue(sessionId, out var current) && current == colorKey))
        {
            return;
        }

        Data.SessionColors[sessionId] = colorKey;
        Save();
    }

    /// <summary>
    /// The home view's per-session dismissal: a row stays hidden until the session is
    /// active again, which is the reference's "dismissedAt >= lastActivity" rule.
    /// </summary>
    public DateTimeOffset? DismissedAt(string sessionId) =>
        Data.DismissedAt.TryGetValue(sessionId, out var at) ? at : null;

    public void Dismiss(string sessionId, DateTimeOffset at)
    {
        Data.DismissedAt[sessionId] = at;
        Save();
    }

    /// <summary>Coordinator mode is per session and survives restarts, like the reference's.</summary>
    /// <summary>Records that a chip of <paramref name="fromSessionId"/> started this session.</summary>
    public void RecordSpawnedFrom(string spawnedSessionId, string fromSessionId, string taskId)
    {
        Data.SpawnedFrom[spawnedSessionId] = new SpawnedFrom
        {
            SessionId = fromSessionId,
            TaskId = taskId,
        };
        Save();
    }

    /// <summary>
    /// Whether a session was started from this one's chip for that task — the
    /// reference's <c>tY</c>, which asks the same question of its session store.
    /// </summary>
    public bool WasSpawned(string fromSessionId, string taskId) =>
        Data.SpawnedFrom.Values.Any(from =>
            string.Equals(from.SessionId, fromSessionId, StringComparison.Ordinal) &&
            string.Equals(from.TaskId, taskId, StringComparison.Ordinal));

    /// <summary>
    /// The session a chip of another session started this one from — the reference's
    /// <c>spawnedFrom.sessionId</c>, which its sidebar reads to file the row under
    /// its parent rather than beside it.
    /// </summary>
    public string? SpawnParentOf(string sessionId) =>
        Data.SpawnedFrom.TryGetValue(sessionId, out var from) && from.SessionId is { Length: > 0 } parent
            ? parent
            : null;

    public bool IsCoordinator(string sessionId) => Data.CoordinatorSessionIds.Contains(sessionId);

    public void SetCoordinator(string sessionId, bool coordinator)
        => SetMembership(Data.CoordinatorSessionIds, sessionId, coordinator);

    private void SetMembership(List<string> list, string sessionId, bool present)
    {
        bool changed;
        if (present)
        {
            changed = !list.Contains(sessionId);
            if (changed)
            {
                list.Add(sessionId);
            }
        }
        else
        {
            changed = list.Remove(sessionId);
        }

        if (changed)
        {
            Save();
        }
    }

    /// <summary>Drops references to sessions that no longer exist on disk.</summary>
    public void Prune(IEnumerable<string> existingSessionIds)
    {
        var existing = new HashSet<string>(existingSessionIds, StringComparer.Ordinal);
        var removed = 0;
        foreach (var group in Data.Groups)
        {
            removed += group.SessionIds.RemoveAll(id => !existing.Contains(id));
        }

        removed += Data.ArchivedSessionIds.RemoveAll(id => !existing.Contains(id));
        removed += Data.PinnedSessionIds.RemoveAll(id => !existing.Contains(id));
        removed += Data.UnreadSessionIds.RemoveAll(id => !existing.Contains(id));
        removed += Data.CoordinatorSessionIds.RemoveAll(id => !existing.Contains(id));
        foreach (var id in Data.SessionColors.Keys.Where(id => !existing.Contains(id)).ToList())
        {
            Data.SessionColors.Remove(id);
            removed++;
        }

        foreach (var id in Data.DismissedAt.Keys.Where(id => !existing.Contains(id)).ToList())
        {
            Data.DismissedAt.Remove(id);
            removed++;
        }

        foreach (var id in Data.SpawnedFrom.Keys.Where(id => !existing.Contains(id)).ToList())
        {
            Data.SpawnedFrom.Remove(id);
            removed++;
        }
        if (removed > 0)
        {
            Save();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Options));
        File.Move(tmp, _filePath, overwrite: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private SessionGroup? Find(string groupId) => Data.Groups.FirstOrDefault(g => g.Id == groupId);

    private static SessionGroupsData LoadFrom(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                return JsonSerializer.Deserialize<SessionGroupsData>(File.ReadAllText(filePath), Options)
                    ?? new SessionGroupsData();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt sidebar layout falls back to defaults; sessions themselves are safe.
        }

        return new SessionGroupsData();
    }
}
