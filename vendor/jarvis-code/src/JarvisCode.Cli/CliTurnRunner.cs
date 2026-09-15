using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Utilities;

namespace JarvisCode.Cli;

/// <summary>Event callbacks a front-end (print or REPL) hangs on a running turn.</summary>
internal sealed class CliTurnEvents
{
    public Action<string>? TextDelta { get; init; }
    /// <summary>Replaceable browser text for the live region; an empty snapshot clears it.</summary>
    public Action<string>? TextPreview { get; init; }
    public Action<string>? ThinkingDelta { get; init; }
    public Action<ChatMessage>? AssistantMessageCompleted { get; init; }
    public Action<ToolExecutionStarted>? ToolStarted { get; init; }
    public Action<ToolExecutionCompleted>? ToolCompleted { get; init; }
    public Action<ToolExecutionDenied>? ToolDenied { get; init; }
    public Action<Usage, Usage>? UsageReported { get; init; }
    public Action<string>? Notice { get; init; }
    public Action<string, AgentEvent>? SubagentEvent { get; init; }
    public Action<Core.Hooks.HookLifecycleEvent>? HookObserved { get; init; }

    /// <summary>
    /// Fires once the turn's context is assembled and before anything runs, so a
    /// front-end can report what this turn actually advertises (stream-json's
    /// `system:init` line needs the real tool list, not a guess).
    /// </summary>
    public Action<TurnSetup>? SetupPrepared { get; init; }
}

internal sealed record CliTurnResult(
    TurnEndReason Reason,
    string? Detail,
    Usage TotalUsage,
    Usage LastCallUsage,
    int ApiCalls,
    string FinalText,
    int PermissionDenials)
{
    public bool CanRetry { get; init; } = true;
}

/// <summary>
/// Runs one user turn the way the app's Code surface does: the same
/// TurnContextFactory assembly (tools, skills, hooks, memory, MCP, subagents),
/// the same system-reminder attachments on the outgoing message (gitStatus on
/// the first message, the budgeted skill listing, plan mode, keywords, memory
/// recall), and the same hook sequence — minus everything that needs a window.
/// </summary>
internal sealed class CliTurnRunner(CliServices services, CliOptions options, UiPermissionGate gate)

{
    public SkillSessionState SkillState { get; } = new();

    /// <summary>Whether this plan-mode run has already carried the full workflow.</summary>
    private bool _planWorkflowSent;

    /// <summary>The token-budget reminder for this runner's main agent (see TotalTokensReminder).</summary>
    private readonly Core.Agent.TotalTokensReminder _totalTokens = new(
        Core.Agent.TotalTokensReminder.ResolveMode(
            Environment.GetEnvironmentVariable(Core.Agent.TotalTokensReminder.ModeVariable),
            services.App.Settings.Current.TotalTokensReminder),
        Core.Agent.TotalTokensReminder.ResolveBudget(
            Environment.GetEnvironmentVariable(Core.Agent.TotalTokensReminder.BudgetVariable),
            services.App.Settings.Current.TotalTokensReminderBudget),
        Core.Agent.TotalTokensReminder.ResolveAfterUserTurn(
            Environment.GetEnvironmentVariable(Core.Agent.TotalTokensReminder.AfterUserTurnVariable),
            services.App.Settings.Current.TotalTokensReminderAfterUserTurn));

    private bool _sessionStartRan;
    private long _lastContextTokens;
    private readonly JarvisCode.Core.Agent.PlanApprovalRegistry _planApprovals = new();
    private JarvisCode.Core.Agent.TaskBoard? _tasks;
    private JarvisCode.Core.Agent.TeamStore? _teams;

    public UiPermissionGate Gate => gate;
    public Func<TurnSetup, TurnSetup>? TransformSetup { get; set; }
    public IReadOnlyList<ITool> ExtraTools { get; set; } = [];
    public Core.Hooks.HookRunner? LastHooks { get; private set; }
    public TurnSetup? LastSetup { get; private set; }
    public bool ModelSelected { get; set; } = options.Model is not null;
    public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];
    public IReadOnlyDictionary<string, long> InstructionTokens => _instructionTokens;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _instructionTokens = new(StringComparer.OrdinalIgnoreCase);
    public bool ExcludeDynamicSections { get; set; } = options.ExcludeDynamicSections;
    private string? _relocatedEnvironment;

    /// <summary>An effort picked with `/effort <level> s`: this session only, never saved.</summary>
    public string? SessionEffortName { get; set; }

    /// <summary>
    /// The three seams an interactive front-end fills in and a print run leaves
    /// empty: AskUserQuestion's card, ExitPlanMode's approval, and the manager
    /// that owns background agents. Without them those tools report themselves
    /// unavailable, which is what a print run wants.
    /// </summary>
    public Func<IReadOnlyList<Core.Tools.BuiltIn.UserQuestion>, CancellationToken,
        Task<Core.Tools.BuiltIn.UserQuestionAnswers?>>? AskUser { get; set; }

    public Func<string, CancellationToken, Task<Core.Tools.BuiltIn.PlanApprovalDecision>>? PlanApproval { get; set; }

    public JarvisCode.Core.Agent.AgentWorkerManager? Workers { get; set; }

    /// <summary>The session goal /goal registered, which rides the turn as a Stop prompt hook.</summary>
    public string? SessionGoal { get; set; }

    /// <summary>Whether /pause-memory has switched memory off for this session.</summary>
    public bool MemoryPaused { get; set; }

    /// <summary>
    /// The built-in tools --restricted removes: the ones that run commands or
    /// code, and WebFetch (the reference's list), unless --tools names them.
    /// </summary>
    internal static readonly string[] RestrictedTools = ["Bash", "PowerShell", "Workflow", "NotebookEdit", "WebFetch"];

    /// <summary>Prompt tokens the next request would re-send — what a model switch costs to re-cache.</summary>
    public long LastContextTokens => _lastContextTokens;

    /// <summary>When the last response landed, which is what decides whether its prompt cache is still warm.</summary>
    public DateTimeOffset? LastUsageAt { get; private set; }

    /// <summary>Instruction files already in this conversation.</summary>
    private readonly HashSet<string> _loadedInstructionPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files tools touched since the last message was composed.</summary>
    private readonly List<string> _instructionTriggers = [];

    /// <summary>
    /// Records a file a read or an edit touched, so the next message can carry
    /// whatever instructions govern its directory.
    /// </summary>
    private void TrackInstructionTrigger(ToolExecutionStarted started)
    {
        // Only a read: the reference pushes this trigger from the Read tool's
        // own body (its f2n) and from no other tool.
        if (started.ToolName != "Read")
        {
            return;
        }

        if (started.ArgumentsJson is not { Length: > 0 } argumentsJson)
        {
            return;
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(argumentsJson)
                is not System.Text.Json.Nodes.JsonObject args)
            {
                return;
            }

            var path = Core.Tools.JsonArgs.GetString(args, "file_path")
                ?? Core.Tools.JsonArgs.GetString(args, "notebook_path");
            if (path is { Length: > 0 } && !_instructionTriggers.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _instructionTriggers.Add(path);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
    }

    private IReadOnlyList<string> DrainInstructionTriggers()
    {
        if (_instructionTriggers.Count == 0)
        {
            return [];
        }

        var drained = _instructionTriggers.ToArray();
        _instructionTriggers.Clear();
        return drained;
    }

    public async Task<CliTurnResult> RunAsync(
        Core.Sessions.Session session,
        string prompt,
        CliTurnEvents events,
        CancellationToken cancellationToken,
        ChatMessage? inputMessage = null)
    {
        var cwd = session.WorkingDirectory;
        DiagnosticLog.Write("CLI: assembling turn");
        var primaryAgent = CliAgents.Primary(services, options, cwd);
        if (!ModelSelected && primaryAgent?.Model is { Length: > 0 } agentModel)
            session.ModelId = services.ResolveModel(agentModel).ModelId;
        ModelSelected = true;
        bool isFirst = session.Messages.Count == 0;
        var mention = FileMentions.BuildUserMessage(prompt, cwd);
        var userMessage = SystemReminders.UserMessage(inputMessage ?? mention.Message);
        // A new prompt re-arms the token budget (the reference's reanchorTaskBudget).
        if (inputMessage?.IsMeta != true) _totalTokens.ReanchorTaskBudget(_lastContextTokens);
        var harnessNotices = new List<string>();

        // The first message carries the reference's context reminder — project
        // instructions, the memory index and the date. gitStatus is not here:
        // the reference puts it in the system prompt, and so does the factory.
        var instructionScope = Core.Agent.ProjectInstructions.ScopeFor(cwd, [.. session.AdditionalDirectories]);
        if (isFirst)
        {
            // The CLI has no /pause-memory, so the index always rides the reminder.
            var indexDirectory = Core.Memory.ProjectMemory.DirectoryFor(services.App.Paths.MemoryRoot, cwd);
            var eager = options.Bare || options.SafeMode ? Core.Agent.ProjectInstructions.InstructionSet.Empty
                : Core.Agent.ProjectInstructions.LoadAll(instructionScope);
            if (options.SettingSources is not null)
                eager = new Core.Agent.ProjectInstructions.InstructionSet([.. eager.Files.Where(file =>
                    file.Type == InstructionMemoryType.Managed || services.CliSettings.Sources.Contains(file.Type.ToString().ToLowerInvariant()))]);
            foreach (var file in eager.Files)
            {
                _loadedInstructionPaths.Add(file.FilePath);
                _instructionTokens[file.FilePath] = ContextBreakdown.EstimateTokens(file.Content);
            }

            var context = SystemReminders.ContextReminder(
                eager,
                options.Bare || options.SafeMode ? null : System.IO.Path.Combine(indexDirectory, "MEMORY.md"),
                options.Bare || options.SafeMode ? null : Core.Memory.ProjectMemory.ReadIndexForPrompt(indexDirectory),
                DateOnly.FromDateTime(DateTime.Now),
                SystemReminders.UserEmail(cwd));
            if (context is not null)
            {
                userMessage = SystemReminders.Attach(userMessage, context);
            }
        }

        // Instruction files below the working directory, and rules whose paths:
        // frontmatter matched, join the message after the tools that touched
        // them - the same queue the reference drains when it composes one.
        foreach (var trigger in DrainInstructionTriggers())
        {
            if (options.Bare || options.SafeMode) break;
            foreach (var file in Core.Agent.ProjectInstructions.LoadForTouchedFile(
                         instructionScope, trigger, _loadedInstructionPaths))
            {
                if (options.SettingSources is not null && file.Type != InstructionMemoryType.Managed &&
                    !services.CliSettings.Sources.Contains(file.Type.ToString().ToLowerInvariant())) continue;
                if (_loadedInstructionPaths.Add(file.FilePath))
                {
                    _instructionTokens[file.FilePath] = ContextBreakdown.EstimateTokens(file.Content);
                    userMessage = SystemReminders.Attach(
                        userMessage, SystemReminders.NestedInstructions(file.FilePath, file.Content));
                }
            }
        }

        SkillState.CurrentTurnTypedText = prompt;
        DiagnosticLog.Write("CLI: instruction context ready");
        if (SkillState.PendingInvokedSkillsReminder && SkillState.Tracker.HasAny)
        {
            SkillState.PendingInvokedSkillsReminder = false;
            userMessage = SystemReminders.Attach(userMessage, SystemReminders.InvokedSkills(
                Core.Customization.SkillInvocation.BuildInvokedSkillsReminder(SkillState.Tracker.Invoked())));
        }

        // The skill listing rides the harness system message added after this
        // one, not the user turn — the shape the reference sends.
        var skillListingBody = options.DisableSlashCommands ? null : BuildSkillListingBody(session);

        if (gate.Mode == Core.Permissions.PermissionMode.Plan)
        {
            // The plan workflow is a harness section: plain in a lean model's
            // system turn, a reminder block on a classic model's message.
            var planFile = PlanModeTools.PlanFilePath(cwd, session.Id);
            var profile = PromptModelProfile.For(services.Factory.ResolveModel(session)?.ModelId ?? "");
            harnessNotices.Add(_planWorkflowSent
                ? PlanModePrompts.Sparse(planFile, options.PlanModeInstructions is { Length: > 0 })
                : PlanModePrompts.Full(
                    planFile,
                    planExists: System.IO.File.Exists(planFile),
                    withAgents: profile.PlanWorkflowUsesAgents,
                    customInstructions: options.PlanModeInstructions));
            _planWorkflowSent = true;
        }
        else
        {
            _planWorkflowSent = false;
        }

        if (options.Brief)
        {
            userMessage = SystemReminders.Attach(userMessage, SystemReminders.Brief);
        }

        if (EffortLevels.HasUltrathinkKeyword(prompt))
        {
            userMessage = SystemReminders.Attach(userMessage, SystemReminders.Ultrathink);
        }

        if (EffortLevels.HasUltracodeKeyword(prompt))
        {
            userMessage = SystemReminders.Attach(userMessage, SystemReminders.UltracodeKeyword);
        }

        // /pause-memory takes recall out of the turn, as it does on the desktop.
        var memoryDirectory = Core.Memory.ProjectMemory.DirectoryFor(services.App.Paths.MemoryRoot, cwd);
        if (!MemoryPaused && !options.Bare && !options.SafeMode && MemoryRecall.BuildReminder(memoryDirectory, prompt) is { } recalled)
        {
            userMessage = SystemReminders.Attach(userMessage, recalled);
        }

        session.Messages.Add(userMessage);
        DiagnosticLog.Write("CLI: message context ready");
        if (!services.Customizations.DisableLsp) await IdeServices.AddSelectionAsync(session, cancellationToken);
        DiagnosticLog.Write("CLI: editor context ready");
        if (isFirst && options.SessionName is not { Length: > 0 })
        {
            session.Title = MakeTitle(prompt);
        }

        // The CLI shares the app's board and team files, so a session started
        // here and reopened in the app finds the same tasks and teammates.
        _tasks ??= new JarvisCode.Core.Agent.TaskBoard(
            System.IO.Path.Combine(services.App.Paths.TasksDirectory, session.Id + ".json"));
        _teams ??= new JarvisCode.Core.Agent.TeamStore(services.App.Paths.TeamsDirectory);

        var turnCustomizations = services.Customizations;
        if (options.Bare && !options.DisableSlashCommands && prompt.StartsWith('/'))
        {
            var name = prompt[1..].Split(' ', 2)[0];
            var available = (turnCustomizations with { DisableAutomaticDiscovery = false })
                .ResolveSkills(cwd, services.App.Paths, services.App.UiSettings.Current);
            turnCustomizations = turnCustomizations with
            { Skills = [.. available.Where(skill => skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase))] };
        }
        var setup = services.Factory.CreateForCode(
            session, gate, prompt,
            todoSink: null,
            subagentActivity: null,
            // The tools the desktop joins into every Code turn that need no
            // window: the routine store and the worktrees (the reference's -p
            // tool list carries CronCreate/CronList/CronDelete and
            // EnterWorktree/ExitWorktree). ListAgents, ReportFindings and
            // ScheduleWakeup render into the desktop's panes and stay there.
            extraTools:
            [
                .. CronTools.Create(new JarvisCode.Core.Routines.RoutineStore(services.App.Paths.RoutinesFile)),
                .. WorktreeTools.Create(() => session.WorkingDirectory, path => session.WorkingDirectory = path),
                .. services.ChromeTools(),
                .. ExtraTools,
            ],
            lastContextTokens: _lastContextTokens,
            askUser: AskUser,
            planApproval: PlanApproval,
            workers: Workers,
            subagentEvents: events.SubagentEvent,
            memoryPaused: MemoryPaused || options.Bare || options.SafeMode,
            sessionGoal: SessionGoal,
            skillState: SkillState,
            tasks: _tasks,
            teams: _teams,
            planApprovals: _planApprovals,
            totalTokens: _totalTokens,
            nonInteractive: options.Print,
            systemPromptSnapshot: options.SystemPromptSnapshot,
            customizations: turnCustomizations);
        if (setup is null)
        {
            throw new CliError(
                "Error: No model is configured. Add an API key and pick a default model in Settings, " +
                "or pass --model/--settings.");
        }

        setup = ApplyOverrides(setup);
        if (services.CliSettings.ExplicitHooks.Count > 0)
        {
            var overlayHooks = (setup.TurnHooks ?? new Core.Hooks.HookRunner([], cwd)).WithExtra(services.CliSettings.ExplicitHooks);
            var toolContext = setup.Context.ToolContext;
            if (toolContext.Subagents is { } subagents) toolContext = toolContext with { Subagents = subagents with { Hooks = overlayHooks } };
            setup = setup with { TurnHooks = overlayHooks, Context = setup.Context with { Hooks = overlayHooks, ToolContext = toolContext } };
            gate.PermissionRequestHookAsync = overlayHooks.Has(Core.Hooks.HookEvent.PermissionRequest)
                ? (tool, input, risk, token) => overlayHooks.RunPermissionRequestAsync(tool, input, risk, token) : null;
        }
        setup = CliAgents.ApplyPrimary(setup, primaryAgent, options.Print ? options.MaxTurns : null);
        if (ExcludeDynamicSections && options.SystemPrompt is null)
        {
            var relocated = CliPromptSections.Split(setup.Context.SystemPrompt);
            setup = setup with { Context = setup.Context with { SystemPrompt = relocated.Static } };
            if (relocated.Dynamic.Length > 0 && relocated.Dynamic != _relocatedEnvironment)
            {
                var target = _relocatedEnvironment is null
                    ? session.Messages.FindIndex(message => message.Role == Role.User && !message.HarnessSystemTurn)
                    : session.Messages.Count - 1;
                if (target >= 0) session.Messages[target] = SystemReminders.Attach(session.Messages[target],
                    "# Session environment\n" + relocated.Dynamic);
                _relocatedEnvironment = relocated.Dynamic;
            }
        }
        setup = TransformSetup?.Invoke(setup) ?? setup;
        if (!string.IsNullOrEmpty(options.Effort) &&
            !Core.Providers.ProviderCapabilities.For(setup.Context.Provider).SupportsThinkingEffort)
            throw new CliError("--effort is unavailable for this provider. Choose a thinking model in the ChatGPT browser.");
        LastHooks = setup.TurnHooks;
        LastSetup = setup;
        LastMessages = session.Messages.ToArray();
        events.SetupPrepared?.Invoke(setup);

        if (setup.TurnHooks is { } hooks)
        {
            hooks.Diagnostics ??= text => Console.Error.WriteLine(text);
            hooks.ExecutionObserved = events.HookObserved;
            if (!_sessionStartRan && hooks.Has(Core.Hooks.HookEvent.SessionStart))
            {
                var source = options.Continue || options.Resume ? "resume" : "startup";
                try
                {
                    if (await hooks.RunSessionStartAsync(session.Id, CancellationToken.None, source) is { } startContext)
                    {
                        session.Messages[^1] = SystemReminders.AttachSessionStartContext(
                            session.Messages[^1], startContext, source);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    events.Notice?.Invoke($"session_start hook failed: {ex.Message}");
                }
            }

            _sessionStartRan = true;

            var submit = await hooks.RunUserPromptSubmitAsync(prompt, CancellationToken.None);
            if (!submit.Allowed)
            {
                session.Messages.RemoveAt(session.Messages.Count - 1);
                throw new CliError($"Prompt blocked by a user_prompt_submit hook: {submit.BlockReason}");
            }

            if (submit.AdditionalContext is { } extraContext)
            {
                session.Messages[^1] = SystemReminders.AttachPromptHookContext(session.Messages[^1], extraContext);
            }
        }

        // Last, once the hooks have finished with the user message: the roster,
        // the skill listing and the notices, as a system turn or as reminders on
        // the message, whichever this model takes.
        PlaceHarnessSections(session, setup, skillListingBody, harnessNotices);

        var reason = TurnEndReason.Completed;
        var canRetry = true;
        string? detail = null;
        var totalUsage = Usage.Zero;
        var lastCall = Usage.Zero;
        int apiCalls = 0;
        int denials = 0;
        var finalText = new System.Text.StringBuilder();
        var currentText = new System.Text.StringBuilder();
        var hasPreview = false;
        void ClearPreview()
        {
            if (!hasPreview) return;
            hasPreview = false;
            events.TextPreview?.Invoke("");
        }

        int stopBlocks = 0;
        try
        {
        while (true)
        {
        var previousUsage = totalUsage;
        await foreach (var evt in services.App.Orchestrator.RunTurnAsync(setup.Context, cancellationToken))
        {
            switch (evt)
            {
                case AssistantTextPreviewed preview:
                    hasPreview = preview.Text.Length > 0;
                    events.TextPreview?.Invoke(preview.Text);
                    break;
                case AssistantThinkingDelta thinking:
                    events.ThinkingDelta?.Invoke(thinking.Delta);
                    break;
                case AssistantTextDelta delta:
                    ClearPreview();
                    currentText.Append(delta.Delta);
                    events.TextDelta?.Invoke(delta.Delta);
                    break;
                case AssistantCitationDelta citation:
                    if (Core.Models.CitationMarkdown.Link(citation.Citation) is { Length: > 0 } link)
                        events.TextDelta?.Invoke(link);
                    break;
                case AssistantMessageCompleted completed:
                    ClearPreview();
                    var visibleText = options.JsonSchema is null
                        ? completed.Message.GetDisplayText() : completed.Message.GetText();
                    if (visibleText.Length > 0)
                    {
                        finalText.Clear();
                        finalText.Append(visibleText);
                    }
                    currentText.Clear();

                    events.AssistantMessageCompleted?.Invoke(completed.Message);
                    break;
                case ToolExecutionStarted started:
                    TrackInstructionTrigger(started);
                    events.ToolStarted?.Invoke(started);
                    break;
                case ToolExecutionCompleted completedTool:
                    events.ToolCompleted?.Invoke(completedTool);
                    break;
                case ToolExecutionDenied denied:
                    denials++;
                    events.ToolDenied?.Invoke(denied);
                    break;
                case UsageReported usage:
                    apiCalls++;
                    totalUsage = previousUsage.Add(usage.Total);
                    lastCall = usage.LastCall;
                    _lastContextTokens = usage.LastCall.TotalInputTokens + usage.LastCall.OutputTokens;
                    _totalTokens.Observe(usage.LastCall);
                    LastUsageAt = DateTimeOffset.Now;
                    events.UsageReported?.Invoke(totalUsage, usage.LastCall);
                    break;
                // A microcompact boundary is silent in the reference, which
                // renders the marker as nothing at all.
                case ConversationMicrocompacted:
                    break;

                case ConversationCompacted compacted:
                    session.ArchivedMessages.AddRange(compacted.Result.Archived);
                    _totalTokens.RollOverContext(_lastContextTokens);
                    _lastContextTokens = 0;
                    SkillState.Tracker.MarkAllCompacted();
                    SkillState.PendingInvokedSkillsReminder = SkillState.Tracker.HasAny;
                    events.Notice?.Invoke("Compacted session");
                    break;
                case TurnCompleted turnCompleted:
                    ClearPreview();
                    reason = turnCompleted.Reason;
                    detail = turnCompleted.Detail;
                    canRetry = turnCompleted.CanRetry;
                    break;
            }
        }

        if (reason != TurnEndReason.Completed || setup.TurnHooks is not { } stopHooks || !stopHooks.Has(Core.Hooks.HookEvent.Stop))
            break;
        var outcome = await stopHooks.RunStopHooksAsync(Core.Hooks.HookEvent.Stop, stopBlocks > 0, cancellationToken);
        if (outcome.Decision.StopImmediately)
        { reason = TurnEndReason.HookStopped; detail = outcome.Decision.BlockReason; break; }
        if (outcome.Decision.Allowed || outcome.Impossible) break;
        stopBlocks++;
        var cap = int.TryParse(Environment.GetEnvironmentVariable(Core.Hooks.PromptHooks.StopBlockCapEnvironmentVariable), out var overrideCap)
            && overrideCap > 0 ? overrideCap : Core.Hooks.PromptHooks.StopBlockCap;
        if (stopBlocks >= cap)
        {
            reason = TurnEndReason.HookStopped;
            detail = Core.Hooks.PromptHooks.StopBlockCapReached(stopBlocks);
            events.Notice?.Invoke(detail);
            break;
        }
        if (options.Print && options.MaxTurns is { } maximum && apiCalls >= maximum)
        {
            reason = TurnEndReason.MaxIterationsReached;
            detail = $"Stopped after {maximum} turns.";
            break;
        }
        session.Messages.Add(SystemReminders.HarnessMessage(outcome.Decision.BlockReason ?? "The Stop hook requires more work."));
        }
        }
        finally { ClearPreview(); }

        if (setup.TurnHooks is { } finishedHooks && TurnEndReasons.IsError(reason) &&
            finishedHooks.Has(Core.Hooks.HookEvent.StopFailure))
            await finishedHooks.RunEventAsync(Core.Hooks.HookEvent.StopFailure, new System.Text.Json.Nodes.JsonObject
            { ["reason"] = TurnEndReasons.WireName(reason), ["error"] = detail }, cancellationToken);
        session.UpdatedAt = DateTimeOffset.Now;
        LastMessages = session.Messages.ToArray();
        if (!options.NoSessionPersistence)
        {
            await services.App.Sessions.SaveAsync(session, CancellationToken.None);
        }

        return new CliTurnResult(
            reason, detail, totalUsage, lastCall, apiCalls, finalText.ToString(), denials) { CanRetry = canRetry };
    }

    /// <summary>--system-prompt/--append-system-prompt/--tools/--autocompact land on the built setup.</summary>
    private TurnSetup ApplyOverrides(TurnSetup setup)
    {
        var context = setup.Context;
        if (options.SystemPrompt is { } replaced)
        {
            context = context with { SystemPrompt = replaced };
        }

        if (options.AppendSystemPrompt is { Length: > 0 } appended)
        {
            context = context with { SystemPrompt = context.SystemPrompt + "\n\n" + appended };
        }
        if (options.Betas.Count > 0)
        {
            if (options.Betas.Any(beta => beta.Any(character => char.IsControl(character) || character == ',')))
                throw new CliError("Beta names must not contain control characters or commas.");
            context = context with { ExtraHeaders = [.. context.ExtraHeaders,
                new KeyValuePair<string, string>("anthropic-beta", string.Join(',', options.Betas))] };
        }

        if (options.HasToolsFilter &&
            ToolNames.BuildToolFilter(options.Tools, options.HasToolsFilter) is { } filter)
        {
            // Explicitly selected deferred tools must advertise their schemas;
            // filtering only the advertised set can also remove ToolSearch.
            var selected = new ToolRegistry(filter.Select(context.Tools.Find).OfType<ITool>());
            var toolContext = context.ToolContext;
            if (toolContext.Subagents is { } subagents) toolContext = toolContext with
                { Subagents = subagents with { ParentTools = selected } };
            context = context with { Tools = selected, ToolContext = toolContext };
            setup = setup with { Deferred = null };
        }

        if (options.Autocompact is { Length: > 0 } autocompact &&
            context.AutoCompact is { } auto &&
            Core.Agent.ContextWindows.TryParseWindow(autocompact, out int? window))
        {
            // "auto" parses to no window at all, which is the reference's way of
            // saying the model's own window decides.
            context = context with { AutoCompact = auto with { ConfiguredWindow = window } };
        }

        if (SessionEffortName is { } sessionEffort)
        {
            context = context with { ThinkingEffort = EffortLevels.ResolveEffort(sessionEffort) };
        }

        if (options.Restricted)
        {
            // --restricted: the tools that run commands or code, and WebFetch, are
            // removed unless --tools names them; settings-file hooks do not run.
            var keep = new HashSet<string>(context.Tools.All.Select(static t => t.Name), StringComparer.Ordinal);
            foreach (var name in RestrictedTools)
            {
                if (!options.Tools.Contains(name, StringComparer.Ordinal))
                {
                    keep.Remove(name);
                }
            }

            context = context with { Tools = new FilteredRegistry(context.Tools, keep), Hooks = null };
            setup = setup with { TurnHooks = null };
        }

        // --max-turns and the truncated-response resume are print-only in the
        // reference; an interactive REPL turn runs uncapped like the desktop's.
        if (options.Print)
        {
            context = context with
            {
                IsNonInteractiveSession = true,
                MaxIterations = options.MaxTurns,
            };
        }

        if (options.AllowedTools.Count > 0)
        {
            gate.AddSessionRuleLines(ToolNames.ToRuleLines(options.AllowedTools, "allow"));
        }

        if (options.DisallowedTools.Count > 0)
        {
            gate.AddSessionRuleLines(ToolNames.ToRuleLines(options.DisallowedTools, "deny"));
        }

        return setup with { Context = context };
    }

    private static bool TryParseTokens(string value, out long tokens)
    {
        value = value.Trim().ToLowerInvariant();
        long multiplier = 1;
        if (value.EndsWith('k'))
        {
            multiplier = 1_000;
            value = value[..^1];
        }
        else if (value.EndsWith('m'))
        {
            multiplier = 1_000_000;
            value = value[..^1];
        }

        if (long.TryParse(value, out var parsed) && parsed > 0)
        {
            tokens = parsed * multiplier;
            return true;
        }

        tokens = 0;
        return false;
    }

    /// <summary>
    /// The reference's mid-conversation system turn, behind the user message it
    /// accompanies — the agent-type roster once, then only newly discovered
    /// skills. See <see cref="HarnessSystemMessage"/> for what was measured.
    /// </summary>
    private void PlaceHarnessSections(
        Core.Sessions.Session session, TurnSetup setup, string? skillListingBody, IReadOnlyList<string> tailNotices)
    {
        if (session.Messages.Count == 0)
        {
            return;
        }

        var includeAgentTypes = !SkillState.AnnouncedAgentTypes;
        var customAgents = includeAgentTypes
            ? CliAgents.Load(services, session.WorkingDirectory)
            : [];
        var sections = new List<string>();
        // The reference's deferred_tools_delta leads the harness turn. A headless
        // run is the non-interactive session the needs-authentication paragraph
        // describes, so that paragraph rides here and not on the desktop.
        if (DeferredToolNotice.Build(SkillState, setup.Deferred, services.App.Mcp, nonInteractive: true)
            is { } deferredBlock)
        {
            sections.Add(deferredBlock);
        }

        var configuredInstructions = services.App.Mcp.ServerInstructions;
        var freshInstructions = configuredInstructions.Where(server =>
            !SkillState.AnnouncedMcpInstructionServers.Contains(server.Name)).ToArray();
        foreach (var departed in SkillState.AnnouncedMcpInstructionServers.Where(name =>
                     !configuredInstructions.Any(server => server.Name == name)).ToArray())
            SkillState.AnnouncedMcpInstructionServers.Remove(departed);
        var instructions = SkillState.AnnouncedMcpInstructions && freshInstructions.Length == 0 ? null :
            McpServerInstructions.RenderAll(SkillState.AnnouncedMcpInstructions ? [] : services.ChromeInstructions(setup.Model.ModelId,
                    setup.Deferred is not null && setup.Context.Tools.Find("ToolSearch") is not null),
                freshInstructions);
        foreach (var server in freshInstructions) SkillState.AnnouncedMcpInstructionServers.Add(server.Name);
        SkillState.AnnouncedMcpInstructions = true;
        sections.AddRange(HarnessSystemMessage.Sections(
            customAgents, skillListingBody, includeAgentTypes, instructions, tailNotices, setup.Roster));
        if (_totalTokens.Enabled && _totalTokens.AfterUserTurn &&
            _totalTokens.RenderLive(setup.Model.MaxContextTokens) is { } tokens)
        {
            sections.Add(tokens);
        }

        SkillState.AnnouncedAgentTypes = true;
        var (userMessage, body) = HarnessTurnComposer.Place(setup.Model.ModelId, session.Messages[^1], sections);
        if (body is not null)
        {
            session.Messages.Add(SystemReminders.HarnessSystemMessage(body));
        }
        else
        {
            session.Messages[^1] = userMessage;
        }
    }

    private string? BuildSkillListingBody(Core.Sessions.Session session)
    {
        try
        {
            var ui = services.App.UiSettings.Current;
            var all = services.Customizations.ResolveSkills(session.WorkingDirectory, services.App.Paths, ui);
            var enabled = SkillCatalog.Enabled(all, ui);
            var forListing = SkillCatalog.ForListing(enabled, ui, SkillState);
            var fresh = forListing.Where(s => !SkillState.AnnouncedListingNames.Contains(s.Name)).ToList();
            if (fresh.Count == 0)
            {
                return null;
            }

            foreach (var skill in fresh)
            {
                SkillState.AnnouncedListingNames.Add(skill.Name);
            }

            var contextTokens = services.Factory.ResolveModel(session)?.MaxContextTokens ?? 200_000;
            var budget = Math.Max(
                1, (int)(contextTokens * 4L * Core.Customization.SkillInvocation.ListingBudgetFraction));
            var body = Core.Customization.SkillInvocation.BuildListing(
                fresh, SkillCatalog.UsageScores(ui), budget);
            return body.Length == 0 ? null : body;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string MakeTitle(string prompt)
    {
        var line = prompt.ReplaceLineEndings(" ").Trim();
        return line.Length == 0 ? "New session" : line.Length <= 60 ? line : line[..60];
    }
}

/// <summary>--tools: the full registry filtered down to the kept names.</summary>
internal static class RestrictedMode
{
    /// <summary>The reference's refusal of bypass under --restricted.</summary>
    public const string BypassRefused = "bypassPermissions not supported in restricted mode";
}

internal sealed class FilteredRegistry(IToolRegistry inner, ISet<string> keep) : IToolRegistry
{
    public IReadOnlyList<ITool> All => [.. inner.All.Where(t => keep.Contains(t.Name))];

    public ITool? Find(string name) => keep.Contains(name) ? inner.Find(name) : null;
}
