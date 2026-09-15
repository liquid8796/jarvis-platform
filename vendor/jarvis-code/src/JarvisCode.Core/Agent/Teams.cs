using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JarvisCode.Core.Agent;

/// <summary>How a spawned teammate executes. Only in-process runs here.</summary>
public enum TeammateMode
{
    /// <summary>The teammate runs as a worker inside this process.</summary>
    InProcess,

    /// <summary>A tmux pane per teammate (POSIX; needs tmux on PATH).</summary>
    Tmux,

    /// <summary>An iTerm2 split pane per teammate (macOS only).</summary>
    ITerm2,

    /// <summary>Pick the best backend the machine actually supports.</summary>
    Auto,
}

/// <summary>One member of the session's agent team, as stored in the team file.</summary>
public sealed record TeamMember
{
    public required string AgentId { get; init; }

    public required string Name { get; init; }

    public DateTimeOffset JoinedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Backend that runs this member ("in-process", "tmux", …).</summary>
    public string Mode { get; init; } = "in-process";

    /// <summary>False while the member is idle — it has reported and is waiting.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>Set when the member works in its own git worktree.</summary>
    public string? WorktreePath { get; init; }

    /// <summary>The worktree's branch, so ending the team can delete it too.</summary>
    public string? WorktreeBranch { get; init; }

    /// <summary>
    /// The commit the worktree started on. Without it a cleanup cannot tell
    /// work from an untouched checkout, so it keeps the worktree.
    /// </summary>
    public string? WorktreeBaseCommit { get; init; }

    /// <summary>
    /// True while this teammate is waiting for the lead to answer a plan it
    /// raised — the reference's awaitingLeaderApproval.
    /// </summary>
    public bool AwaitingLeaderApproval { get; init; }
}

/// <summary>Contents of a team's <c>config.json</c>.</summary>
public sealed record TeamFile
{
    public string Name { get; init; } = "";

    public IReadOnlyList<TeamMember> Members { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// What a running agent knows about its team: who it is, where the team file
/// lives, and whether it is the lead. Rides <see cref="Tools.ToolExecutionContext"/>
/// so the task and messaging tools can address teammates by name.
/// </summary>
public sealed record TeamContext(
    string TeamName,
    string AgentName,
    string AgentId,
    bool IsLeader,
    string ConfigPath);

/// <summary>
/// Naming rules for team members, ported from the reference: the same regex,
/// the same canonicalization, the same reserved recipients, and the same
/// refusal wording (with this harness's tool name).
/// </summary>
public static partial class TeamNames
{
    /// <summary>The lead's name; teammates report to it.</summary>
    public const string Leader = "team-lead";

    /// <summary>Routes a message to the main conversation instead of an agent.</summary>
    public const string Main = "main";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^a(?:[\w-]{1,63}-)?[0-9a-f]{16}$")]
    private static partial Regex AgentIdPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// The reference's name key: NFKC, control/format characters dropped,
    /// trimmed, lowercased, runs of whitespace folded to a hyphen. Two names
    /// that canonicalize the same address the same agent.
    /// </summary>
    public static string Canonical(string name)
    {
        var normalized = name.Normalize(NormalizationForm.FormKC);
        var stripped = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            bool formatting = category is UnicodeCategory.Control or UnicodeCategory.Format;
            if (formatting && !char.IsWhiteSpace(character))
                continue;
            stripped.Append(character);
        }

        var trimmed = stripped.ToString().Trim().ToLowerInvariant();
        return Whitespace().Replace(trimmed, "-");
    }

    public static bool IsAgentId(string value) => AgentIdPattern().IsMatch(value);

    /// <summary>A recipient that already addresses something directly.</summary>
    public static bool IsReserved(string name)
    {
        var key = Canonical(name);
        return key == Main || key == Leader || IsAgentId(key);
    }

    /// <summary>Null when the name may be used; otherwise the reference's refusal.</summary>
    public static string? Validate(string name)
    {
        if (!NamePattern().IsMatch(name))
        {
            return "name must start with a letter or digit and contain only letters, digits, " +
                "underscores, or hyphens (max 64 chars)";
        }

        if (Canonical(name) == Main)
            return $"\"{Main}\" is reserved — SendMessage routes it to the main conversation";

        if (IsReserved(name))
        {
            return "name must not be a reserved recipient (\"main\" or \"team-lead\", in any spelling) " +
                "or have the shape of an agent id — those already address an agent directly";
        }

        return null;
    }

    /// <summary>Directory-safe form of a team name (the reference's slug).</summary>
    public static string Slug(string teamName)
    {
        var slug = new StringBuilder(teamName.Length);
        foreach (var character in teamName)
            slug.Append(char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        return slug.ToString();
    }

    /// <summary>
    /// An agent id in the reference's shape: <c>a</c>, an optional name label,
    /// then 16 hex characters.
    /// </summary>
    public static string NewAgentId(string? label = null)
    {
        var hex = Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 8)).ToLowerInvariant();
        if (string.IsNullOrEmpty(label))
            return $"a{hex}";
        var cleaned = new string(label.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (cleaned.Length == 0)
            return $"a{hex}";
        if (cleaned.Length > 63)
            cleaned = cleaned[..63];
        return $"a{cleaned}-{hex}";
    }
}

/// <summary>
/// The team file store. The reference locks the file because its teammates are
/// separate processes; ours run inside this process, so writes are serialized
/// on a monitor and written whole (temp file + replace) so a crash cannot leave
/// half a document behind.
/// </summary>
public sealed class TeamStore
{
    private readonly object _gate = new();
    private readonly string _root;

    public TeamStore(string root) => _root = root;

    public string DirectoryFor(string teamName) => Path.Combine(_root, TeamNames.Slug(teamName));

    public string ConfigPathFor(string teamName) => Path.Combine(DirectoryFor(teamName), "config.json");

    /// <summary>The team's shared memory directory, which teammates write to directly.</summary>
    public string MemoryDirectoryFor(string teamName) => Path.Combine(DirectoryFor(teamName), "memory");

    /// <summary>
    /// Creates the shared memory directory and returns it, so the prompt can
    /// promise it already exists.
    /// </summary>
    public string? EnsureMemoryDirectory(string teamName)
    {
        var directory = MemoryDirectoryFor(teamName);
        try
        {
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public TeamFile? Read(string teamName)
    {
        lock (_gate)
            return ReadLocked(teamName);
    }

    private TeamFile? ReadLocked(string teamName)
    {
        var path = ConfigPathFor(teamName);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<TeamFile>(File.ReadAllText(path), SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Creates the team file if it is missing and returns it.</summary>
    public TeamFile EnsureTeam(string teamName)
    {
        lock (_gate)
        {
            var existing = ReadLocked(teamName);
            if (existing is not null)
                return existing;
            var created = new TeamFile { Name = teamName, Members = [] };
            WriteLocked(teamName, created);
            return created;
        }
    }

    /// <summary>Applies <paramref name="mutate"/> to the team file under the store's lock.</summary>
    public TeamFile Update(string teamName, Func<TeamFile, TeamFile> mutate)
    {
        lock (_gate)
        {
            var current = ReadLocked(teamName) ?? new TeamFile { Name = teamName, Members = [] };
            var updated = mutate(current);
            WriteLocked(teamName, updated);
            return updated;
        }
    }

    public TeamMember AddMember(string teamName, TeamMember member) =>
        Update(teamName, file => file with
        {
            Members = file.Members
                .Where(m => m.AgentId != member.AgentId &&
                            !string.Equals(TeamNames.Canonical(m.Name), TeamNames.Canonical(member.Name), StringComparison.Ordinal))
                .Append(member)
                .ToList(),
        }).Members.First(m => m.AgentId == member.AgentId);

    public bool RemoveMember(string teamName, string agentId)
    {
        bool removed = false;
        Update(teamName, file =>
        {
            var kept = file.Members.Where(m => m.AgentId != agentId).ToList();
            removed = kept.Count != file.Members.Count;
            return file with { Members = kept };
        });
        return removed;
    }

    /// <summary>Marks a member active (working) or idle (waiting), as the reference does.</summary>
    public void SetActive(string teamName, string name, bool isActive) =>
        Update(teamName, file => file with
        {
            Members = file.Members
                .Select(m => string.Equals(TeamNames.Canonical(m.Name), TeamNames.Canonical(name), StringComparison.Ordinal)
                    ? m with { IsActive = isActive }
                    : m)
                .ToList(),
        });

    /// <summary>Records whether a member is waiting on the lead's plan answer.</summary>
    public void SetAwaitingApproval(string teamName, string name, bool awaiting) =>
        Update(teamName, file => file with
        {
            Members = file.Members
                .Select(m => string.Equals(TeamNames.Canonical(m.Name), TeamNames.Canonical(name), StringComparison.Ordinal)
                    ? m with { AwaitingLeaderApproval = awaiting }
                    : m)
                .ToList(),
        });

    public TeamMember? FindByName(string teamName, string name)
    {
        var key = TeamNames.Canonical(name);
        return Read(teamName)?.Members
            .FirstOrDefault(m => string.Equals(TeamNames.Canonical(m.Name), key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Ends a session's team: each member's worktree is removed when it still
    /// holds nothing (one with work is kept, exactly as an agent's own cleanup
    /// keeps it), then the team directory goes. Returns what it removed.
    /// </summary>
    public (int WorktreesRemoved, int WorktreesKept) CleanupSession(string teamName)
    {
        var removed = 0;
        var kept = 0;
        foreach (var member in Read(teamName)?.Members ?? [])
        {
            switch (ReleaseWorktree(member))
            {
                case true:
                    removed++;
                    break;
                case false:
                    kept++;
                    break;
            }
        }

        DeleteTeam(teamName);
        return (removed, kept);
    }

    /// <summary>
    /// Gives up one member's worktree: removed when it still holds nothing,
    /// kept when it carries work, exactly as an agent's own cleanup decides.
    /// Null when the member never had one.
    /// </summary>
    private static bool? ReleaseWorktree(TeamMember member)
    {
        if (member.WorktreePath is not { Length: > 0 } path || !Directory.Exists(path))
            return null;
        // A member recorded before the base commit was stored cannot be judged,
        // and an unjudgeable worktree is kept rather than discarded.
        if (member.WorktreeBaseCommit is not { Length: > 0 } baseCommit)
            return false;
        if (AgentWorktrees.MainRepoRoot(path) is not { Length: > 0 } repoRoot)
            return false;
        var worktree = new AgentWorktrees.Worktree(
            path, member.WorktreeBranch ?? "", repoRoot, baseCommit);
        return AgentWorktrees.RemoveIfUnchanged(worktree);
    }

    /// <summary>
    /// Removes team directories whose session is gone. The reference sweeps its
    /// session teams the same way, because a process that died mid-run leaves
    /// its team behind.
    /// </summary>
    /// <summary>How recent a team file has to be for the sweep to leave it alone.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(1);

    public int RemoveOrphans(IEnumerable<string> liveSessionIds)
    {
        var live = liveSessionIds.Select(TeamNames.Slug).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        try
        {
            if (!Directory.Exists(_root))
                return 0;
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                var slug = Path.GetFileName(directory);
                if (live.Contains(slug))
                    continue;
                // Only a directory that really is a team is swept.
                var config = Path.Combine(directory, "config.json");
                if (!File.Exists(config))
                    continue;
                // A team written moments ago belongs to a session another
                // process has not saved yet, so it is left alone.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(config) < GracePeriod)
                    continue;
                var teamFile = ReadBySlug(slug);
                if (teamFile is not null)
                {
                    // A team that recorded worktrees keeps the ones holding
                    // work, so a leftover checkout is never discarded with it.
                    foreach (var member in teamFile.Members)
                        ReleaseWorktree(member);
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A directory in use is left for the next sweep.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return removed;
        }

        return removed;
    }

    private TeamFile? ReadBySlug(string slug)
    {
        try
        {
            var path = Path.Combine(_root, slug, "config.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<TeamFile>(File.ReadAllText(path), SerializerOptions)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes the team directory; used when the session ends.</summary>
    public void DeleteTeam(string teamName)
    {
        lock (_gate)
        {
            try
            {
                var directory = DirectoryFor(teamName);
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover team directory is harmless; it is re-created by name next time.
            }
        }
    }

    private void WriteLocked(string teamName, TeamFile file)
    {
        try
        {
            var directory = DirectoryFor(teamName);
            Directory.CreateDirectory(directory);
            var path = ConfigPathFor(teamName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory team stays authoritative for this session.
        }
    }
}

/// <summary>The reminders a teammate carries, ported verbatim from the reference.</summary>
public static class TeamPrompts
{
    /// <summary>
    /// The memory block when a session has both stores. The reference names the
    /// private directory and the shared one in a single sentence, then insists
    /// the model writes into them directly rather than creating them.
    /// </summary>
    public static string MemoryDirectories(string autoDirectory, string teamDirectory) =>
        $"Your memories live at `{autoDirectory}` (private to this user) and `{teamDirectory}` " +
        "(shared with all users of this project). These directories already exist — write to them " +
        "directly with the Write tool (do not run mkdir or check for their existence). " +
        "Never write secrets or credentials to team memory.";

    /// <summary>The same block for a teammate with no private directory of its own.</summary>
    public static string TeamMemoryOnly(string teamDirectory) =>
        $"Write only to `{teamDirectory}` — it already exists; write to it directly with the Write tool " +
        "(do not run mkdir or check for its existence). There is no separate private memory directory in " +
        "this session — save every memory type to the team directory, bearing in mind it is shared with " +
        "teammates. Never write secrets or credentials to team memory.";

    /// <summary>Said instead when the team directory cannot be written this session.</summary>
    public const string TeamMemoryReadOnly =
        "Team memory is read-only this session — you cannot persist new memories.";

    /// <summary>
    /// The Team Coordination system-reminder a spawned teammate receives.
    /// <paramref name="taskListPath"/> is omitted when the session has no board.
    /// </summary>
    public static string Coordination(string agentName, string teamConfigPath, string? taskListPath)
    {
        var taskLine = string.IsNullOrEmpty(taskListPath) ? "" : $"\n- Task list: {taskListPath}";
        var taskAdvice = string.IsNullOrEmpty(taskListPath)
            ? ""
            : " Check the task list periodically. Create new tasks when work should be divided. " +
              "Mark tasks resolved when complete.";
        return $$"""
            <system-reminder>
            # Team Coordination

            You are a teammate in this session's agent team.

            **Your Identity:**
            - Name: {{agentName}}

            **Team Resources:**
            - Team config: {{teamConfigPath}}{{taskLine}}

            **Team Leader:** The team lead's name is "team-lead". Send updates and completion notifications to them.

            Read the team config to discover your teammates' names.{{taskAdvice}}

            **IMPORTANT:** Always refer to active teammates by their NAME (e.g., "team-lead", "analyzer", "researcher"). Use an `agentId` (format `a...-...`, from the spawn result) only to resume a background agent that has already completed. When messaging, use the name directly:

            ```json
            {
              "to": "team-lead",
              "message": "Your message here",
              "summary": "Brief 5-10 word preview"
            }
            ```
            </system-reminder>
            """;
    }

    /// <summary>
    /// The reference's nudge when the task tools have gone unused for a while.
    /// </summary>
    public const string TaskToolsReminder =
        "The task tools haven't been used recently. If you're working on tasks that would benefit from " +
        "tracking progress, consider using TaskCreate to add new tasks and TaskUpdate to update task " +
        "status (set to in_progress when starting, completed when done). Also consider cleaning up the " +
        "task list if it has become stale. Only use these if relevant to the current work. This is just " +
        "a gentle reminder - ignore if not applicable.";
}

/// <summary>
/// Which teammate backends this machine can actually run. The reference refuses
/// tmux on Windows with these words, and tmux is what its "agent swarms" need.
/// </summary>
public static class TeammateBackends
{
    public const string TmuxUnavailableWindows =
        "tmux is not natively available on Windows. Consider using WSL or Cygwin.";

    public const string SwarmNeedsTmux =
        "To use agent swarms, you need tmux which requires WSL (Windows Subsystem for Linux).";

    public const string ITerm2NeedsMac =
        "iTerm2 panes are only available on macOS.";

    /// <summary>True when tmux can be launched from this process.</summary>
    public static bool TmuxAvailable { get; } = LocateTmux() is not null;

    private static string? LocateTmux()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
            return null;
        var executables = OperatingSystem.IsWindows() ? new[] { "tmux.exe" } : new[] { "tmux" };
        foreach (var directory in pathVariable.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            foreach (var executable in executables)
            {
                try
                {
                    var candidate = Path.Combine(directory, executable);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is skipped rather than fatal.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the configured mode against the machine. Auto lands on the best
    /// available backend; an explicitly requested one that cannot run returns
    /// its reference refusal in <paramref name="refusal"/>.
    /// </summary>
    public static TeammateMode Resolve(TeammateMode requested, out string? refusal)
    {
        refusal = null;
        switch (requested)
        {
            case TeammateMode.Tmux when !TmuxAvailable:
                refusal = OperatingSystem.IsWindows() ? TmuxUnavailableWindows : SwarmNeedsTmux;
                return TeammateMode.InProcess;
            case TeammateMode.ITerm2 when !OperatingSystem.IsMacOS():
                refusal = ITerm2NeedsMac;
                return TeammateMode.InProcess;
            case TeammateMode.Auto:
                return TeammateMode.InProcess;
            default:
                return requested;
        }
    }

    public static string Name(TeammateMode mode) => mode switch
    {
        TeammateMode.Tmux => "tmux",
        TeammateMode.ITerm2 => "iterm2",
        TeammateMode.Auto => "auto",
        _ => "in-process",
    };

    public static TeammateMode Parse(string? value) => value switch
    {
        "tmux" => TeammateMode.Tmux,
        "iterm2" => TeammateMode.ITerm2,
        "auto" => TeammateMode.Auto,
        _ => TeammateMode.InProcess,
    };
}
