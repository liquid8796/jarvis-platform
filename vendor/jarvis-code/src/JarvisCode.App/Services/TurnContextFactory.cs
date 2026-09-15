using System.Net.Http;
using System.IO;
using JarvisCode.App.Composition;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Checkpoints;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Hooks;
using JarvisCode.Core.Memory;
using JarvisCode.Core.Models;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Sessions;
using JarvisCode.Core.Settings;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.App.Services;

/// <param name="Roster">
/// What the harness roster needs to know about this turn's registry and model;
/// null on the Chat surface, which sends no roster.
/// </param>
public sealed record TurnSetup(
    AgentTurnContext Context,
    ModelInfo Model,
    FileCheckpointStore.TurnCheckpoint? Checkpoint,
    HookRunner? TurnHooks,
    HarnessSystemMessage.RosterContext? Roster = null,
    /// <summary>
    /// The turn's deferred-tool registry when tool search engaged, so the
    /// harness can name what it hid; null when every tool is advertised.
    /// </summary>
    DeferredToolRegistry? Deferred = null);

/// <summary>
/// Assembles an AgentTurnContext for one turn. The Chat surface is a pure
/// conversation — its only tools are the desktop-control pair, and only when the
/// user opts in; the Code surface gets the full agentic tool set, checkpoints,
/// skills, plugins, hooks, project memory, and subagents.
/// </summary>
public sealed class TurnContextFactory(AppServices services) : IDisposable
{
    private readonly Dictionary<string, JarvisCode.Core.LanguageServers.PluginLanguageServerManager> _pluginLanguageServers = new(StringComparer.OrdinalIgnoreCase);

    private JarvisCode.Core.LanguageServers.PluginLanguageServerManager PluginLanguageServers(string cwd, IReadOnlyList<string> files)
    {
        var key = cwd + "\n" + string.Join("\n", files);
        lock (_pluginLanguageServers)
        {
            if (!_pluginLanguageServers.TryGetValue(key, out var manager))
                _pluginLanguageServers[key] = manager = new(files, cwd);
            return manager;
        }
    }

    public void Dispose()
    {
        lock (_pluginLanguageServers)
        {
            foreach (var manager in _pluginLanguageServers.Values) manager.Dispose();
            _pluginLanguageServers.Clear();
        }
    }

    /// <summary>
    /// The reference's deferral predicate, <c>AO(e)</c>, for the part of it this
    /// harness can reach: <c>alwaysLoad</c> is read first and exempts the tool,
    /// and every other MCP tool defers — the shell's own servers included, which
    /// is where this port used to diverge by exempting all ten of them.
    /// </summary>
    internal static bool Deferrable(ITool tool) =>
        ToolDeferral.ShouldDefer(
            tool,
            isMcp: IsMcpTool(tool),
            alwaysLoad: InternalMcpServers.IsAlwaysLoad(tool)
                || tool is JarvisCode.Core.Mcp.McpToolAdapter { AlwaysLoad: true },
            sessionKind: Environment.GetEnvironmentVariable(ToolDeferral.SessionKindVariable));

    /// <summary>An MCP tool: composed by one of the shell's servers, or by a configured one.</summary>
    private static bool IsMcpTool(ITool tool) =>
        tool.Name.StartsWith(InternalMcpServers.Prefix, StringComparison.Ordinal)
        || tool is JarvisCode.Core.Mcp.McpToolAdapter;

    /// <summary>
    /// Whether tool search engages for this turn. This is the reference's own
    /// answer (<see cref="ToolSearchAvailability"/>, its <c>eJe</c>/<c>sW</c>/
    /// <c>y_</c>): on for every model but the two haiku-3 spellings, unless
    /// <c>ENABLE_TOOL_SEARCH</c> turns it off. There is no size threshold there
    /// and there is none here — a live desktop session on opus-5 lists its
    /// deferred tools from the first message, whatever is configured.
    ///
    /// The tool-set guard is the only local addition: a registry that would
    /// defer nothing advertises ToolSearch for no reason, and it is the same
    /// guard <see cref="ChatRegistry"/> already applies.
    /// </summary>
    internal static bool WillDefer(IReadOnlyList<ITool> tools, string? modelId) =>
        ToolSearchAvailability.IsEnabled(modelId) && tools.Any(Deferrable);

    /// <summary>
    /// gitStatus per session, computed once. It says "at the start of the
    /// conversation" and rides the cached system prompt, so recomputing it each
    /// turn would both make that sentence false and rewrite the cached prefix
    /// every time the working tree moved — five git subprocesses per turn to
    /// throw the prompt cache away.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?>
        GitStatusBySession = new(StringComparer.Ordinal);

    internal static string? GitStatusForSession(string sessionId, string workingDirectory) =>
        GitStatusBySession.GetOrAdd(sessionId, _ => SystemReminders.BuildGitStatus(workingDirectory));

    /// <summary>The reference's environment override for the auto-compact window.</summary>
    internal const string AutoCompactWindowVariable = "CLAUDE_CODE_AUTO_COMPACT_WINDOW";

    /// <summary>Its test-only percentage dial, which outranks the 13k reserve when set.</summary>
    internal const string AutoCompactPercentVariable = "CLAUDE_AUTOCOMPACT_PCT_OVERRIDE";

    /// <summary>
    /// The compaction breakers count across a session's turns — how long ago the
    /// last compact was, how many attempts failed — so the state has to outlive
    /// the context each turn builds, exactly as the git status above does.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CompactionState>
        CompactionStateBySession = new(StringComparer.Ordinal);

    internal static CompactionState CompactionStateForSession(string sessionId) =>
        CompactionStateBySession.GetOrAdd(sessionId, static _ => new CompactionState());

    /// <summary>
    /// The background summary is written once per session and carried between
    /// turns, so its store outlives the context each turn builds.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PrecomputeStore>
        PrecomputeBySession = new(StringComparer.Ordinal);

    internal static PrecomputeStore PrecomputeStoreForSession(string sessionId) =>
        PrecomputeBySession.GetOrAdd(sessionId, static _ => new PrecomputeStore());

    /// <summary>
    /// The build that wrote a stored summary. The reference records this beside
    /// the payload and reports whether it matches; it does not refuse on it, and
    /// neither does this.
    /// </summary>
    internal static string ClientVersion { get; } =
        System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                typeof(TurnContextFactory).Assembly)
            ?.InformationalVersion ?? "unknown";

    /// <summary>
    /// A session is gone: stop the summary it may still be writing and drop the
    /// bookkeeping kept for it, so a closed session cannot spend a request.
    /// </summary>
    internal static void ForgetSession(string sessionId)
    {
        if (PrecomputeBySession.TryRemove(sessionId, out var precompute))
            precompute.Cancel();
        CompactionStateBySession.TryRemove(sessionId, out _);
        GitStatusBySession.TryRemove(sessionId, out _);
    }

    private static double PercentOverrideFromEnvironment() =>
        double.TryParse(
            Environment.GetEnvironmentVariable(AutoCompactPercentVariable),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double percent)
            ? percent
            : 0;

    private const string ChatPrompt =
        "You are Jarvis, a helpful, knowledgeable assistant in a desktop chat app. " +
        "Answer in the user's language. Format answers as markdown when it helps. " +
        "Be direct and warm; keep simple answers short.";

    private const string DesktopControlPrompt =
        " You can also see and drive this Windows desktop: take a screenshot to look, use the " +
        "computer tool to click, type or scroll, then screenshot again to check the result. " +
        "You have no file, shell or git tools here — for work on the user's project, say that a " +
        "Code session is the place for it.";

    public ModelInfo? ResolveModel(Session session)
    {
        var models = services.Settings.Models;
        return ModelCatalog.Find(models, session.ModelId ?? services.Settings.Current.DefaultModelId)
            ?? models.FirstOrDefault();
    }

    /// <param name="tools">
    /// Chat carries no agentic tools; what it may carry is what the conversation's
    /// own switches asked for — desktop control (opted into in Settings) and the
    /// connectors the user turned on in the composer's tools menu.
    /// </param>
    public TurnSetup? CreateForChat(
        Session session,
        UiPermissionGate gate,
        IReadOnlyList<ITool>? tools = null,
        SkillSessionState? skillState = null)
    {
        if (ResolveModel(session) is not { } model)
        {
            return null;
        }

        var provider = services.Providers.Get(model.ProviderId);
        var settings = services.Settings.Current;
        var chatTools = tools ?? [];
        // The reference keeps web search, extended thinking and tool access on the
        // conversation, not on the account: one chat may search the web while the
        // next does not, and the composer's toggles write here.
        var conversation = services.ChatConversations.Get(session.Id, settings.EnableWebSearch);
        var desktopControl = chatTools.Any(static t => t.Name is "screenshot" or "computer_batch");
        // A chat filed under a project carries the project's own block, which is
        // the reference's <project_instructions> (and <project_links>) verbatim.
        var project = ProjectStore.Block(services.Projects.ForSession(session.Id));

        var context = new AgentTurnContext
        {
            Provider = provider,
            ModelId = model.ModelId,
            SystemPrompt = (desktopControl ? ChatPrompt + DesktopControlPrompt : ChatPrompt)
                + (string.IsNullOrEmpty(project) ? "" : "\n\n" + project),
            Messages = session.Messages,
            Tools = ChatRegistry(chatTools, conversation.ToolAccess, skillState),
            PermissionGate = gate,
            ToolContext = new ToolExecutionContext
            {
                SessionId = session.Id,
                WorkingDirectory = string.IsNullOrEmpty(session.WorkingDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : session.WorkingDirectory,
            },
            EnableWebSearch = conversation.WebSearch && ProviderCapabilities.For(provider).SupportsWebSearchControl,
            // The Chat surface has no effort ladder; its one thinking control is the
            // reference's Extended thinking toggle, and switched off it sends the
            // body this engine sends for no thinking at all.
            ThinkingEffort = conversation.ExtendedThinking && ProviderCapabilities.For(provider).SupportsThinkingEffort
                ? EffortLevels.ResolveEffort(settings.ThinkingEffortName)
                : JarvisCode.Core.Providers.ThinkingEffort.Off,
            ProviderOptions = CopyProviderOptions(settings, model.ModelId),
        };

        return new TurnSetup(
            WithClientAttribution(context, session), model, Checkpoint: null, TurnHooks: null);
    }

    /// <summary>
    /// Chat's registry. "Load tools when needed" is the reference's default and is
    /// this engine's deferred registry; "Tools already loaded" advertises the lot.
    /// A conversation with nothing but the desktop-control pair defers nothing
    /// either way, since neither is an MCP tool.
    /// </summary>
    private static IToolRegistry ChatRegistry(
        IReadOnlyList<ITool> tools, ChatToolAccess access, SkillSessionState? skillState) =>
        access == ChatToolAccess.LoadWhenNeeded && tools.Any(Deferrable)
            ? new DeferredToolRegistry([.. tools], Deferrable, skillState?.LoadedDeferredTools)
            : new ToolRegistry(tools);

    /// <summary>
    /// The turn's token target for workflow budgets, or null when none was set.
    /// The reference takes it from its --task-budget flag; the CLI sets this and
    /// the desktop app leaves it unset, which is the reference's "no target".
    /// </summary>
    public static JarvisCode.Core.Agent.WorkflowBudget? WorkflowTurnBudget { get; set; }

    /// <summary>
    /// Set means set: the reference tests these variables for raw truthiness, so
    /// any non-empty value counts — including "0".
    /// </summary>
    /// <summary>
    /// The reminder options for this turn, with both texts run through the
    /// session's latch so a conversation keeps what it first resolved for this
    /// model. A session with no state of its own resolves fresh each turn,
    /// which is what a one-shot caller wants.
    /// </summary>
    private static JarvisCode.Core.Agent.ToolResultReminders.Options ReminderOptions(
        string modelId, bool modelOwnsText, string sessionId, SkillSessionState? skillState,
        AppSettings settings)
    {
        var options = JarvisCode.Core.Agent.ToolResultReminders.Options.FromEnvironment(
            systemTurnModel: HarnessTurnComposer.UsesSystemTurn(modelId),
            modelOwnsText: modelOwnsText);
        // Settings decide which tiers may answer before the latch closes over
        // the result, so the choice cannot change under a running conversation
        // any more than the environment can.
        options = ReminderOverrides.Apply(options, settings, modelId);
        return skillState is null ? options : options.Latched(skillState.ReminderTexts, sessionId, modelId);
    }

    private static bool HasEnvironmentFlag(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 };

    public TurnSetup? CreateForCode(
        Session session,
        UiPermissionGate gate,
        string userPrompt,
        Action<IReadOnlyList<TodoItem>>? todoSink,
        Action<string, string>? subagentActivity,
        IReadOnlyList<ITool>? extraTools = null,
        long lastContextTokens = 0,
        Func<IReadOnlyList<UserQuestion>, CancellationToken, Task<UserQuestionAnswers?>>? askUser = null,
        Func<string, CancellationToken, Task<PlanApprovalDecision>>? planApproval = null,
        AgentWorkerManager? workers = null,
        bool coordinatorMode = false,
        bool memoryPaused = false,
        Action<JarvisCode.Core.Hooks.HookEvent, System.Text.Json.Nodes.JsonObject>? fireHook = null,
        Func<string, string, Task<string?>>? sendToSession = null,
        Action<string, JarvisCode.Core.Agent.AgentEvent>? subagentEvents = null,
        bool ultracodeMode = false,
        SkillSessionState? skillState = null,
        JarvisCode.Core.BackgroundTasks.BackgroundMoveRequests? backgroundMoves = null,
        JarvisCode.Core.Agent.TaskBoard? tasks = null,
        JarvisCode.Core.Agent.TeamStore? teams = null,
        Func<string, string, string?, Task>? teammateMessage = null,
        JarvisCode.Core.Agent.PlanApprovalRegistry? planApprovals = null,
        string? sessionGoal = null,
        IReadOnlyList<JarvisCode.Core.Hooks.FunctionHook>? functionHooks = null,
        JarvisCode.Core.Agent.TotalTokensReminder? totalTokens = null,
        bool nonInteractive = false,
        Func<string, Task<string?>>? subscribeIdle = null,
        bool systemPromptSnapshot = true,
        Func<bool>? pendingDelivery = null,
        TurnCustomizations? customizations = null)
    {
        if (ResolveModel(session) is not { } model)
        {
            return null;
        }

        var settings = services.Settings.Current;
        var paths = services.Paths;
        var cwd = session.WorkingDirectory;
        var uiSettings = services.UiSettings.Current;
        var outputStyleName = customizations?.OutputStyleOverride ??
            (customizations?.DisableOutputStyles == true ? null : uiSettings.SessionOutputStyles.GetValueOrDefault(session.Id, uiSettings.OutputStyle));
        var outputStyle = OutputStyles.Find(outputStyleName, cwd, paths.Root, customizations?.PluginOutputStylePaths);
        var outputStyleBlock = OutputStyles.PromptBlock(outputStyleName, cwd, paths.Root, customizations?.PluginOutputStylePaths);

        // An invoked skill's model:/effort: frontmatter overrides following
        // turns (the reference applies them to its live loop options).
        if (skillState?.ModelOverride is { Length: > 0 } modelOverride &&
            ModelCatalog.Find(services.Settings.Models, modelOverride) is { } overridden)
        {
            model = overridden;
        }

        var provider = services.Providers.Get(model.ProviderId);
        var planMode = gate.Mode == JarvisCode.Core.Permissions.PermissionMode.Plan;
        // Which of the reference's prompt sections, tools and message shapes this
        // model receives — measured per model, see PromptModelProfile.
        var profile = PromptModelProfile.For(model.ModelId);
        // Ultracode is session-scoped and runs at xhigh on the wire; the
        // reference's ultrathink/ultracode keywords ride the message as
        // system-reminders instead of touching the payload (2.x behavior).
        var thinkingEffort = ultracodeMode
            ? ThinkingEffort.XHigh
            : EffortLevels.ResolveEffort(skillState?.EffortOverride ?? settings.ThinkingEffortName);
        if (!ProviderCapabilities.For(provider).SupportsThinkingEffort) thinkingEffort = ThinkingEffort.Off;

        var plugins = customizations is null ? PluginLibrary.LoadUser(paths)
            : Plugins.Combine(customizations.Plugins, customizations.DisableAutomaticDiscovery
                ? Plugins.PluginContent.Empty : PluginLibrary.LoadActive(paths, cwd, uiSettings));
        // The skill namespace, reference-style: skills + legacy commands +
        // plugin entries in one list, minus "off" overrides; dormant
        // conditional (paths:) skills stay invisible until a touched file
        // activates them, and a "user-invocable-only" override reads as
        // disable-model-invocation so the tool refuses it.
        var enabledSkills = SkillCatalog.Enabled(customizations?.ResolveSkills(cwd, paths, uiSettings)
            ?? SkillCatalog.LoadAll(cwd, paths), uiSettings);
        var skills = enabledSkills
            .Where(s => s.Paths.Count == 0 || skillState?.ActivatedConditionalSkills.Contains(s.Name) == true)
            .Select(s => string.Equals(
                    SkillCatalog.OverrideFor(uiSettings, s.Name),
                    SkillCatalog.OverrideUserInvocableOnly, StringComparison.OrdinalIgnoreCase)
                ? s with { DisableModelInvocation = true }
                : s)
            .ToList();
        var hooks = customizations is null ? HookRunner.Load(cwd, paths.UserHooksFile, plugins.HookFiles)
            : HookRunner.LoadOnlyFiles(cwd, customizations.DisableHooks ? [] :
                plugins.HookFiles.Concat(customizations.AutomaticHookFiles(cwd, paths)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (skillState is { ExtraHooks.Count: > 0 })
        {
            // Hooks contributed by invoked skills join the session's set.
            hooks = hooks.WithExtra(skillState.ExtraHooks);
        }

        // /goal is a Stop hook the model answers, not a reminder: the turn cannot
        // end until a model reading the transcript says the condition is met.
        if (sessionGoal is { Length: > 0 } goal)
        {
            hooks = hooks.WithExtra([new JarvisCode.Core.Hooks.HookDefinition(
                JarvisCode.Core.Hooks.HookEvent.Stop, null, goal,
                (int)JarvisCode.Core.Hooks.PromptHooks.DefaultTimeout.TotalSeconds,
                JarvisCode.Core.Hooks.HookKind.Prompt, goal)]);
        }

        if (functionHooks is { Count: > 0 })
        {
            // The host's own built-in hooks (the reference registers its
            // preview-verification pair the same way).
            hooks = hooks.WithFunctions(functionHooks);
        }

        if (hooks.Hooks.Any(h => h.Kind == JarvisCode.Core.Hooks.HookKind.Prompt))
        {
            hooks = hooks.WithPromptEvaluator(PromptHookEvaluation.Create(
                services, provider, model, () => [.. session.Messages], cwd));
        }

        // The two kinds that leave this process are wired only when a hook of
        // that kind exists, so an ordinary session opens no client for them.
        if (hooks.Hooks.Any(h => h.Kind is JarvisCode.Core.Hooks.HookKind.Http
                or JarvisCode.Core.Hooks.HookKind.McpTool))
        {
            hooks = hooks.WithTransports(
                HookTransports.CreateHttpSender(),
                HookTransports.CreateMcpCaller(services.Mcp, cwd),
                new JarvisCode.Core.Hooks.HttpHookPolicy(
                    settings.AllowedHttpHookUrls, settings.HttpHookAllowedEnvVars));
        }
        if (skillState is not null)
        {
            // Dormant conditional (paths:) skills, watched by the view model:
            // a read or edit of a matching file activates them mid-session.
            skillState.ConditionalCandidates.Clear();
            skillState.ConditionalCandidates.AddRange(enabledSkills.Where(s =>
                s.Paths.Count > 0 && !skillState.ActivatedConditionalSkills.Contains(s.Name)));
        }
        var customAgents = customizations is null ? Merge(CustomAgents.Load(cwd, paths.UserAgentsDirectory), plugins.Agents)
            : Merge(customizations.Agents, Merge(plugins.Agents,
                customizations.DisableAutomaticDiscovery || customizations.DisableAgents ? [] :
                    CustomAgents.Load(cwd, customizations.SettingSources?.Contains("user") == false ? null : paths.UserAgentsDirectory)));

        // /pause-memory: the tool disappears and the prompt loses the index for
        // the rest of the session (recall is gated in the view model).
        var memoryDirectory = memoryPaused ? null : ProjectMemory.DirectoryFor(paths.MemoryRoot, cwd);
        var memoryIndex = memoryDirectory is null ? null : ProjectMemory.ReadIndexForPrompt(memoryDirectory);

        // A session with a team shares a memory directory with its teammates.
        // It is created here so the prompt can promise it already exists — and
        // only once the team has members, so a solo session neither carries the
        // block nor leaves an empty directory behind.
        var hasTeammates = teams?.Read(session.Id)?.Members.Count > 0;
        var teamMemoryDirectory = hasTeammates ? teams!.EnsureMemoryDirectory(session.Id) : null;
        gate.MemoryDirectories = teamMemoryDirectory is null ? [] : [teamMemoryDirectory];

        var checkpoint = services.Checkpoints.BeginTurn(
            session.Id,
            services.Checkpoints.NextTurnNumber(session.Id),
            userPrompt,
            session.Messages.Count);

        var subagentToolPlaceholder = new SubagentTool(services.Orchestrator);
        var allTools = new List<ITool>
        {
            new ReadFileTool(),
            new ReadDocumentTool(),
            new WriteFileTool(),
            new EditFileTool(),
            new ListDirectoryTool(),
            new GlobTool(),
            new GrepTool(),
            new ShellTool(),
            // The reference ships both shells on Windows as separate tools.
            new ShellTool(ShellKind.Bash),
            // The reference's WebFetch: a url and a prompt, answered by a model
            // pass over the fetched page rather than by handing back its text.
            new VendorWebFetchTool(services.Http, provider, model.ModelId, thinkingEffort),
            new TodoTool(),
            new SkillTool(services.Orchestrator),
            new TaskOutputTool(),
            new TaskKillTool(),
            new MonitorTool(),
            new NotebookEditTool(),
            new GitStatusTool(),
            new GitDiffTool(),
            new GitLogTool(),
            new GitShowTool(),
            new GitBlameTool(),
            new AskUserQuestionTool(),
            new SendMessageTool(),
            subagentToolPlaceholder,
        };
        if (nonInteractive)
        {
            // A print run has nobody to answer this UI card. Its host now owns
            // armed Monitor tasks and waits for their events across turns.
            allTools.RemoveAll(static t => t.Name == "AskUserQuestion");
        }

        JarvisCode.Core.LanguageServers.PluginLanguageServerManager? pluginLanguageServers = null;
        if (customizations?.DisableLsp != true)
        {
            allTools.AddRange(IdeServices.Tools(cwd));
            if (customizations is { PluginLspFiles.Count: > 0 })
            {
                var languageServers = pluginLanguageServers = PluginLanguageServers(cwd, customizations.PluginLspFiles);
                if (languageServers.HasServers)
                {
                    for (var index = 0; index < allTools.Count; index++)
                        if (allTools[index].Name is "Read" or "Write" or "Edit")
                            allTools[index] = new LspDocumentSyncTool(allTools[index], languageServers);
                    allTools.Add(new LspTool(languageServers));
                }
            }
        }

        if (!ShellEnvironment.OffersPowerShell(services.Headless))
        {
            // Launched from Git Bash, the reference registers Bash alone.
            allTools.RemoveAll(static t => t.Name == "PowerShell");
        }

        var commitTrailer = CommitTrailers.Name(model);
        if (tasks is not null && profile.TakesTaskBoard)
        {
            // The board only exists on the Code surface, and only for the models
            // the reference gives it to (every classic-prompt model without the
            // mid-conversation system role — 27 tools against 23 in every
            // capture); the team-only doc segments appear once the session
            // actually has teammates, which is when the reference shows them.
            bool inTeam = teams?.Read(session.Id)?.Members.Count > 0;
            allTools.Add(new TaskCreateTool(inTeam));
            allTools.Add(new TaskGetTool());
            allTools.Add(new TaskListTool(inTeam));
            allTools.Add(new TaskUpdateTool());
        }
        if (uiSettings.DynamicWorkflowsEnabled && customizations?.DisableWorkflows != true)
        {
            var configuredSize = uiSettings.WorkflowSize;
            allTools.Add(new WorkflowTool(
                services.Orchestrator,
                JarvisCode.Core.Tools.BuiltIn.WorkflowSizeGuideline.Parse(configuredSize),
                sizeIsDefault: string.IsNullOrEmpty(configuredSize)));
        }
        if (!memoryPaused)
        {
            // /pause-memory withholds the tool itself, not just its directory —
            // the model should not see a tool the session has switched off.
            allTools.Add(new MemoryTool());
        }
        if (settings.EnableWebSearch)
        {
            allTools.Add(new WebSearchTool(services.Http, settings.SearxngBaseUrl));
        }
        else if (VendorWebSearchTool.IsEnabled(
                     provider.Id,
                     model.ModelId,
                     JarvisCode.Core.Providers.ProviderDecorators.Unwrap(provider) is not JarvisCode.Providers.LlmApi.LlmApiProvider relay ||
                     relay.Protocol == JarvisCode.Providers.LlmApi.LlmApiProtocol.Anthropic))
        {
            // Web search switched off: fall back to the reference CLI's WebSearch —
            // Anthropic's server-side search behind a side query — on the providers
            // that serve it (first-party always, Vertex per model family, Bedrock
            // never; no other provider has a reference counterpart).
            allTools.Add(new VendorWebSearchTool(provider, model.ModelId, thinkingEffort, session.Id));
        }

        // Custom commands ride the skill namespace (the reference retired its
        // SlashCommand tool — the Skill tool covers legacy commands too).
        allTools.AddRange(services.Mcp.Tools);
        if (services.Mcp.ConnectedToolCounts.Count > 0)
        {
            allTools.Add(new JarvisCode.Core.Mcp.ListMcpResourcesTool(services.Mcp));
            allTools.Add(new JarvisCode.Core.Mcp.ReadMcpResourceTool(services.Mcp));
        }
        if (extraTools is not null)
        {
            allTools.AddRange(extraTools);
        }

        // Plan mode keeps the read-only set plus the reference plan-file flow:
        // write/edit scoped to the plan file, and ExitPlanMode for approval.
        // The registry is live in both directions — an approved exit swaps in
        // the full set mid-turn, and EnterPlanMode swaps in the plan set.
        var planFilePath = PlanModeTools.PlanFilePath(cwd, session.Id);
        // Plan mode used to open Write and Edit to every path and scope them
        // back to the plan file inside a wrapper; the gate now knows the plan
        // file itself and asks about everything else, as the reference does.
        gate.ExtraRuleLines = [];
        gate.PlanFilePath = planFilePath;
        gate.AutoClassifyAsync = async (request, ct) =>
        {
            var binding = gate.PrAutoFixActive
                ? services.UiSettings.Current.SessionPrAutoFix.GetValueOrDefault(session.Id) : null;
            string? standing = null;
            if (binding is not null)
            {
                standing = PrAutoFixPrompts.StandingAuthorizationIfCurrent(binding, cwd,
                    await PrAutoFixMonitor.ReadCurrentBranchAsync(cwd, ct));
                if (standing is null)
                    return AutoPermissionVerdict.Ask("Auto-fix no longer matches the checked-out branch. Rebind this session before continuing.");
            }
            var verdict = await AutoPermissionClassifier.ClassifyAsync(
                provider, model.ModelId, session.Messages, request, cwd, ct, standing);
            if (binding is not null && (!gate.PrAutoFixActive ||
                services.UiSettings.Current.SessionPrAutoFix.GetValueOrDefault(session.Id) != binding ||
                verdict.Decision == "allow" && await PrAutoFixMonitor.ReadCurrentBranchAsync(cwd, ct) != binding.Branch))
                return AutoPermissionVerdict.Ask("Auto-fix authorization changed while this action was being assessed.");
            return verdict;
        };
        gate.PermissionRequestHookAsync = hooks.Has(HookEvent.PermissionRequest)
            ? (tool, args, risk, ct) => hooks.RunPermissionRequestAsync(tool, args, risk, ct)
            : null;
        // Many MCP tools would ride every request as schema bloat; past a size
        // threshold they are deferred behind tool_search (the reference's
        // deferred-tools mechanism) and fetched by name or keyword on demand.
        // Tools are advertised in ordinal name order, which is the order every
        // reference capture lists them in.
        // The reference's fork gate: an interactive session that is not a
        // coordinator, with CLAUDE_CODE_FORK_SUBAGENT forcing it either way. It
        // resolves to false on this app's own window, and true in the CLI's
        // REPL — which is the same answer the reference gives on both surfaces,
        // since its desktop hosts the CLI over the SDK (isInteractive() false)
        // while a terminal REPL sets it.
        var forkAvailable = JarvisCode.Core.Agent.ForkAgent.IsAvailable(
            JarvisCode.Core.Agent.ForkAgent.IsEnabled(
                interactive: services.Headless && !nonInteractive,
                coordinatorMode: coordinatorMode),
            customAgents.Select(static a => a.Name),
            allowedAgentTypes: null);
        var fullToolSet = Sorted(ReferenceToolDocs.Apply(
            nonInteractive ? allTools : allTools.Append(PlanModeTools.CreateEnterTool(gate, planFilePath)),
            profile: profile, commitTrailer: commitTrailer, forkAvailable: forkAvailable));
        // Seeded with what this session has already fetched: the reference's
        // tool_reference blocks keep a fetched tool advertised for the whole
        // conversation, and a registry rebuilt per turn has to be told.
        var deferredRegistry = WillDefer(fullToolSet, model.ModelId)
            ? new DeferredToolRegistry(fullToolSet, Deferrable, skillState?.LoadedDeferredTools)
            : null;
        IToolRegistry fullRegistry = (IToolRegistry?)deferredRegistry ?? new ToolRegistry(fullToolSet);
        IToolRegistry registry;
        if (coordinatorMode && !planMode)
        {
            // The coordinator keeps only the delegation tools; workers (below)
            // keep the full set.
            var coordinatorSet = allTools.Where(static t => CoordinatorMode.IsCoordinatorTool(t.Name));
            registry = new ToolRegistry(Sorted(ReferenceToolDocs.Apply([.. coordinatorSet], coordinatorMode: true, profile: profile, commitTrailer: commitTrailer)));
        }
        else
        {
            // Plan mode keeps the whole tool set, as the reference does — a -p
            // plan capture on CLI 2.1.257 advertises Bash, Edit and Write — and
            // the gate holds the line, asking "Cannot call X while in plan mode."
            // for every non-read-only call that is not the plan file.
            // ExitPlanMode joins only where somebody can approve it.
            IEnumerable<ITool> planSet = allTools;
            if (!nonInteractive)
            {
                planSet = planSet.Append(new ExitPlanModeTool());
            }

            registry = new PlanModeTools.ModeSwitchedRegistry(
                gate,
                new ToolRegistry(Sorted(ReferenceToolDocs.Apply(
                    planSet, profile: profile, commitTrailer: commitTrailer, forkAvailable: forkAvailable))),
                fullRegistry);
        }

        // What an invoked skill applies to the session: lifetime usage, its
        // allowed/disallowed-tools as session permission grants, its hooks,
        // and its model/effort overrides for the following turns.
        Action<SkillDefinition>? skillInvoked = skillState is null ? null : skill =>
            SkillCatalog.ApplyInvocationEffects(services.UiSettings, gate, skillState, skill, cwd);

        // The reference prompts "Execute skill: {name}" for skills declaring
        // permission-relevant frontmatter; plain skills run silently.
        Func<string, CancellationToken, Task<bool>> confirmSkill = async (skillName, ct) =>
        {
            if (gate.Mode == JarvisCode.Core.Permissions.PermissionMode.Bypass)
            {
                return true;
            }
            if (gate.PromptAsync is not { } prompt)
            {
                return false;
            }
            var decision = await prompt(
                new PermissionPrompt(
                    "Skill", $"Execute skill: {skillName}", skillName, null, null,
                    JarvisCode.Core.Permissions.CallRisk.Standard),
                ct);
            return decision != JarvisCode.Core.Agent.PermissionDecision.Deny;
        };

        // Subagents can start well after this turn was assembled. Freeze provider-native controls
        // now so a UI effort change cannot mutate a Dictionary while a worker copies it later.
        var providerOptionsByModel = SnapshotProviderOptions(settings);
        var toolContext = new ToolExecutionContext
        {
            WorkingDirectory = cwd,
            SessionId = session.Id,
            AdditionalDirectories = session.AdditionalDirectories,
            ShellTimeout = TimeSpan.FromSeconds(Math.Max(5, settings.ShellTimeoutSeconds)),
            Subagents = new SubagentServices
            {
                // The reference gives a subagent a document of its own shape,
                // not the main prompt and not this engine's older one. Note it
                // deliberately carries no project instructions: a capture with a
                // CLAUDE.md present shows the parent loading it and the child
                // receiving it in neither its prompt nor its messages.
                SystemPrompt = request => ReferenceSubagentPrompt.Build(
                    request.AgentType,
                    request.WorkingDirectory,
                    services.Settings.Models.FirstOrDefault(m => m.ModelId == request.ModelId)
                        ?? new ModelInfo(model.ProviderId, request.ModelId, request.ModelId, model.MaxContextTokens),
                    request.AdditionalDirectories,
                    request.CustomAgentPrompt,
                    skills,
                    request.TotalTokensBlock),
                // Every agent starts at the full budget, whatever its parent has
                // consumed — a subagent's first request reads 15000000.
                LeadingSystemBlocks = static modelId => LeadingBlocksFor(PromptModelProfile.For(modelId)),
                TotalTokensFactory = totalTokens is null || !totalTokens.Enabled
                    ? null
                    : () => new JarvisCode.Core.Agent.TotalTokensReminder(
                        totalTokens.Mode, totalTokens.Budget, totalTokens.AfterUserTurn),
                ParentProvider = provider,
                ParentModelId = model.ModelId,
                ProviderOptionsForModel = modelId => providerOptionsByModel.TryGetValue(modelId, out var options)
                    ? options
                    : new Dictionary<string, string>(),
                Models = services.Settings.Models,
                Providers = services.Providers,
                // Coordinator turns hold a filtered registry, but their workers
                // do the actual work and need the full one.
                ParentTools = coordinatorMode && !planMode ? fullRegistry : registry,
                PermissionGate = gate,
                ActivityReporter = subagentActivity,
                EventReporter = subagentEvents,
                CustomAgents = customAgents,
                Hooks = hooks,
                Skills = skills,
                Teams = teams,
                TeamName = teams is null ? null : session.Id,
                TeammateMessage = teammateMessage,
                TeammateMode = JarvisCode.Core.Agent.TeammateBackends.Parse(uiSettings.TeammateMode),
                ForkEnabled = forkAvailable,
            },
            Checkpoints = checkpoint,
            TodoSink = todoSink,
            BackgroundTasks = services.BackgroundTasks,
            IsNonInteractiveSession = gate.PromptAsync is null,
            BackgroundMoves = backgroundMoves,
            Skills = skills,
            SkillTracker = skillState?.Tracker,
            CoordinatorMode = coordinatorMode && !planMode,
            UserTypedSkillThisTurn = skillState is null ? null : skillState.UserTypedThisTurn,
            SkillInvoked = skillInvoked,
            ConfirmSkillExecutionAsync = confirmSkill,
            RunSkillShell = (command, shell) => SkillShellRunner.Run(
                command, shell, cwd, gate, TimeSpan.FromSeconds(Math.Max(5, settings.ShellTimeoutSeconds))),
            EffortName = thinkingEffort.ToString().ToLowerInvariant(),
            MemoryDirectory = memoryDirectory,
            // The reference's output_file for a background agent: its whole JSONL
            // transcript, written beside this session's persisted tool results.
            AgentTranscriptDirectory = SessionScratchpad.AgentTranscriptsDirectory(cwd, session.Id),
            PlanMode = planMode,
            PlanModeProvider = () => gate.Mode == JarvisCode.Core.Permissions.PermissionMode.Plan,
            // Always present so ExitPlanMode still works after a mid-turn
            // EnterPlanMode; outside plan mode the tool is simply not offered.
            PlanFilePath = planFilePath,
            AskUserAsync = askUser,
            PlanApprovalAsync = planApproval,
            Workers = workers,
            FireHook = fireHook,
            SendToSessionAsync = sendToSession,
            SubscribeToSessionIdleAsync = subscribeIdle,
            Tasks = tasks,
            PlanApprovals = planApprovals,
            TeamStore = teams,
            TeamName = teams is null ? null : session.Id,
            TeamMemoryDirectory = teamMemoryDirectory,
            Workflows = new WorkflowServices
            {
                Store = new JarvisCode.Core.Agent.WorkflowStore(cwd, paths.WorkflowRunsDirectory,
                    customizations?.WorkflowDefinitions(), discover: customizations?.DisableAutomaticDiscovery != true),
                RunDirectory = System.IO.Path.Combine(paths.WorkflowRunsDirectory, session.Id),
                Enabled = uiSettings.DynamicWorkflowsEnabled,
                Size = JarvisCode.Core.Tools.BuiltIn.WorkflowSizeGuideline.Parse(uiSettings.WorkflowSize),
                SizeIsDefault = string.IsNullOrEmpty(uiSettings.WorkflowSize),
                // The reference's own switches, honoured by their own names.
                DisabledByManagedSettings = HasEnvironmentFlag("CLAUDE_CODE_DISABLE_WORKFLOWS"),
                NamedWorkflowsOnly = HasEnvironmentFlag("CLAUDE_WORKFLOW_NAME_ONLY"),
                Budget = WorkflowTurnBudget,
                // A run lists itself in the Background tasks pane while it works,
                // the way the reference shows its Workflow rows.
                RunSink = title =>
                {
                    var adopted = services.BackgroundTasks.Adopt(
                        title, session.Id, title, killRequested: () => { }, kind: "workflow");
                    var feed = services.WorkflowRuns.Open(adopted.Id);
                    return new JarvisCode.Core.Tools.BuiltIn.WorkflowRunHandle(
                        line => adopted.Append(line + "\n"),
                        success => adopted.Complete(success ? 0 : 1),
                        feed.Apply);
                },
            },
        };

        // Project instructions and the memory index ride the first message's
        // context reminder now, not the prompt; gitStatus rides the prompt, not
        // a reminder. Both are the reference's placements, measured.
        // The host's own sections are gated on what this front-end can actually
        // do: the CLI has no transcript to put a Run button in, and the Browser
        // pane and the extension are two surfaces only when both are up.
        // Every host append is the desktop's: a CLI 2.1.257 capture — with the
        // desktop entrypoint too — carries none of them, so the headless
        // front-end sends none either.
        var hostSections = services.Headless
            ? ""
            : HostPromptSections.Render(new HostPromptSections.Capabilities(
                ClickableFileLinks: true,
                RunButtonOnShellFences: true,
                HasInAppBrowser: true,
                HasChromeBrowserSurface: services.Browser.IsConnected)
              {
                  // The desktop opens its append with the worktree paragraph
                  // whenever the session runs in one.
                  WorktreePath = HostPromptSections.LinkedWorktree(cwd)?.Path,
                  WorktreeName = HostPromptSections.LinkedWorktree(cwd)?.Name,
              })
              // Auto verify is a host append too, and the reference's host appends
              // all land before gitStatus. This port used to add it after the
              // trailer, which put a block the desktop sends among the host's
              // below the last thing the client writes.
              + PreviewVerification.PromptBlock(PreviewServers.ReadAutoVerify(cwd))
              // <simulator_tools> follows the auto-verify block, which is the
              // order the reference's own append accumulates them in, and rides
              // only while the Android emulator server is really live this turn.
              + HostPromptSections.SimulatorTools(
                  allTools.Any(static tool =>
                      tool.Name == InternalMcpServers.WireName(
                          InternalMcpServerNames.AndroidEmulator, "control")));

        // What follows gitStatus. Both blocks are additions rather than ports —
        // neither is in any installed artifact — and the safety policy rides
        // only when this session can actually drive a screen or a browser,
        // which is the trigger a two-agent experiment measured.
        var toolNames = allTools.Select(static tool => tool.Name).ToList();
        var trailingBlocks = services.Headless ? null : HostPromptSections.Trailer(
            hasComputerUse: toolNames.Any(static n =>
                n.StartsWith("mcp__computer-use__", StringComparison.Ordinal) || n == "screenshot"),
            hasBrowser: toolNames.Any(static n =>
                n.StartsWith("mcp__Claude_Browser__", StringComparison.Ordinal) ||
                n.StartsWith("mcp__claude-in-chrome__", StringComparison.Ordinal)));
        // An output style is appended to the harness prompt rather than
        // replacing it: all four of the reference's built-ins set
        // keepCodingInstructions. It rides *inside* the prompt, after
        // # Environment — not after gitStatus — and swaps the intro line.
        // Which of the reference's two prompts this model gets. The lean form is
        // the Claude 5 family's; everything else — including every model reached
        // through another provider — takes the classic one, which is what the
        // reference sends a model without the prompt bundle.
        // The static token block ends the CLI's part of either prompt; the live
        // one follows tool results and user prompts (see TotalTokensReminder).
        var totalTokensBlock = totalTokens?.RenderStatic(model.MaxContextTokens);
        // The four sections the reference emits behind a gate this port can
        // reproduce: # Language, # Background Session, # Focus mode and the
        // delegation steer. Focus mode is the reference's viewMode: "focus";
        // this session's Summary transcript view is the state that shows the
        // user prompts and responses and nothing between them.
        var gatedSections = GatedPromptSections.Resolve(
            settings,
            leanPrompt: profile.Lean,
            focusMode: TranscriptViewModes.Parse(
                uiSettings.TranscriptViewBySession.GetValueOrDefault(session.Id))
                == JarvisCode.App.Views.Panels.TranscriptViewMode.Summary,
            hasAgentTool: toolNames.Contains(SubagentTool.ToolName),
            steerLatch: skillState?.Steer);
        // The advisor block rides only when the advisor really answers: the
        // tool is registered on the same condition, so the model is never
        // told about a tool this turn does not carry.
        var advisorBlock = toolNames.Contains(AdvisorTool.ToolName) &&
            AdvisorPrompt.IsEnabled(uiSettings.AdvisorModelId)
                ? AdvisorPrompt.Block
                : null;
        var systemPrompt = profile.Lean
            ? ReferencePromptBuilder.Build(
                cwd,
                model,
                skills,
                memoryDirectory,
                session.AdditionalDirectories,
                GitStatusForSession(session.Id, cwd),
                services.Headless ? null : SessionScratchpad.Ensure(cwd, session.Id),
                hostSections,
                outputStyleBlock,
                trailingBlocks: trailingBlocks,
                totalTokensBlock: totalTokensBlock,
                shellLine: ShellEnvironment.PromptLine(services.Headless),
                gated: gatedSections,
                advisorBlock: advisorBlock)
            : ClassicPromptBuilder.Build(
                cwd,
                model,
                memoryDirectory,
                session.AdditionalDirectories,
                GitStatusForSession(session.Id, cwd),
                hostSections,
                trailingBlocks,
                hasTaskBoard: tasks is not null && profile.TakesTaskBoard,
                totalTokensBlock: totalTokensBlock,
                shellLine: ShellEnvironment.PromptLine(services.Headless),
                gated: gatedSections,
                advisorBlock: advisorBlock,
                outputStyleBlock: outputStyleBlock,
                keepCodingInstructions: outputStyle?.KeepCodingInstructions ?? true);
        if (teamMemoryDirectory is not null)
        {
            systemPrompt += "\n\n# Team memory (shared with your teammates)\n" +
                (memoryDirectory is null
                    ? JarvisCode.Core.Agent.TeamPrompts.TeamMemoryOnly(teamMemoryDirectory)
                    : JarvisCode.Core.Agent.TeamPrompts.MemoryDirectories(memoryDirectory, teamMemoryDirectory));
        }

        if (coordinatorMode && !planMode)
        {
            var mcpTools = services.Mcp.Tools;
            systemPrompt += "\n\n" + CoordinatorMode.BuildPrompt(
                allTools.Where(t => !mcpTools.Contains(t)).Select(static t => t.Name),
                mcpTools.Select(static t => t.Name));
        }

        // The reference's systemPromptSnapshot: the conversation's prompt is
        // recorded once and reused verbatim on every later request and resume, so
        // the cached prefix never churns mid-conversation. Off — as the reference
        // turns it off for --append-system-prompt — while an output style appends
        // to the prompt; re-recorded when the model or the directory changes.
        var snapshotKey = model.ModelId + "|" + cwd;
        if (systemPromptSnapshot && string.IsNullOrEmpty(outputStyleBlock))
        {
            if (session.SystemPromptSnapshot is { } snapshot && session.SystemPromptSnapshotKey == snapshotKey)
            {
                systemPrompt = snapshot;
            }
            else
            {
                session.SystemPromptSnapshot = systemPrompt;
                session.SystemPromptSnapshotKey = snapshotKey;
            }
        }

        // Mode state is evaluated after the reusable base prompt, so turning
        // Auto-fix off cannot replay a cached promise that it is still enabled.
        if (gate.PrAutoFixActive && (gate.Mode is PermissionMode.Auto or PermissionMode.Bypass) && PrAutoFixPrompts.StandingAuthorization(
                services.UiSettings.Current.SessionPrAutoFix.GetValueOrDefault(session.Id), cwd) is not null)
            systemPrompt += "\n\n" + PrAutoFixPrompts.Enabled;
        else if (!services.Headless)
            systemPrompt += "\n\n" + PrAutoFixPrompts.EventProvenance;

        // A fork runs on the parent's own prompt rather than the subagent
        // document — which is what "shares your prompt cache" describes — so the
        // subagent services take it once it exists.
        if (toolContext.Subagents is { } subagentServices)
        {
            toolContext = toolContext with
            {
                Subagents = subagentServices with { ParentSystemPrompt = systemPrompt },
            };
        }

        var context = new AgentTurnContext
        {
            Provider = provider,
            ModelId = model.ModelId,
            SystemPrompt = systemPrompt,
            LeadingSystemBlocks = LeadingBlocksFor(profile),
            // The reference writes an oversize tool result to disk and hands the
            // model a <persisted-output> block naming the file, rather than
            // spending the context on output it may not need.
            ResultPersistence = new JarvisCode.Core.Tools.ToolResultPersistence(
                SessionScratchpad.ToolResultsDirectory(cwd, session.Id)),
            ToolResultTrailer = totalTokens is null || !totalTokens.Enabled
                ? null
                : () => totalTokens.RenderLive(model.MaxContextTokens),
            // The reference's batching_reminder and silent_turn_reminder ride the
            // same trailing harness turn, ahead of the token block. Both need the
            // mid-conversation system role, and only a fable/mythos 5.1 model
            // owns either text without an environment override.
            ToolResultReminders = messages => JarvisCode.Core.Agent.ToolResultReminders.Compose(
                messages,
                ReminderOptions(model.ModelId, profile.Fable51, session.Id, skillState, settings),
                deliveryPending: pendingDelivery?.Invoke() ?? false).Concat(pluginLanguageServers?.DrainDiagnostics() ?? []).ToArray(),
            Messages = session.Messages,
            Tools = registry,
            PermissionGate = gate,
            ToolContext = toolContext,
            Hooks = hooks,
            // Anthropic would attach its own server-side tool called web_search, which
            // collides by name with the client tool above. Code sessions search through
            // the configured instance (or the vendor fallback's own side query) instead.
            EnableWebSearch = false,
            ThinkingEffort = thinkingEffort,
            ProviderOptions = CopyProviderOptions(settings, model.ModelId),
            // MaxOutputTokens is left at the reference's own ceiling: it subtracts
            // min(the model's max output, 20k), and every model the reference runs
            // on answers that with 20k. MicrocompactEnabled stays off for the same
            // reason the reference leaves it off — it hangs off the context-hint
            // reject path, whose server-side flag ships false.
            // Built whether or not auto-compaction may run: with it off the
            // blocking gate still needs the window and the model to measure
            // against, which is how the reference reads it too.
            AutoCompact = model.MaxContextTokens > AutoCompactOptions.ReserveTokens
                ? new AutoCompactOptions(model.MaxContextTokens, InitialContextTokens: lastContextTokens)
                {
                    Enabled = settings.AutoCompactEnabled,
                    ModelId = model.ModelId,
                    ConfiguredWindow = settings.AutoCompactWindow,
                    EnvironmentWindow = Environment.GetEnvironmentVariable(AutoCompactWindowVariable),
                    ThresholdPercent = PercentOverrideFromEnvironment(),
                    State = CompactionStateForSession(session.Id),
                    Precompute = settings.PrecomputeCompactionEnabled
                        ? PrecomputeStoreForSession(session.Id)
                        : null,
                    Sidecar = settings.PrecomputeCompactionEnabled && settings.PrecomputeSidecarEnabled
                        ? new PrecompactSidecar(
                            services.Sessions.DirectoryPath, session.Id, model.ModelId, ClientVersion)
                        : null,
                }
                : null,
            // The reference asks PreCompact before an automatic compaction too,
            // not only before /compact, and a refusal leaves the turn running
            // on the context it already had.
            CompactionBlockedAsync = hooks.Has(JarvisCode.Core.Hooks.HookEvent.PreCompact)
                ? async trigger =>
                    (await hooks.RunPreCompactAsync(trigger, session.Id, CancellationToken.None)) is
                        { Allowed: false } refusal
                        ? refusal.BlockReason ?? "the hook exited non-zero"
                        : null
                : null,
        };

        // What the roster says a read-only agent is denied: the Agent tool itself
        // and every tool that can write, in registry order — the reference prints
        // its own list ("All tools except Agent, …, Edit, Write, NotebookEdit").
        var excluded = new List<string> { SubagentTool.ToolName };
        excluded.AddRange(allTools
            .Where(static t => !t.IsReadOnly && t.Name != SubagentTool.ToolName)
            .Select(static t => t.Name));
        var roster = new HarnessSystemMessage.RosterContext(
            profile.Lean, excluded, IncludeGuideAgent: !services.Headless);

        if (customizations?.DisablePrefetch == true && context.AutoCompact is { } compact)
            context = context with { AutoCompact = compact with { Precompute = null, Sidecar = null } };

        return new TurnSetup(
            customizations?.DisableAttribution == true ? context : WithClientAttribution(context, session),
            model, checkpoint, hooks, roster, deferredRegistry);
    }

    /// <summary>
    /// Adds the request-identity fields the user opted into (Settings ›
    /// Fingerprints). All three are off by default, and with all three off the
    /// context is returned exactly as it was built.
    /// </summary>
    /// <summary>
    /// The system entries ahead of the harness prompt, in the reference's order
    /// (CLI 2.1.257: billing header, identity, # Reporting outcomes, prompt): the
    /// identity sentence as its own cached block, then # Reporting outcomes,
    /// uncached, for the models that take it (fable/mythos 5.1). The reference
    /// gates that block on its billing header being on, which it always is
    /// there; this app's fingerprints are opt-in and the block is protocol, not
    /// attribution, so it rides whenever the model is eligible. Subagents get the
    /// same set for their own model.
    /// </summary>
    internal static IReadOnlyList<JarvisCode.Core.Providers.SystemPromptBlock> LeadingBlocksFor(PromptModelProfile profile)
    {
        var blocks = new List<JarvisCode.Core.Providers.SystemPromptBlock>
        {
            new(PromptIdentity.Sentence, Cached: true),
        };
        if (profile.EligibleForReportingOutcomes)
        {
            blocks.Add(new(ReferencePromptBuilder.ReportingOutcomes));
        }

        return blocks;
    }

    /// <summary>Ordinal name order — the order every reference capture advertises its tools in.</summary>
    private static IReadOnlyList<ITool> Sorted(IEnumerable<ITool> tools) =>
        [.. tools.OrderBy(static t => t.Name, StringComparer.Ordinal)];

    private AgentTurnContext WithClientAttribution(AgentTurnContext context, Session session)
    {
        var ui = services.UiSettings.Current;

        if (ui.SendAttributionBlock)
        {
            // The reference carries this as its own leading system block, uncached,
            // ahead of everything else — and so does this port now. The fingerprint
            // reads the conversation's first message, so it is stable for the whole
            // session and does not churn the prompt cache.
            var entrypoint = services.Headless ? "cli" : "app";
            context = context with
            {
                LeadingSystemBlocks =
                [
                    new JarvisCode.Core.Providers.SystemPromptBlock(
                        ClientAttribution.Block(entrypoint, ClientAttribution.FirstUserText(session.Messages))),
                    .. context.LeadingSystemBlocks,
                ],
            };
        }

        if (ui.SendRequestMetadata)
        {
            var stored = ui.ClientDeviceId;
            var deviceId = ClientAttribution.EnsureDeviceId(ui);
            if (!string.Equals(stored, deviceId, StringComparison.Ordinal))
            {
                services.UiSettings.Save();
            }

            // Applied as the base so a hand edit in the request inspector, which the
            // surface merges on top, still wins.
            context = context with
            {
                BodyOverride = RequestBodyOverride.Apply(
                    ClientAttribution.MetadataPatch(deviceId, session.Id), context.BodyOverride),
            };
        }

        if (ui.SendSessionIdHeader)
        {
            context = context with
            {
                ExtraHeaders =
                [
                    .. context.ExtraHeaders,
                    new KeyValuePair<string, string>(ClientAttribution.SessionIdHeaderName, session.Id),
                ],
            };
        }

        return context;
    }

    private static IReadOnlyList<T> Merge<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
        => b.Count == 0 ? a : [.. a, .. b];

    private static IReadOnlyDictionary<string, string> CopyProviderOptions(
        JarvisCode.Core.Settings.AppSettings settings,
        string modelId) =>
        settings.ProviderOptionsByModel.TryGetValue(modelId, out var options)
            ? new Dictionary<string, string>(options, StringComparer.Ordinal)
            : new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SnapshotProviderOptions(
        JarvisCode.Core.Settings.AppSettings settings) =>
        settings.ProviderOptionsByModel.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
                entry.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
}
