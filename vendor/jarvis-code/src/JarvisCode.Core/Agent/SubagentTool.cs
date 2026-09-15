using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Agent;

/// <summary>
/// Lets the model launch an isolated subagent: a nested agent loop with its own
/// empty context and a filtered tool set, returning only the final report to the
/// caller. Read-only itself, so the orchestrator runs several subagents from one
/// assistant turn concurrently; mutating tools *inside* a subagent still go
/// through the shared permission gate.
/// </summary>
public sealed class SubagentTool(AgentOrchestrator orchestrator) : ITool
{
    public const string ToolName = "Agent";

    // The reference's own spellings, which a capture shows on the wire. The
    // names this harness used to send (explore / general / plan) still resolve:
    // a stored session replays its agent_type, and a model that learned the old
    // names from an older listing must not get an error for them.
    public const string ExploreAgentType = "Explore";
    public const string GeneralAgentType = "general-purpose";
    public const string PlanAgentType = "Plan";

    /// <summary>The reference's catch-all agent (its roster: "FleetView's default when no agent name is typed").</summary>
    public const string ClaudeAgentType = "claude";

    /// <summary>The reference's status-line configurator: Read and Edit only, on a Sonnet model.</summary>
    public const string StatuslineSetupAgentType = "statusline-setup";

    /// <summary>The reference's documentation guide: the five research tools, on a Haiku model.</summary>
    public const string ClaudeCodeGuideAgentType = "claude-code-guide";

    /// <summary>The reference's own environment variables for the subagent model.</summary>
    public const string SubagentModelVariable = "CLAUDE_CODE_SUBAGENT_MODEL";
    public const string SubagentModelForceVariable = "CLAUDE_CODE_SUBAGENT_MODEL_FORCE";

    /// <summary>Every built-in type, in the reference's roster order.</summary>
    public static readonly IReadOnlyList<string> BuiltInAgentTypes =
    [
        ClaudeAgentType, ClaudeCodeGuideAgentType, ExploreAgentType, GeneralAgentType, PlanAgentType,
        StatuslineSetupAgentType,
    ];

    public static bool IsBuiltInAgentType(string agentType) =>
        BuiltInAgentTypes.Contains(agentType, StringComparer.Ordinal);

    /// <summary>Explore, Plan and the guide read; everything else may write.</summary>
    public static bool IsReadOnlyBuiltIn(string agentType) =>
        agentType is ExploreAgentType or PlanAgentType or ClaudeCodeGuideAgentType;

    /// <summary>
    /// The fixed tool set a built-in definition grants, or null for a type that
    /// takes the session's registry (whole, or its read-only half).
    /// </summary>
    public static IReadOnlyList<string>? BuiltInToolNames(string agentType) => agentType switch
    {
        StatuslineSetupAgentType => ["Read", "Edit"],
        ClaudeCodeGuideAgentType => ["Glob", "Grep", "Read", "WebFetch", "WebSearch"],
        _ => null,
    };

    /// <summary>The model alias a built-in definition names, or null to inherit.</summary>
    public static string? BuiltInModelAlias(string agentType) => agentType switch
    {
        StatuslineSetupAgentType => "sonnet",
        ClaudeCodeGuideAgentType => "haiku",
        _ => null,
    };

    private static bool IsFamilyAlias(string name) =>
        name.Trim().ToLowerInvariant() is "sonnet" or "opus" or "haiku" or "fable" or "mythos";

    /// <summary>
    /// The reference's precedence since CLI 2.1.251: an explicit per-spawn model
    /// wins, then the agent definition's, then <c>CLAUDE_CODE_SUBAGENT_MODEL</c>
    /// as the default; and since 2.1.257 <c>CLAUDE_CODE_SUBAGENT_MODEL_FORCE</c>
    /// applies the default (or the parent's model, when none is set) to every
    /// subagent regardless. Null means "the parent's".
    /// </summary>
    internal static string? ResolveModelRequest(
        string? explicitModel, string? definitionModel, string? defaultModel, string? force)
    {
        var forced = force is { Length: > 0 } && force.Trim().ToLowerInvariant() is not ("0" or "false" or "no" or "off");
        if (forced)
            return string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim();
        if (explicitModel is { Length: > 0 })
            return explicitModel;
        if (definitionModel is { Length: > 0 })
            return definitionModel;
        return string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim();
    }

    /// <summary>The reference's parameter is <c>subagent_type</c>; the pre-parity spelling is still read for stored sessions.</summary>
    private static string? RequestedAgentType(JsonObject arguments) =>
        JsonArgs.GetString(arguments, "subagent_type") ?? JsonArgs.GetString(arguments, "agent_type");

    /// <summary>
    /// The built-in name this value means, whatever its casing, or the value
    /// trimmed when it names no built-in (a custom agent resolves by its own
    /// name, case-insensitively, further down).
    /// </summary>
    public static string CanonicalAgentType(string? agentType)
    {
        var raw = (agentType ?? "").Trim();
        if (raw.Length == 0)
        {
            // The reference's doc: "If omitted, the general-purpose agent is used."
            return GeneralAgentType;
        }

        // "general" was this harness's own spelling before the rename.
        if (raw.Equals("general", StringComparison.OrdinalIgnoreCase))
        {
            return GeneralAgentType;
        }

        foreach (var builtIn in BuiltInAgentTypes)
        {
            if (raw.Equals(builtIn, StringComparison.OrdinalIgnoreCase))
            {
                return builtIn;
            }
        }

        return raw;
    }

    /// <summary>The plan agent's role, modeled on the reference Plan (architect) agent.</summary>
    internal const string PlanRolePrompt =
        "You are a software architect designing an implementation plan for the requested task. Investigate the " +
        "codebase with your read-only tools, then return: (1) a step-by-step implementation plan, (2) the critical " +
        "files and integration points each step touches, with paths, and (3) the architectural trade-offs you " +
        "considered and why you chose this approach. Do not write code or modify anything — your deliverable is " +
        "the plan itself, concrete enough that another engineer could implement it without re-doing the research.";

    /// <summary>
    /// The reference's <c>fork</c> agent is the one built-in type that declares a
    /// cap; Explore, Plan, general-purpose, teammate and workflow-subagent declare
    /// none and run until they are done. A custom agent may declare its own.
    /// </summary>
    public const int ForkAgentMaxTurns = ForkAgent.MaxTurns;
    private const int SubagentMaxToolOutputChars = 30_000;
    private const int PromptPreviewChars = 60;

    public string Name => ToolName;

    public string Description =>
        "Launches an autonomous subagent with a fresh, isolated context to work on a task and returns its final " +
        "report. Use it to keep your own context small (large searches, reading many files) or to fan out " +
        "independent work — multiple Agent calls issued in the same turn run in parallel. " +
        "agent_type 'Explore' has read-only tools (search/read) and is right for questions about the codebase; " +
        "'general-purpose' can also edit files and run shell commands; 'Plan' is a read-only software architect " +
        "that returns a step-by-step implementation plan; projects may define additional custom agent types " +
        "(an unknown type error lists what is available). The subagent only receives your prompt — make it " +
        "self-contained, say exactly what should be investigated or done, and what the report must contain.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("description", SchemaBuilder.String("A short (3-5 word) description of the task")),
            ("prompt", SchemaBuilder.String("The task for the agent to perform")),
            ("subagent_type", SchemaBuilder.String("The type of specialized agent to use for this task")),
            ("model", new JsonObject
            {
                ["description"] =
                    "Optional model override for this agent. Takes precedence over the agent definition's model " +
                    "frontmatter and the configured default subagent model. If omitted, uses the agent " +
                    "definition's model, else the default (inherits from the parent unless a default subagent " +
                    "model is configured). Ignored for subagent_type: \"fork\" — forks always inherit the parent " +
                    "model.",
                ["type"] = "string",
                ["enum"] = new JsonArray([.. ModelAliases.Select(static a => JsonValue.Create(a))]),
            }),
            ("run_in_background", SchemaBuilder.Boolean(
                "Agents run in the background by default; you will be notified when one completes. Set to false " +
                "only when your very next action depends on this agent's result and nothing else could usefully " +
                "happen while it runs — otherwise leave it in the background so the user can hand you other " +
                "work.")),
            ("isolation", new JsonObject
            {
                ["description"] =
                    "Isolation mode. \"worktree\" creates a temporary git worktree so the agent works on an " +
                    "isolated copy of the repo. \"remote\" launches the agent in a remote cloud environment " +
                    "(always runs in background; availability is gated).",
                ["type"] = "string",
                ["enum"] = new JsonArray("worktree", "remote"),
            }),
            ("name", SchemaBuilder.String(
                "Name for the spawned agent. Makes it addressable via SendMessage({to: name}) while running.")),
            ("cwd", SchemaBuilder.String(
                "Absolute path to run the agent in. Overrides the working directory for all filesystem and " +
                "shell operations within this agent. Mutually exclusive with isolation: \"worktree\".")),
        ],
        "description", "prompt");

    /// <summary>
    /// The family aliases the <c>model</c> argument accepts, in the reference's
    /// order (its <c>latest_per_family</c> keys: sonnet, opus, haiku, fable).
    /// </summary>
    public static readonly IReadOnlyList<string> ModelAliases = ["sonnet", "opus", "haiku", "fable"];

    public bool IsReadOnly => true;

    /// <summary>
    /// The reference's <c>async_launched</c> tool result (CLI 2.1.257, at
    /// 188466905), verbatim on this port's worker id. Its last paragraph has two
    /// forms: one naming the JSONL transcript the model must not read, and one
    /// for a launch with no output file.
    /// </summary>
    public static string AsyncLaunchResult(string agentId, string? outputFile)
    {
        var opening =
            "Async agent launched successfully. (This tool result is internal metadata — never quote or paste " +
            "any part of it, including the agentId below, into a user-facing reply.)\n" +
            $"agentId: {agentId} (internal ID - do not mention to user. Use SendMessage with to: '{agentId}', " +
            "summary: '<5-10 word recap>' to continue this agent.)\n" +
            "The agent is working in the background. You will be notified automatically when it completes. You " +
            "know nothing about its results until that notification arrives — do not report, assume, or predict " +
            "them; continue other work or respond to the user in the meantime.";
        var tail = outputFile is null
            ? "In your own words, briefly tell the user what you launched — do not echo this tool result. Agent " +
              "results will arrive in a subsequent message. If the user asks for progress, say the agent is " +
              "still running."
            : "Do not duplicate this agent's work — avoid working with the same files or topics it is using.\n" +
              $"output_file: {outputFile}\n" +
              "Do NOT Read or tail this file via the shell tool — it is the full subagent JSONL transcript and " +
              "reading it will overflow your context. If the user asks for progress, say the agent is still " +
              "running; you'll get a completion notification.";
        return opening + "\n" + tail;
    }

    public string DescribeCall(JsonObject arguments)
    {
        var prompt = JsonArgs.GetString(arguments, "prompt") ?? "?";
        var agentType = RequestedAgentType(arguments) ?? GeneralAgentType;
        var preview = prompt.Length > PromptPreviewChars ? prompt[..PromptPreviewChars] + "…" : prompt;
        return $"Agent({agentType}: {preview})";
    }

    public Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
        RunAsync(arguments, context, effortOverride: null, cancellationToken);

    /// <summary>
    /// The tool's body, with the effort override a workflow's <c>agent()</c> may
    /// set per call (the reference's opts.effort, which its Agent tool has no
    /// parameter for).
    /// </summary>
    internal async Task<ToolResult> RunAsync(
        JsonObject arguments,
        ToolExecutionContext context,
        ThinkingEffort? effortOverride,
        CancellationToken cancellationToken)
    {
        var services = context.Subagents;
        if (services is null)
            return ToolResult.Error("Subagents are not available in this context (nested subagents are not allowed).");

        var prompt = JsonArgs.GetString(arguments, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Error("prompt is required.");

        // A named agent becomes a teammate: addressable by name while it runs,
        // and a member of the session's single implicit team.
        var teammateName = JsonArgs.GetString(arguments, "name");
        if (!string.IsNullOrWhiteSpace(teammateName))
        {
            if (TeamNames.Validate(teammateName) is { } nameError)
                return ToolResult.Error(nameError);
            if (!JsonArgs.GetBool(arguments, "run_in_background"))
            {
                return ToolResult.Error(
                    "A named agent is addressable only while it runs alongside you — spawn it with " +
                    "run_in_background: true, or drop the name for a blocking subagent.");
            }
        }
        else
        {
            teammateName = null;
        }

        var agentType = CanonicalAgentType(RequestedAgentType(arguments));
        // The reference resolves "fork" ahead of every other type: it is not one
        // of the roster's definitions, and the two refusals its JRn raises come
        // before the availability lookup.
        var forkRequested = agentType.Equals(ForkAgent.AgentType, StringComparison.OrdinalIgnoreCase) &&
            ForkAgent.IsAvailable(
                services.ForkEnabled,
                services.CustomAgents.Select(a => a.Name),
                allowedAgentTypes: null);
        if (forkRequested)
        {
            agentType = ForkAgent.AgentType;
        }

        Customization.CustomAgentDefinition? customAgent = null;
        if (!forkRequested && !IsBuiltInAgentType(agentType))
        {
            customAgent = services.CustomAgents.FirstOrDefault(a =>
                a.Name.Equals(agentType, StringComparison.OrdinalIgnoreCase));
            if (customAgent is null)
            {
                // The reference puts `fork` at the head of its own "Available
                // agents" list exactly while the gate admits it, which is the
                // only place its definition surfaces to the model — the roster
                // is built from the active definitions, and fork is not one.
                var offered = ForkAgent.IsAvailable(
                    services.ForkEnabled, services.CustomAgents.Select(a => a.Name), allowedAgentTypes: null)
                    ? new[] { ForkAgent.AgentType }.Concat(BuiltInAgentTypes)
                    : BuiltInAgentTypes;
                var available = string.Join(", ",
                    offered.Concat(services.CustomAgents.Select(a => a.Name)));
                return ToolResult.Error($"Unknown subagent_type '{agentType}'. Available: {available}.");
            }
        }
        bool readOnlyTools = customAgent?.ReadOnlyTools ?? IsReadOnlyBuiltIn(agentType);
        // A built-in type with a fixed tool list (statusline-setup: Read and
        // Edit; claude-code-guide: the five read-only research tools) takes
        // exactly that, as the reference's definitions grant.
        var fixedTools = customAgent?.Tools ?? BuiltInToolNames(agentType);
        // While planning, nothing may modify state — even through a subagent.
        // The live check matters in both directions: an approved exit frees
        // subagents mid-turn, and EnterPlanMode restricts them mid-turn.
        if (context.IsPlanModeActive)
            readOnlyTools = true;

        // Isolation and cwd: a worktree gives the agent its own checkout, and
        // the two are mutually exclusive, as the reference states.
        var isolation = JsonArgs.GetString(arguments, "isolation");
        var requestedCwd = JsonArgs.GetString(arguments, "cwd");
        // The reference's JRn, in its order: a fork cannot go remote (a remote
        // session cannot inherit the context), and a fork inside a fork is
        // refused outright.
        if (forkRequested)
        {
            if (isolation is "remote")
            {
                return ToolResult.Error(ForkAgent.RemoteIsolationRefusal);
            }

            if (context.IsSubagent || ForkAgent.IsForkedConversation(context.ConversationSnapshot?.Invoke()))
            {
                return ToolResult.Error(ForkAgent.RecursiveForkRefusal);
            }
        }

        if (isolation is "remote")
        {
            return ToolResult.Error(
                "isolation: \"remote\" runs the agent in a cloud environment, which this build does not have. " +
                "Drop it to run the agent here, or use isolation: \"worktree\" for an isolated checkout.");
        }

        if (isolation is not (null or "worktree"))
            return ToolResult.Error($"Unknown isolation '{isolation}'. Use \"worktree\".");
        if (isolation == "worktree" && !string.IsNullOrWhiteSpace(requestedCwd))
        {
            return ToolResult.Error(
                "cwd is mutually exclusive with isolation: \"worktree\" — the worktree is the agent's directory.");
        }

        AgentWorktrees.Worktree? worktree = null;
        var agentDirectory = context.WorkingDirectory;
        if (isolation == "worktree")
        {
            worktree = AgentWorktrees.Create(
                context.WorkingDirectory, teammateName ?? agentType, out var worktreeError);
            if (worktree is null)
                return ToolResult.Error(worktreeError ?? "The agent's worktree could not be created.");
            agentDirectory = worktree.Path;
        }
        else if (!string.IsNullOrWhiteSpace(requestedCwd))
        {
            if (!Directory.Exists(requestedCwd))
                return ToolResult.Error($"cwd '{requestedCwd}' does not exist.");
            agentDirectory = requestedCwd;
        }

        ILlmProvider provider = services.ParentProvider;
        string modelId = services.ParentModelId;
        // "the fork inherits your full conversation context and always runs on
        // your model — a `model` override is ignored", which is what the
        // reference's wP.model = "inherit" and its own doc both say.
        var requestedModel = forkRequested ? null : ResolveModelRequest(
            JsonArgs.GetString(arguments, "model"),
            customAgent?.Model ?? BuiltInModelAlias(agentType),
            Environment.GetEnvironmentVariable(SubagentModelVariable),
            Environment.GetEnvironmentVariable(SubagentModelForceVariable));
        if (requestedModel is { Length: > 0 } &&
            !requestedModel.Equals(modelId, StringComparison.OrdinalIgnoreCase))
        {
            // A family alias (sonnet, haiku, …) picks the newest model of that
            // family the user has added; an alias no model answers keeps the
            // parent's, as an unmet default would.
            var model = ModelCatalog.Resolve(services.Models, requestedModel);
            if (model is null && !IsFamilyAlias(requestedModel))
            {
                return ToolResult.Error(
                    $"Unknown model id '{requestedModel}'. Available: {string.Join(", ", services.Models.Select(m => m.ModelId))}.");
            }
            if (model is not null)
            {
                try
                {
                    provider = services.Providers.Get(model.ProviderId);
                }
                catch (ProviderException ex)
                {
                    return ToolResult.Error(ex.Message);
                }
                modelId = model.ModelId;
            }
        }

        // The agent's own token budget, fresh — the reference starts every agent
        // at the full figure — rendered static at the end of its prompt and live
        // after each of its tool-result batches.
        var totalTokens = services.TotalTokensFactory?.Invoke();
        var contextWindow = ModelCatalog.Find(services.Models, modelId)?.MaxContextTokens ?? 0;
        var totalTokensBlock = totalTokens?.RenderStatic(contextWindow);

        // tool_search stays out: the inner registry is a snapshot, so fetching a
        // deferred tool in here could never make it callable.
        var innerTools = new ToolRegistry(services.ParentTools.All.Where(tool =>
            tool.Name != ToolName &&
            tool.Name != ToolSearchTool.ToolName &&
            (customAgent is null || !customAgent.DisallowedTools.Contains(tool.Name, StringComparer.Ordinal)) &&
            (fixedTools is null ? !readOnlyTools || tool.IsReadOnly : fixedTools.Contains(tool.Name))));

        // A host may build this document itself; the reference's is a different
        // shape from this engine's, and its port lives beside the main prompt's.
        // Its role prompts already carry what the two appends below add, so they
        // stay on the built-in path.
        string systemPrompt;
        if (forkRequested && services.ParentSystemPrompt is { Length: > 0 } parentPrompt)
        {
            // A fork inherits the conversation, so it inherits the prompt that
            // conversation was written under — which is what makes it share the
            // parent's cache rather than open a second prefix.
            systemPrompt = parentPrompt;
        }
        else if (services.SystemPrompt is { } buildPrompt)
        {
            systemPrompt = buildPrompt(new SubagentPromptRequest(
                customAgent?.Name ?? agentType,
                context.WorkingDirectory,
                modelId,
                context.AdditionalDirectories,
                customAgent?.SystemPrompt,
                totalTokensBlock));
        }
        else
        {
            systemPrompt = SystemPromptBuilder.BuildForSubagent(
                context.WorkingDirectory,
                readOnlyTools ? ExploreAgentType : GeneralAgentType,
                ProjectInstructions.LoadAll(context.WorkingDirectory),
                context.AdditionalDirectories,
                services.Skills);
            if (customAgent is not null)
            {
                systemPrompt += $"\n# Your role: {customAgent.Name}\n{customAgent.SystemPrompt}\n";
            }
            else if (agentType == PlanAgentType)
            {
                systemPrompt += $"\n# Your role: software architect\n{PlanRolePrompt}\n";
            }

            if (totalTokensBlock is not null)
            {
                systemPrompt += "\n\n" + totalTokensBlock;
            }
        }

        // A teammate carries the reference's Team Coordination reminder and joins
        // the team file, so its peers can be discovered by name.
        TeamContext? teamContext = null;
        string? backendRefusal = null;
        Action? shutdownSelf = null;
        if (teammateName is not null && services.Teams is { } teamStore && services.TeamName is { } teamName)
        {
            // The session's teammateMode decides the backend; one that cannot run
            // here falls back to in-process and says why, as the reference does.
            var backend = TeammateBackends.Resolve(services.TeammateMode, out backendRefusal);
            var agentId = TeamNames.NewAgentId(teammateName);
            teamStore.EnsureTeam(teamName);
            teamStore.AddMember(teamName, new TeamMember
            {
                AgentId = agentId,
                Name = teammateName,
                Mode = TeammateBackends.Name(backend),
                WorktreePath = worktree?.Path,
                WorktreeBranch = worktree?.Branch,
                WorktreeBaseCommit = worktree?.BaseCommit,
            });
            var configPath = teamStore.ConfigPathFor(teamName);
            teamContext = new TeamContext(teamName, teammateName, agentId, IsLeader: false, configPath);
            systemPrompt += "\n\n" + TeamPrompts.Coordination(
                teammateName, configPath, context.Tasks is null ? null : "the session task board");
            if (context.TeamMemoryDirectory is { Length: > 0 } teamMemory)
            {
                systemPrompt += "\n\n# Team memory (shared with your teammates)\n" +
                    (context.MemoryDirectory is { Length: > 0 } privateMemory
                        ? TeamPrompts.MemoryDirectories(privateMemory, teamMemory)
                        : TeamPrompts.TeamMemoryOnly(teamMemory));
            }
        }

        if (services.Hooks is Hooks.HookRunner hookRunner && hookRunner.Has(Hooks.HookEvent.SubagentStart))
        {
            await hookRunner.RunEventAsync(Hooks.HookEvent.SubagentStart, new JsonObject
            {
                ["agent_type"] = agentType,
                ["prompt"] = prompt.Length > 500 ? prompt[..500] : prompt,
                ["background"] = JsonArgs.GetBool(arguments, "run_in_background"),
            }, cancellationToken);
        }

        // A fork's conversation is the parent's, with a user turn answering the
        // calls that were in flight and carrying the directive; every other agent
        // starts on its prompt alone. A worktree fork is told the paths it
        // inherited belong to the parent's checkout.
        List<ChatMessage> agentMessages;
        if (forkRequested)
        {
            var directive = worktree is null
                ? prompt
                : prompt + "\n\n" + ForkAgent.WorktreeNotice(context.WorkingDirectory, worktree.Path);
            agentMessages =
            [
                .. ForkAgent.BuildConversation(context.ConversationSnapshot?.Invoke() ?? [], directive),
            ];
        }
        else
        {
            agentMessages = [ChatMessage.FromUserText(prompt)];
        }

        var turnContext = new AgentTurnContext
        {
            ConversationScopeId = $"{context.SessionId}/agent/{context.CallId}/{Guid.NewGuid():N}",
            Provider = provider,
            ModelId = modelId,
            SystemPrompt = systemPrompt,
            // A subagent's cut-off answer is always resumed, with the wording that
            // tells it only the next message reaches its caller.
            IsSubagent = true,
            LeadingSystemBlocks = services.LeadingSystemBlocks?.Invoke(modelId) ?? [],
            // The agent definition's experimental.cacheTtl rides its requests.
            CacheTtl = customAgent?.CacheTtl,
            ToolResultTrailer = totalTokens is null ? null : () => totalTokens.RenderLive(contextWindow),
            Messages = agentMessages,
            Tools = innerTools,
            PermissionGate = services.PermissionGate,
            Hooks = services.Hooks,
            EnableWebSearch = services.EnableWebSearch,
            ThinkingEffort = effortOverride ?? services.ThinkingEffort,
            ProviderOptions = services.ProviderOptionsForModel?.Invoke(modelId)
                ?? new Dictionary<string, string>(),
            ToolContext = context with
            {
                Subagents = null,
                Workers = null,
                CallId = null,
                IsSubagent = true,
                WorkingDirectory = agentDirectory,
                MaxOutputChars = SubagentMaxToolOutputChars,
                Team = teamContext ?? context.Team,
                ReportToLeadAsync = teamContext is null ? context.ReportToLeadAsync : services.TeammateMessage,
                PlanApprovals = context.PlanApprovals,
                // A teammate that approves its own shutdown cancels its worker.
                RequestOwnShutdown = teamContext is null
                    ? context.RequestOwnShutdown
                    : () => shutdownSelf?.Invoke(),
            },
            // Built-in agent types carry no cap, exactly as the reference's
            // definitions do; only a custom agent that asked for one gets one —
            // and `fork`, the one built-in whose definition declares 200.
            MaxIterations = forkRequested ? ForkAgent.MaxTurns : customAgent?.MaxTurns,
        };

        // Hosts that render the subagent's own transcript get the raw inner
        // events, keyed by this Agent call; the agent's own token budget reads
        // the same stream for the context each of its calls reported.
        Action<AgentEvent>? eventSink = null;
        if (context.CallId is { } parentCallId && services.EventReporter is { } reporter)
            eventSink = agentEvent => reporter(parentCallId, agentEvent);
        eventSink?.Invoke(new SubagentModelSelected(modelId));
        if (totalTokens is not null)
        {
            var forward = eventSink;
            eventSink = agentEvent =>
            {
                if (agentEvent is UsageReported usage)
                    totalTokens.Observe(usage.LastCall);
                forward?.Invoke(agentEvent);
            };
        }

        if (JsonArgs.GetBool(arguments, "run_in_background"))
        {
            if (context.Workers is null)
                return ToolResult.Error("Background agents are not available in this context.");
            // The reference titles a background agent's row with the call's own
            // short description, falling back to the head of the prompt.
            var described = JsonArgs.GetString(arguments, "description");
            var preview = !string.IsNullOrWhiteSpace(described)
                ? described
                : prompt.Length > PromptPreviewChars ? prompt[..PromptPreviewChars] + "…" : prompt;
            // The worker's own events feed both the host's nested transcript and
            // the counters its task row shows (tokens, tool uses, last tool).
            var progress = new WorkerProgress();
            // The reference writes the agent's whole JSONL transcript to a file
            // and names it in the launch result (telling the model not to read
            // it); the file is keyed by the call, since the worker id is only
            // known once Start returns.
            var transcriptPath = context.AgentTranscriptDirectory is { Length: > 0 } transcriptDirectory
                ? System.IO.Path.Combine(
                    transcriptDirectory,
                    AgentTranscriptFile.SafeName(context.CallId ?? Guid.NewGuid().ToString("n")) + ".jsonl")
                : null;
            var workerSink = new Action<AgentEvent>(agentEvent =>
            {
                progress.Observe(agentEvent);
                if (transcriptPath is not null)
                    AgentTranscriptFile.Append(transcriptPath, agentEvent);
                eventSink?.Invoke(agentEvent);
            });
            // Bound after Start returns the id: an approved shutdown kills
            // exactly this worker.
            var workers = context.Workers;
            string? workerId = null;
            shutdownSelf = () =>
            {
                if (workerId is not null)
                    workers.Kill(workerId);
            };
            var id = context.Workers.Start(
                agentType, preview, turnContext,
                async (workerContext, workerToken) =>
                {
                    try
                    {
                        return await RunTurnToReportAsync(
                            orchestrator, workerContext, context, report: null, workerToken, workerSink);
                    }
                    finally
                    {
                        if (worktree is not null)
                            AgentWorktrees.RemoveIfUnchanged(worktree);

                        // A teammate that stops working goes idle in the team
                        // file, and the lead's hooks hear about it.
                        if (teamContext is not null && services.Teams is { } store)
                        {
                            store.SetActive(teamContext.TeamName, teamContext.AgentName, isActive: false);
                            context.FireHook?.Invoke(Hooks.HookEvent.TeammateIdle, new JsonObject
                            {
                                ["agent_name"] = teamContext.AgentName,
                                ["agent_id"] = teamContext.AgentId,
                                ["team_name"] = teamContext.TeamName,
                            });
                        }
                    }
                },
                progress,
                context.CallId,
                teammateName);
            workerId = id;
            if (teammateName is not null)
            {
                var note = backendRefusal is null ? "" : $" {backendRefusal} Running in-process instead.";
                return ToolResult.Success(
                    $"Started background agent {id} ({agentType}) as \"{teammateName}\". Address it by name " +
                    $"while it runs — SendMessage({{to: \"{teammateName}\"}}) — and its final report will arrive " +
                    "as a task notification. Do not wait or poll, and never fabricate or predict its results; " +
                    $"continue with other work. Stop it with TaskStop.{note}");
            }

            return ToolResult.Success(AsyncLaunchResult(id, transcriptPath));
        }

        void Report(string activity)
        {
            if (context.CallId is { } callId)
                services.ActivityReporter?.Invoke(callId, activity);
        }

        Report($"{agentType} agent · {modelId}");
        try
        {
            var result = await RunTurnToReportAsync(
                orchestrator, turnContext, context, Report, cancellationToken, eventSink);
            Report("finished");
            return result;
        }
        finally
        {
            // An agent that changed nothing leaves no worktree behind; one that
            // did keeps it, so its work survives.
            if (worktree is not null)
                AgentWorktrees.RemoveIfUnchanged(worktree);
        }
    }

    /// <summary>Runs one subagent turn to completion and folds it into the report result.</summary>
    internal static async Task<ToolResult> RunTurnToReportAsync(
        AgentOrchestrator orchestrator,
        AgentTurnContext turnContext,
        ToolExecutionContext context,
        Action<string>? report,
        CancellationToken cancellationToken,
        Action<AgentEvent>? eventSink = null)
    {
        string reportText = "";
        int toolCallCount = 0;
        TurnCompleted? outcome = null;
        await foreach (var agentEvent in orchestrator.RunTurnAsync(turnContext, cancellationToken))
        {
            eventSink?.Invoke(agentEvent);
            switch (agentEvent)
            {
                case AssistantMessageCompleted completed:
                    var text = completed.Message.GetText();
                    if (text.Length > 0)
                        reportText = text;
                    break;
                case ToolExecutionStarted started:
                    toolCallCount++;
                    report?.Invoke($"[{toolCallCount}] {started.CallDescription}");
                    break;
                case TurnCompleted turnCompleted:
                    outcome = turnCompleted;
                    break;
            }
        }

        switch (outcome?.Reason)
        {
            case TurnEndReason.Cancelled:
            case TurnEndReason.AbortedTools:
                cancellationToken.ThrowIfCancellationRequested();
                return ToolResult.Error("The subagent was interrupted before finishing.");
            case TurnEndReason.Error:
            case TurnEndReason.PromptTooLong:
            case TurnEndReason.MalformedToolUseExhausted:
                return ToolResult.Error(
                    $"Subagent failed: {outcome.Detail ?? "unknown error"}" +
                    (reportText.Length > 0 ? $"\nPartial report before the failure:\n{reportText}" : ""));
            case TurnEndReason.MaxIterationsReached:
                return ToolResult.Success(context.Truncate(
                    $"{reportText}\n\n[Note: the subagent hit its turn limit ({turnContext.MaxIterations} turns); the report above may be incomplete.]",
                    "subagent report"));
        }

        if (reportText.Length == 0)
            return ToolResult.Error("The subagent finished without producing a report.");
        return ToolResult.Success(context.Truncate(reportText, "subagent report"));
    }
}
