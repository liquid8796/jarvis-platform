using JarvisCode.Core.Customization;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Agent;

/// <summary>
/// Per-turn facilities the subagent tool needs from its host: which provider and
/// model the parent runs on, the catalog for model overrides, the full tool set
/// to filter, and the shared permission gate.
/// </summary>
/// <summary>
/// What a host needs in order to build a subagent's system prompt itself.
/// </summary>
/// <param name="AgentType">
/// The resolved built-in type — Explore, general-purpose or Plan — or a custom
/// agent's name.
/// </param>
/// <param name="CustomAgentPrompt">
/// A custom agent's declared prompt, or null for a built-in type.
/// </param>
/// <param name="TotalTokensBlock">
/// The agent's own <c>&lt;total_tokens&gt;</c> block for the end of its prompt
/// (see <see cref="TotalTokensReminder"/>), or null when the reminder is off.
/// </param>
public sealed record SubagentPromptRequest(
    string AgentType,
    string WorkingDirectory,
    string ModelId,
    IReadOnlyList<string>? AdditionalDirectories,
    string? CustomAgentPrompt,
    string? TotalTokensBlock = null);

public sealed record SubagentServices
{
    /// <summary>
    /// Builds the subagent's system prompt in place of this engine's own.
    /// </summary>
    /// <remarks>
    /// Additive: null keeps <see cref="SystemPromptBuilder.BuildForSubagent"/>,
    /// so nothing that does not set it changes behaviour. It exists because the
    /// reference's subagent prompt is a document of a different shape, and the
    /// port of it belongs beside the port of the main prompt — in the host —
    /// rather than in this engine's own builder.
    /// </remarks>
    public Func<SubagentPromptRequest, string>? SystemPrompt { get; init; }

    /// <summary>
    /// Creates the token-budget reminder a subagent keeps for itself — the
    /// reference gives every agent a fresh budget (a subagent's first request
    /// reads 15000000 whatever its parent has consumed). Null, the default,
    /// emits no block.
    /// </summary>
    public Func<TotalTokensReminder?>? TotalTokensFactory { get; init; }

    /// <summary>
    /// The system entries a child's request carries ahead of its prompt, by the
    /// model it runs on — the host's identity block and whatever else the
    /// reference sends there (see the host's LeadingBlocksFor). Null sends none.
    /// </summary>
    public Func<string, IReadOnlyList<SystemPromptBlock>>? LeadingSystemBlocks { get; init; }

    public required ILlmProvider ParentProvider { get; init; }

    public required string ParentModelId { get; init; }

    /// <summary>Models available for the optional per-subagent model override.</summary>
    public required IReadOnlyList<ModelInfo> Models { get; init; }

    public required IProviderRegistry Providers { get; init; }

    /// <summary>The parent's tool registry; the subagent tool filters it per agent type.</summary>
    public required IToolRegistry ParentTools { get; init; }

    /// <summary>Mutating tools inside a subagent still ask the user through this gate.</summary>
    public required IPermissionGate PermissionGate { get; init; }

    /// <summary>Live progress line for the caller: (tool call id, current activity).</summary>
    public Action<string, string>? ActivityReporter { get; init; }

    /// <summary>
    /// Full inner-event feed for hosts that render the subagent's own transcript:
    /// (parent Agent call id, inner event). Optional and purely observational.
    /// </summary>
    public Action<string, AgentEvent>? EventReporter { get; init; }

    /// <summary>User-defined agent types loaded from .jarvis/agents; usable as agent_type values.</summary>
    public IReadOnlyList<CustomAgentDefinition> CustomAgents { get; init; } = [];

    /// <summary>Tool hooks of the session; subagent tool calls go through them too.</summary>
    public Hooks.IToolHooks? Hooks { get; init; }

    /// <summary>Subagents inherit the session's server-side web search setting.</summary>
    public bool EnableWebSearch { get; init; }

    /// <summary>Subagents inherit the session's reasoning budget.</summary>
    public ThinkingEffort ThinkingEffort { get; init; } = ThinkingEffort.Off;

    /// <summary>
    /// Resolves opaque provider controls for the model a subagent actually runs.
    /// Null sends no provider-defined options.
    /// </summary>
    public Func<string, IReadOnlyDictionary<string, string>>? ProviderOptionsForModel { get; init; }

    /// <summary>The session's skills, listed in the subagent's system prompt (the skill tool rides the registry).</summary>
    public IReadOnlyList<SkillDefinition>? Skills { get; init; }

    /// <summary>Team file store; set when the session can spawn named teammates.</summary>
    public TeamStore? Teams { get; init; }

    /// <summary>The session's implicit team name (the reference: one team per session).</summary>
    public string? TeamName { get; init; }

    /// <summary>
    /// Delivers a running teammate's message to the lead's transcript:
    /// (fromName, message, summary). Hosts render it as a task notification.
    /// </summary>
    public Func<string, string, string?, Task>? TeammateMessage { get; init; }

    /// <summary>How the session asks for teammates to execute (tmux/iterm2/in-process/auto).</summary>
    public TeammateMode TeammateMode { get; init; } = TeammateMode.Auto;

    /// <summary>
    /// Whether <c>subagent_type: "fork"</c> is offered this turn — the
    /// reference's fork gate, which is on for an interactive session and off for
    /// a print or SDK-hosted one (see <see cref="ForkAgent"/>). False, the
    /// default, keeps the surface every capture shows.
    /// </summary>
    public bool ForkEnabled { get; init; }

    /// <summary>
    /// The parent turn's own system prompt, which a fork runs on: the reference's
    /// fork definition declares <c>getSystemPrompt: () =&gt; ""</c> and its doc
    /// says a fork "shares your prompt cache", both of which describe a child on
    /// the parent's prompt rather than on the subagent document. Null falls back
    /// to <see cref="SystemPrompt"/>, so a host that does not set it is unchanged.
    /// </summary>
    public string? ParentSystemPrompt { get; init; }
}
