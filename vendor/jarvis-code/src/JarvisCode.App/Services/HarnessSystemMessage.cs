using System.Collections.Generic;
using System.Text;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference CLI's mid-conversation harness message: the agent-type roster,
/// the parallel-agents note and the skill listing, sent as a message whose role
/// on the wire is literally <c>system</c> rather than as blocks bolted onto the
/// user's turn.
///
/// Measured against the installed CLI 2.1.251 by capturing its real requests
/// (a local listener answering a complete Messages SSE turn, so a second
/// <c>--continue</c> request could be recorded too):
///
///   - Request 1 carries <c>messages: [user[reminder, "first"], system[roster]]</c>
///     — the roster is its own trailing message, plain text with no
///     <c>&lt;system-reminder&gt;</c> wrapper around it.
///   - Request 2 carries that same system message *from history*, unchanged,
///     and adds no second one. So it is persisted like any other message and
///     emitted once, not rebuilt per request — which is also why the static
///     agent-type roster costs its tokens a single time.
///
/// Re-measured on 2.1.257: the roster grew <c>claude</c>, <c>statusline-setup</c>
/// and (on the desktop entrypoint) <c>claude-code-guide</c>, each line closing
/// with the tools its definition grants, and a model without the mid-conversation
/// system role receives the same sections as <c>&lt;system-reminder&gt;</c> blocks
/// leading its user message instead — see <see cref="Sections"/>.
///
/// The listing half keeps the engine's existing "full first, only newly
/// discovered skills afterwards" rule, so a later turn that does find new
/// skills sends a further system message carrying just those.
/// </summary>
public static class HarnessSystemMessage
{
    /// <summary>The reference header, on this harness's tool name.</summary>
    public const string AgentTypesHeader = "Available agent types for the Agent tool:";

    /// <summary>The reference's note under the roster, verbatim.</summary>
    public const string ParallelAgentsNote =
        "When you launch multiple agents for independent work, send them in a single message with multiple " +
        "tool uses so they run concurrently.";

    /// <summary>
    /// How the roster describes the built-in types, verbatim from a live 2.1.257
    /// roster. Explore has two descriptions: the lean-prompt models get the
    /// "broad fan-out" one, the classic-prompt models the "fast … locating code"
    /// one (measured on opus-5 against opus-4-5).
    /// </summary>
    private const string ClaudeDescription =
        "Catch-all for any task that doesn't fit a more specific agent. FleetView's default when no agent name " +
        "is typed.";

    private const string ClaudeCodeGuideDescription =
        "Use this agent when the user asks questions (\"Can Claude...\", \"Does Claude...\", \"How do I...\") " +
        "about: (1) Claude Code (the CLI tool) - features, hooks, slash commands, MCP servers, settings, IDE " +
        "integrations, keyboard shortcuts; (2) Claude Agent SDK - building custom agents; (3) Claude API " +
        "(formerly Anthropic API) - Messages API for directly passing messages to Claude, Tool Runner " +
        "(`client.beta.messages.tool_runner`) for running an agentic loop over your own tools, manual tool-use " +
        "loops, Managed Agents for server-hosted agents with a managed sandbox, prompt caching, and general " +
        "Anthropic SDK usage; (4) Claude Tag (Claude in Slack) - what it is, setting it up for a Slack " +
        "workspace, `/install-slack-app`; (5) `claude plugin eval` (writing and running plugin eval suites, " +
        "its JSON/report, sandbox, CI, early-access enablement) and the `/skill-doctor` report. " +
        "**IMPORTANT:** Before spawning a new agent, check if there is already a running or recently completed " +
        "claude-code-guide agent that you can continue via SendMessage.";

    private const string ExploreLeanDescription =
        "Read-only search agent for broad fan-out searches — when answering means sweeping many files, " +
        "directories, or naming conventions and you only need the conclusion, not the file dumps. It reads " +
        "excerpts rather than whole files, so it locates code; it doesn't review or audit it. Specify search " +
        "breadth: \"medium\" for moderate exploration, \"very thorough\" for multiple locations and naming " +
        "conventions.";

    private const string ExploreClassicDescription =
        "Fast read-only search agent for locating code. Use it to find files by pattern (eg. " +
        "\"src/components/**/*.tsx\"), grep for symbols or keywords (eg. \"API endpoints\"), or answer \"where " +
        "is X defined / which files reference Y.\" Do NOT use it for code review, design-doc auditing, " +
        "cross-file consistency checks, or open-ended analysis — it reads excerpts rather than whole files and " +
        "will miss content past its read window. When calling, specify search breadth: \"quick\" for a single " +
        "targeted lookup, \"medium\" for moderate exploration, or \"very thorough\" to search across multiple " +
        "locations and naming conventions.";

    private const string GeneralPurposeDescription =
        "General-purpose agent for researching complex questions, searching for code, and executing " +
        "multi-step tasks. When you are searching for a keyword or file and are not confident that you will " +
        "find the right match in the first few tries use this agent to perform the search for you.";

    private const string PlanDescription =
        "Software architect agent for designing implementation plans. Use this when you need to plan the " +
        "implementation strategy for a task. Returns step-by-step plans, identifies critical files, and " +
        "considers architectural trade-offs.";

    private const string StatuslineSetupDescription =
        "Use this agent to configure the user's Claude Code status line setting.";

    /// <summary>
    /// What the roster needs to know about the session: which prompt form the
    /// model takes, which tools a read-only agent is denied, and whether the
    /// entrypoint carries the guide agent (the desktop does; the sdk-cli
    /// entrypoint does not).
    /// </summary>
    /// <param name="ReadOnlyExcludedTools">
    /// The names of the session's tools a read-only agent does not receive, in
    /// registry order — the reference prints "All tools except …" from its own
    /// registry, and this port prints it from its own.
    /// </param>
    public readonly record struct RosterContext(
        bool LeanPrompt,
        IReadOnlyList<string> ReadOnlyExcludedTools,
        bool IncludeGuideAgent);

    /// <summary>
    /// The built-in roster lines for a session, in the reference's order:
    /// claude, claude-code-guide, Explore, general-purpose, Plan, statusline-setup.
    /// </summary>
    public static IReadOnlyList<string> BuiltInAgentLines(RosterContext roster)
    {
        var except = roster.ReadOnlyExcludedTools.Count == 0
            ? "*"
            : "All tools except " + string.Join(", ", roster.ReadOnlyExcludedTools);
        var lines = new List<string>
        {
            $"- claude: {ClaudeDescription} (Tools: *)",
        };
        if (roster.IncludeGuideAgent)
        {
            lines.Add($"- claude-code-guide: {ClaudeCodeGuideDescription} (Tools: Glob, Grep, Read, WebFetch, WebSearch)");
        }

        lines.Add($"- Explore: {(roster.LeanPrompt ? ExploreLeanDescription : ExploreClassicDescription)} (Tools: {except})");
        lines.Add($"- general-purpose: {GeneralPurposeDescription} (Tools: *)");
        lines.Add($"- Plan: {PlanDescription} (Tools: {except})");
        lines.Add($"- statusline-setup: {StatuslineSetupDescription} (Tools: Read, Edit)");
        return lines;
    }

    /// <summary>
    /// The reference's roster context before this round measured any of it:
    /// lean, no excluded-tool list, no guide agent. Kept for callers that have
    /// nothing better to say.
    /// </summary>
    public static readonly RosterContext DefaultRoster = new(
        LeanPrompt: true, ReadOnlyExcludedTools: [], IncludeGuideAgent: false);

    /// <summary>
    /// The roster block on its own: header, built-in lines, custom agents, then
    /// the parallel note.
    /// </summary>
    public static string Roster(IReadOnlyList<CustomAgentDefinition>? customAgents, RosterContext roster)
    {
        var builder = new StringBuilder();
        builder.Append(AgentTypesHeader).Append('\n');
        foreach (var line in BuiltInAgentLines(roster))
        {
            builder.Append(line).Append('\n');
        }

        if (customAgents is { Count: > 0 })
        {
            foreach (var agent in customAgents)
            {
                builder
                    .Append($"- {agent.Name}: {agent.Description} ")
                    .Append($"(Tools: {(agent.ReadOnlyTools ? "read-only" : "all")})")
                    .Append('\n');
            }
        }

        builder.Append('\n').Append(ParallelAgentsNote);
        return builder.ToString();
    }

    /// <summary>
    /// The message body, or null when there is nothing to announce. The roster
    /// rides only the first such message of a session (<paramref name="includeAgentTypes"/>);
    /// afterwards the message carries the newly discovered skills alone.
    /// </summary>
    /// <param name="tailNotices">
    /// What the session's mode and output style say, in the order they were
    /// captured. The reference appends these after the skill listing rather
    /// than putting them on the user's message, and a turn that has only these
    /// to say still sends the message — which is how a style reminder repeats
    /// on turns that announce no new skill. See <see cref="SessionModeNotices"/>.
    /// </param>
    public static string? Build(
        IReadOnlyList<CustomAgentDefinition>? customAgents,
        string? skillListingBody,
        bool includeAgentTypes,
        string? mcpServerInstructions = null,
        IReadOnlyList<string>? tailNotices = null,
        RosterContext? roster = null)
    {
        var sections = Sections(customAgents, skillListingBody, includeAgentTypes, mcpServerInstructions, tailNotices, roster);
        return sections.Count == 0 ? null : string.Join("\n\n", sections);
    }

    /// <summary>
    /// The same content as a list of sections, in the order they ride the
    /// message: roster (with its parallel note), MCP server instructions, skill
    /// listing, then each notice. A model without the mid-conversation system
    /// role gets each one as its own <c>&lt;system-reminder&gt;</c> block on the
    /// user message instead of one system turn — the shape a classic-prompt
    /// model's first request carries (measured on opus-4-5 and haiku-4-5).
    /// </summary>
    public static IReadOnlyList<string> Sections(
        IReadOnlyList<CustomAgentDefinition>? customAgents,
        string? skillListingBody,
        bool includeAgentTypes,
        string? mcpServerInstructions = null,
        IReadOnlyList<string>? tailNotices = null,
        RosterContext? roster = null)
    {
        var sections = new List<string>();
        if (includeAgentTypes)
        {
            sections.Add(Roster(customAgents, roster ?? DefaultRoster));
        }

        // The reference's order, read off a live ccd session's system turn:
        // roster, parallel note, MCP server instructions, skill listing.
        if (!string.IsNullOrEmpty(mcpServerInstructions))
        {
            sections.Add(mcpServerInstructions);
        }

        if (!string.IsNullOrEmpty(skillListingBody))
        {
            sections.Add(SkillInvocation.ListingHeader + "\n\n" + skillListingBody);
        }

        // Mode and style notices close the message, each separated by a blank
        // line — the position a bypass capture and a plan capture both put them
        // in, after the skill listing.
        foreach (var notice in tailNotices ?? [])
        {
            if (!string.IsNullOrEmpty(notice))
            {
                sections.Add(notice);
            }
        }

        return sections;
    }
}
