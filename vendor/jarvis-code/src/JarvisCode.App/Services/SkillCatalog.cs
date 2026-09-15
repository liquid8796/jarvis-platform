using System.IO;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Hooks;

namespace JarvisCode.App.Services;

/// <summary>
/// Session-scoped skill state: the invoked-skill tracker (elision + the
/// post-compaction reminder), which listing entries the session has announced,
/// activated conditional (paths:) skills, hooks contributed by invoked skills,
/// and model/effort overrides an invoked skill declared.
/// </summary>
public sealed class SkillSessionState
{
    /// <summary>
    /// The # Unavailable MCP Tools entries already sent. The reference
    /// announces this as a delta too (its mcp_dropped_tools_delta), so a
    /// server that drops the same tool every refresh is named once.
    /// </summary>
    public HashSet<string> AnnouncedDroppedTools { get; } = new(StringComparer.Ordinal);

    public SkillInvocationTracker Tracker { get; } = new();

    /// <summary>Skill names already announced through a listing reminder; later turns announce only deltas.</summary>
    public HashSet<string> AnnouncedListingNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the harness system message has already carried the agent-type
    /// roster. The reference sends that roster once and leaves it in history
    /// (its second request replays the same system message rather than building
    /// a new one), so later announcements carry only newly discovered skills.
    /// </summary>
    public bool AnnouncedAgentTypes { get; set; }

    /// <summary>
    /// Whether the harness system message has already carried the
    /// <c># MCP Server Instructions</c> section. The reference sends a server's
    /// instructions when the connected set changes and its later deltas carry
    /// only what moved; the shell's servers do not come and go inside a session,
    /// so the first announcement is the only one.
    /// </summary>
    public bool AnnouncedMcpInstructions { get; set; }

    /// <summary>
    /// Configured MCP servers whose <c>instructions</c> this session has already
    /// sent. The reference retracts a server's instructions when it goes away,
    /// which needs the names rather than a flag.
    /// </summary>
    public HashSet<string> AnnouncedMcpInstructionServers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Deferred tool names the harness has already named to the model, and the
    /// MCP servers it has already said need authentication. The reference builds
    /// its <c>deferred_tools_delta</c> from what earlier attachments announced,
    /// so the first request lists every deferred tool and a later one only what
    /// appeared since.
    /// </summary>
    public HashSet<string> AnnouncedDeferredTools { get; } = new(StringComparer.Ordinal);

    /// <summary>MCP servers already named in the needs-authentication paragraph.</summary>
    public HashSet<string> AnnouncedNeedsAuthServers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The reminder texts this session has already resolved, per model. The
    /// reference latches each of its two <c>TBn</c> slots per conversation so a
    /// later turn re-reads nothing.
    /// </summary>
    public JarvisCode.Core.Agent.ToolResultReminders.TextLatch ReminderTexts { get; } = new();

    /// <summary>
    /// The delegation steer this session resolved, latched. The reference
    /// resolves its <c>hH</c> once and returns the latched value thereafter, so
    /// the steer cannot change under a running conversation.
    /// </summary>
    public GatedPromptSections.SteerLatch Steer { get; } = new();

    /// <summary>
    /// Deferred tools this session has already fetched with ToolSearch. The
    /// reference keeps them loaded through the conversation itself — its
    /// ToolSearch answers with <c>tool_reference</c> blocks the API expands, and
    /// that tool_result rides every later request — so a fetched tool never goes
    /// back to being deferred. This port writes the schemas itself, so the names
    /// live here and seed the registry each turn builds.
    /// </summary>
    public HashSet<string> LoadedDeferredTools { get; } = new(StringComparer.Ordinal);

    /// <summary>Conditional (paths:) skills a touched file has activated.</summary>
    public HashSet<string> ActivatedConditionalSkills { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Still-dormant conditional skills of the running turn, refreshed by the turn factory.</summary>
    public List<SkillDefinition> ConditionalCandidates { get; } = [];

    /// <summary>Hook definitions contributed by invoked skills' frontmatter.</summary>
    public List<HookDefinition> ExtraHooks { get; } = [];

    private readonly HashSet<string> _hookSkillsApplied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True the first time a given skill's hooks are folded in.</summary>
    public bool MarkHooksApplied(string skillName) => _hookSkillsApplied.Add(skillName);

    /// <summary>An invoked skill's model: frontmatter, applied to following turns ("inherit" keeps the session's).</summary>
    public string? ModelOverride { get; set; }

    /// <summary>An invoked skill's effort: frontmatter, applied to following turns.</summary>
    public string? EffortOverride { get; set; }

    /// <summary>Set when a compaction cleared skill content; the next message carries the invoked-skills reminder.</summary>
    public bool PendingInvokedSkillsReminder { get; set; }

    /// <summary>The text of the prompt that started the current turn (unlocks disable-model-invocation for typed /name).</summary>
    public string? CurrentTurnTypedText { get; set; }

    public bool UserTypedThisTurn(string skillName)
    {
        var text = CurrentTurnTypedText;
        if (string.IsNullOrEmpty(text))
            return false;
        return System.Text.RegularExpressions.Regex.IsMatch(
            text, @"(?<!\S)/" + System.Text.RegularExpressions.Regex.Escape(skillName) + @"(?=$|\s)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

/// <summary>
/// Loads the session's full command namespace the way the reference CLI does:
/// skills (user, Claude Code user, project, directory-scoped), legacy commands
/// (loaded as flat skills), and plugin skills — one list, first name wins —
/// plus the override / listing / composer filters over it.
/// </summary>
public static class SkillCatalog
{
    public const string CommandSource = "command";

    /// <summary>Override states (reference skillOverrides).</summary>
    public const string OverrideOff = "off";
    public const string OverrideUserInvocableOnly = "user-invocable-only";

    public static string ClaudeUserSkillsDirectory =>
        Environment.GetEnvironmentVariable("JARVIS_PARITY_ISOLATED") == "1"
            ? Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
                Path.Combine(Path.GetTempPath(), "jarvis-parity-no-user-" + Environment.ProcessId), "skills")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");

    private static readonly object CacheLock = new();
    private static (string Key, DateTimeOffset At, IReadOnlyList<SkillDefinition> Skills)? _cache;

    /// <summary>Drops the short-lived load cache (after creating/deleting/importing skills).</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cache = null;
        }
    }

    /// <summary>
    /// Everything invocable by name, before overrides. The directory-scoped
    /// scan walks the project tree, so results are cached briefly — the popup
    /// rebuilds per keystroke and turns per message.
    /// </summary>
    public static IReadOnlyList<SkillDefinition> LoadAll(string workingDirectory, ProfilePaths paths)
    {
        var key = workingDirectory + "|" + paths.Root;
        lock (CacheLock)
        {
            if (_cache is { } cached && cached.Key == key &&
                (DateTimeOffset.Now - cached.At) < TimeSpan.FromSeconds(3))
            {
                return cached.Skills;
            }
        }
        var loaded = LoadAllUncached(workingDirectory, paths);
        lock (CacheLock)
        {
            _cache = (key, DateTimeOffset.Now, loaded);
        }
        return loaded;
    }

    private static IReadOnlyList<SkillDefinition> LoadAllUncached(string workingDirectory, ProfilePaths paths)
    {
        var byName = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<SkillDefinition>();
        void AddAll(IEnumerable<SkillDefinition> skills)
        {
            foreach (var skill in skills)
            {
                if (byName.TryAdd(skill.Name, skill))
                    ordered.Add(skill);
            }
        }

        AddAll(Skills.LoadWithDirectoryScoped(
            workingDirectory, paths.UserSkillsDirectory, ClaudeUserSkillsDirectory));

        // Legacy commands share the namespace (the reference's
        // commands_DEPRECATED): flat markdown, full substitution, no base-dir
        // header. .claude/commands is read for Claude Code compatibility.
        AddAll(Skills.LoadDirectory(paths.UserCommandsDirectory, CommandSource));
        AddAll(Skills.LoadDirectory(
            Path.Combine(workingDirectory, CustomCommands.ProjectSubdirectory.Replace('/', Path.DirectorySeparatorChar)),
            CommandSource));
        AddAll(Skills.LoadDirectory(
            Path.Combine(workingDirectory, ".claude", "commands"), CommandSource));

        var plugins = PluginLibrary.LoadUser(paths);
        AddAll(plugins.Skills);
        AddAll(plugins.Commands.Select(command => new SkillDefinition(
            command.Name, command.Description, "You", command.Template,
            FilePath: "", DateTimeOffset.MinValue, Source: "plugin")
        {
            // A plugin command that declares a frontmatter description is listed
            // to the model like a skill (the reference lists vercel:deploy and
            // its siblings); an undeclared one — the plugin's own shared
            // include files — stays out of the listing.
            DescriptionDeclared = command.DescriptionDeclared,
        }));

        // Last, so a user, project or plugin skill of the same name shadows a
        // bundled one — the reference's own find-first order. The shipped
        // third-party packs sit in that same tail, ahead of the embedded skills
        // only because their names are pack-qualified and so cannot collide.
        AddAll(SkillPacks.All);
        AddAll(BundledSkills.All);

        return ordered;
    }

    /// <summary>Command-format entries default their description; skills declare theirs.</summary>
    private static bool IsPluginSource(SkillDefinition skill) =>
        skill.Source.StartsWith("plugin", StringComparison.OrdinalIgnoreCase);

    public static string OverrideFor(UiSettings settings, string name) =>
        settings.SkillOverrides.TryGetValue(name, out var value) ? value : "on";

    /// <summary>Skills the session can invoke at all (override "off" removes them everywhere).</summary>
    public static IReadOnlyList<SkillDefinition> Enabled(
        IReadOnlyList<SkillDefinition> all, UiSettings settings) =>
        [.. all.Where(s => !string.Equals(OverrideFor(settings, s.Name), OverrideOff, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// The listing filter (reference Tce): model-invocable, not overridden to
    /// user-invocable-only, not a dormant conditional skill, and plugin entries
    /// only when they declare a description or when-to-use.
    /// </summary>
    public static IReadOnlyList<SkillDefinition> ForListing(
        IReadOnlyList<SkillDefinition> enabled, UiSettings settings, SkillSessionState? state) =>
        [.. enabled.Where(s =>
            !s.DisableModelInvocation &&
            !string.Equals(OverrideFor(settings, s.Name), OverrideUserInvocableOnly, StringComparison.OrdinalIgnoreCase) &&
            (s.Paths.Count == 0 || state?.ActivatedConditionalSkills.Contains(s.Name) == true) &&
            (!IsPluginSource(s) || s.DescriptionDeclared || !string.IsNullOrEmpty(s.WhenToUse)))];

    /// <summary>The "/" menu filter: user-invocable skills only.</summary>
    public static IReadOnlyList<SkillDefinition> ForComposer(
        IReadOnlyList<SkillDefinition> enabled) =>
        [.. enabled.Where(s => s.UserInvocable)];

    /// <summary>The recency-weighted usage score function over the persisted usage map.</summary>
    public static Func<string, double> UsageScores(UiSettings settings) => name =>
        settings.SkillUsage.TryGetValue(name, out var entry)
            ? SkillInvocation.UsageScore(entry.UsageCount, entry.LastUsedAt)
            : 0;

    private static readonly Dictionary<string, DateTimeOffset> UsageWriteThrottle = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bumps a skill's lifetime usage (reference Fdt): at most one persisted
    /// write per skill per minute.
    /// </summary>
    public static void RecordUsage(UiSettingsStore store, string name)
    {
        var now = DateTimeOffset.Now;
        lock (UsageWriteThrottle)
        {
            if (UsageWriteThrottle.TryGetValue(name, out var last) && (now - last) < TimeSpan.FromSeconds(60))
                return;
            UsageWriteThrottle[name] = now;
        }
        var entry = store.Current.SkillUsage.TryGetValue(name, out var existing) ? existing : new SkillUsageEntry();
        entry.UsageCount += 1;
        entry.LastUsedAt = now;
        store.Current.SkillUsage[name] = entry;
        try
        {
            store.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Usage stats must never break an invocation.
        }
    }

    /// <summary>
    /// A skill was dispatched, with the working directory it ran in. Plugin monitors
    /// whose trigger is <c>on-skill-invoke:{name}</c> arm on this.
    /// </summary>
    public static event Action<string, string>? SkillInvoked;

    /// <summary>
    /// What an invoked skill applies to its session (shared by the Skill tool
    /// and the typed /name path): lifetime usage, allowed/disallowed-tools as
    /// session permission grants, frontmatter hooks, and model/effort
    /// overrides for the following turns.
    /// </summary>
    public static void ApplyInvocationEffects(
        UiSettingsStore uiSettings, UiPermissionGate gate, SkillSessionState state,
        SkillDefinition skill, string workingDirectory)
    {
        RecordUsage(uiSettings, skill.Name);
        SkillInvoked?.Invoke(skill.Name, workingDirectory);
        if (skill.AllowedTools.Count > 0)
        {
            gate.AddSessionRuleLines(SkillPermissionRules.ToRuleLines(skill.AllowedTools, "allow", skill, workingDirectory));
        }
        if (skill.DisallowedTools.Count > 0)
        {
            gate.AddSessionRuleLines(SkillPermissionRules.ToRuleLines(skill.DisallowedTools, "deny", skill, workingDirectory));
        }
        if (skill.HooksBlock is not null && state.MarkHooksApplied(skill.Name))
        {
            try
            {
                state.ExtraHooks.AddRange(SkillHooks.Parse(skill.Name, skill.HooksBlock));
            }
            catch (FormatException ex)
            {
                JarvisCode.Core.Utilities.DiagnosticLog.Write($"skills: {ex.Message}");
            }
        }
        if (skill.Model is { Length: > 0 } skillModel &&
            !skillModel.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            state.ModelOverride = skillModel;
        }
        if (skill.Effort is { Length: > 0 } skillEffort)
        {
            state.EffortOverride = skillEffort;
        }
    }

    /// <summary>
    /// Evaluates dormant conditional skills against a touched file, activating
    /// matches (reference Vpt fires on reads and edits). Returns the names
    /// activated by this path.
    /// </summary>
    public static IReadOnlyList<string> ActivateConditional(
        IReadOnlyList<SkillDefinition> enabled, SkillSessionState state, string workingDirectory, string filePath)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(workingDirectory, Path.GetFullPath(filePath, workingDirectory));
        }
        catch (ArgumentException)
        {
            return [];
        }
        if (relative.StartsWith("..", StringComparison.Ordinal))
            return [];

        var activated = new List<string>();
        foreach (var skill in enabled)
        {
            if (skill.Paths.Count == 0 || state.ActivatedConditionalSkills.Contains(skill.Name))
                continue;
            if (SkillInvocation.MatchesPaths(skill.Paths, relative))
            {
                state.ActivatedConditionalSkills.Add(skill.Name);
                activated.Add(skill.Name);
            }
        }
        return activated;
    }
}
