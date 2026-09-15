using System.Text.Json.Nodes;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// The reference Skill tool, on our wire name: invokes a skill. Inline skills
/// return "Launching skill: {name}" and inject their rendered instructions into
/// the same user turn (<see cref="ToolResult.FollowUpText"/>); forked skills
/// (context: fork) run as a subagent, in the background by default; coordinator
/// sessions load instructions read-only and execute nothing.
/// </summary>
public sealed class SkillTool(AgentOrchestrator? orchestrator = null) : ITool
{
    public const string ToolName = "Skill";

    public string Name => ToolName;

    public string Description =>
        "Invoke a skill: packaged instructions the user or project set up for a specific kind of task. " +
        "Available skills appear in a system-reminder listing with one-line descriptions; when the task at " +
        "hand is one a listed skill covers, call this tool first and follow the loaded instructions. Some " +
        "skills instead run in a subagent and return the finished result.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("skill", SchemaBuilder.String("The name of a skill from the available-skills list. Do not guess names.")),
            ("args", SchemaBuilder.String("Optional arguments for the skill")),
        ],
        "skill");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"Skill({SkillNameArgument(arguments) ?? "?"})";

    /// <summary>
    /// The reference schema key is "skill" (CLI 2.1.257); the capitalised "Skill" this
    /// port advertised for a while and the pre-parity "name" are still read for old sessions.
    /// </summary>
    private static string? SkillNameArgument(JsonObject arguments) =>
        JsonArgs.GetString(arguments, "skill") ?? JsonArgs.GetString(arguments, "Skill") ?? JsonArgs.GetString(arguments, "name");

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var skills = context.Skills;
        if (skills is null || skills.Count == 0)
            return ToolResult.Error("No skills are available in this session.");

        var name = SkillNameArgument(arguments)?.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Error("skill is required.");
        var args = JsonArgs.GetString(arguments, "args") ?? "";

        var skill = skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            var variants = skills
                .Where(s => name.Equals(s.UnqualifiedName, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Name)
                .ToList();
            if (variants.Count > 0)
            {
                return ToolResult.Error(
                    $"Unknown skill: {name}. Directory-scoped variants exist: {string.Join(", ", variants)} — " +
                    "invoke the variant whose directory contains the files you are working on.");
            }
            var suggestions = Utilities.DidYouMean.Suggest(name, skills.Select(s => s.Name));
            return ToolResult.Error(suggestions.Count > 0
                ? $"Unknown skill: {name}. Did you mean {suggestions[0]}?"
                : $"Unknown skill: {name}.");
        }

        if (context.ForkedSkillName is { } forkedName &&
            forkedName.Equals(skill.Name, StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Error(
                $"Skill {skill.Name} is already executing in this forked context — you are the subagent running " +
                "it. Execute the instructions in the skill body directly instead of re-invoking the Skill tool.");
        }

        if (skill.DisableModelInvocation &&
            !(context.UserTypedSkillThisTurn?.Invoke(skill.Name) ?? false))
        {
            if (context.CoordinatorMode)
            {
                return ToolResult.Error(
                    $"Skill \"/{skill.Name}\" is user-invocable only (disable-model-invocation) and cannot run " +
                    "in coordinator mode: the coordinator does not load skill content, and workers cannot " +
                    "invoke it via the Skill tool.");
            }
            return ToolResult.Error(
                $"Skill {skill.Name} cannot be used with Skill tool due to disable-model-invocation. Ask the " +
                $"user to run /{skill.Name} themselves — it cannot be invoked via the Skill tool. Do not " +
                "replicate this skill's workflow by other means — it is reserved for explicit user invocation.");
        }

        // The reference prompts "Execute skill: {name}" only for skills that
        // declare permission-relevant frontmatter; a plain skill runs silently.
        if (!context.CoordinatorMode && RequiresApproval(skill) &&
            context.ConfirmSkillExecutionAsync is { } confirm &&
            !await confirm(skill.Name, cancellationToken))
        {
            return ToolResult.Error(
                "The user denied this tool call. Ask how they would like to proceed instead of retrying.");
        }

        if (context.CoordinatorMode)
        {
            // Read-only load: no fork, no grants, no hooks, no shell commands.
            var readOnlyBody = SkillInvocation.RenderBody(skill, args, RenderOptions(context, skill) with
            {
                ShellDisabledPlaceholder =
                    "[shell command not executed: read-only skill load on the coordinator — delegate to a worker to run it]",
            });
            readOnlyBody = ApplyTracking(context, skill, args, readOnlyBody);
            return ToolResult.Success(
                $"Loaded skill instructions (read-only): {skill.Name}. Nothing was executed; delegate " +
                "execution to a worker.") with
            {
                FollowUpText = context.Truncate(readOnlyBody, "skill content"),
            };
        }

        if (skill.Fork)
            return await RunForkedAsync(skill, args, context, cancellationToken);

        string body;
        try
        {
            body = SkillInvocation.RenderBody(skill, args, RenderOptions(context, skill));
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }

        body = ApplyTracking(context, skill, args, body);
        context.SkillInvoked?.Invoke(skill);

        var followUp = body;
        if (VariantNote(skill, skills) is { } note)
            followUp += "\n\n" + note;
        return ToolResult.Success($"Launching skill: {skill.Name}") with
        {
            FollowUpText = context.Truncate(followUp, "skill content"),
        };
    }

    /// <summary>Elision against the prior invocation, then recording this one.</summary>
    private static string ApplyTracking(
        ToolExecutionContext context, SkillDefinition skill, string args, string rendered)
    {
        if (context.SkillTracker is not { } tracker)
            return rendered;
        var result = SkillInvocation.ApplyElision(skill.Name, args, rendered, tracker.Prior(skill.Name));
        tracker.Record(skill.Name, skill.FilePath, rendered);
        return result;
    }

    /// <summary>
    /// The reference's meta note when the unscoped skill is invoked while
    /// directory-scoped variants exist in the repo.
    /// </summary>
    private static string? VariantNote(SkillDefinition skill, IReadOnlyList<SkillDefinition> skills)
    {
        var variants = skills
            .Where(s => skill.Name.Equals(s.UnqualifiedName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (variants.Count == 0)
            return null;
        return $"Directory-scoped variants of the \"{skill.Name}\" skill exist in this repo:\n" +
               string.Join('\n', variants.Select(v => $"- {v.Name}")) + "\n" +
               "The bare name always resolves to this unscoped skill; the variants are reachable only by " +
               "their exact qualified names. If the files you are working on are under a variant's directory, " +
               "invoke that variant now with the Skill tool and follow it instead — it carries that subtree's " +
               "own instructions. If your changes span more than one variant's directory, run each matching variant.";
    }

    private static bool RequiresApproval(SkillDefinition skill) =>
        skill.AllowedTools.Count > 0 || skill.DisallowedTools.Count > 0 ||
        skill.HooksBlock is not null || skill.ShellDeclared;

    private static SkillInvocation.RenderOptions RenderOptions(ToolExecutionContext context, SkillDefinition skill) => new()
    {
        ProjectDirectory = context.WorkingDirectory,
        SessionId = context.SessionId,
        Effort = context.EffortName,
        RunShellCommand = context.RunSkillShell is { } run ? command => run(command, skill.Shell) : null,
    };

    // ---- forked execution (context: fork) ----

    private async Task<ToolResult> RunForkedAsync(
        SkillDefinition skill, string args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var services = context.Subagents;
        if (services is null || orchestrator is null)
            return ToolResult.Error(
                $"Skill {skill.Name} is a forked skill (context: fork) but subagents are not available in this context.");

        // A skill's `agent:` is written by hand, so it arrives in whatever case
        // the author used and may still use this harness's old spellings.
        var agentType = SubagentTool.CanonicalAgentType(skill.Agent ?? SubagentTool.GeneralAgentType);
        CustomAgentDefinition? customAgent = null;
        if (agentType is not (SubagentTool.ExploreAgentType or SubagentTool.GeneralAgentType or SubagentTool.PlanAgentType))
        {
            customAgent = services.CustomAgents.FirstOrDefault(a =>
                a.Name.Equals(agentType, StringComparison.OrdinalIgnoreCase));
            if (customAgent is null)
                agentType = SubagentTool.GeneralAgentType;
        }
        bool readOnlyTools = customAgent?.ReadOnlyTools
            ?? agentType is SubagentTool.ExploreAgentType or SubagentTool.PlanAgentType;
        if (context.IsPlanModeActive)
            readOnlyTools = true;

        ILlmProvider provider = services.ParentProvider;
        string modelId = services.ParentModelId;
        if (skill.Model is { Length: > 0 } requestedModel &&
            !requestedModel.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            if (ModelCatalog.Find(services.Models, requestedModel) is { } model)
            {
                try
                {
                    provider = services.Providers.Get(model.ProviderId);
                    modelId = model.ModelId;
                }
                catch (ProviderException)
                {
                    // The declared model has no provider configured; inherit instead.
                }
            }
        }

        string body;
        try
        {
            body = SkillInvocation.RenderBody(skill, args, RenderOptions(context, skill));
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }

        context.SkillInvoked?.Invoke(skill);
        context.SkillTracker?.Record(skill.Name, skill.FilePath, body);

        var innerTools = new ToolRegistry(services.ParentTools.All.Where(tool =>
            tool.Name != SubagentTool.ToolName &&
            tool.Name != ToolSearchTool.ToolName &&
            (!readOnlyTools || tool.IsReadOnly)));

        var systemPrompt = SystemPromptBuilder.BuildForSubagent(
            context.WorkingDirectory,
            readOnlyTools ? SubagentTool.ExploreAgentType : SubagentTool.GeneralAgentType,
            ProjectInstructions.LoadAll(context.WorkingDirectory),
            context.AdditionalDirectories);
        if (customAgent is not null)
            systemPrompt += $"\n# Your role: {customAgent.Name}\n{customAgent.SystemPrompt}\n";

        var turnContext = new AgentTurnContext
        {
            ConversationScopeId = $"{context.SessionId}/skill/{context.CallId}/{Guid.NewGuid():N}",
            Provider = provider,
            ModelId = modelId,
            SystemPrompt = systemPrompt,
            Messages = new List<ChatMessage> { ChatMessage.FromUserText(body) },
            Tools = innerTools,
            PermissionGate = services.PermissionGate,
            Hooks = services.Hooks,
            EnableWebSearch = services.EnableWebSearch,
            ThinkingEffort = services.ThinkingEffort,
            ToolContext = context with
            {
                Subagents = null,
                Workers = null,
                CallId = null,
                ForkedSkillName = skill.Name,
            },
            // A forked skill is the reference's fork agent, which is the one
            // built-in agent type carrying a cap of its own.
            MaxIterations = Agent.SubagentTool.ForkAgentMaxTurns,
        };

        Action<AgentEvent>? eventSink = null;
        if (context.CallId is { } parentCallId && services.EventReporter is { } reporter)
            eventSink = agentEvent => reporter(parentCallId, agentEvent);

        bool background = skill.Background ?? true;
        if (background && context.Workers is not null)
        {
            var id = context.Workers.Start(
                agentType, $"/{skill.Name}", turnContext,
                (workerContext, workerToken) => SubagentTool.RunTurnToReportAsync(
                    orchestrator, workerContext, context, report: null, workerToken, eventSink));
            return ToolResult.Success(
                $"Skill \"{skill.Name}\" launched (forked execution, running in the background).\n\n" +
                $"Running in the background as @{id}");
        }

        var result = await SubagentTool.RunTurnToReportAsync(
            orchestrator, turnContext, context, report: null, cancellationToken, eventSink);
        if (result.IsError)
            return result;
        return ToolResult.Success(
            $"Skill \"{skill.Name}\" completed (forked execution).\n\nResult:\n{result.Content}");
    }
}
