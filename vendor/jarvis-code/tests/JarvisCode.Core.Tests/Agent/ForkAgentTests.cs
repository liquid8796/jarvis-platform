using System.Text.Json.Nodes;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Tests.Agent;

/// <summary>
/// The reference's fork gate, its two refusals and the conversation a fork
/// starts from.
/// </summary>
public sealed class ForkAgentTests
{
    private static Func<string, string?> Env(string? forkSubagent = null) =>
        key => key == ForkAgent.EnabledVariable ? forkSubagent : null;

    [Fact]
    public void The_gate_is_on_for_an_interactive_session_and_off_otherwise()
    {
        Assert.True(ForkAgent.IsEnabled(interactive: true, coordinatorMode: false, Env()));
        Assert.False(ForkAgent.IsEnabled(interactive: false, coordinatorMode: false, Env()));
    }

    [Fact]
    public void Coordinator_mode_takes_the_gate_away()
    {
        Assert.False(ForkAgent.IsEnabled(interactive: true, coordinatorMode: true, Env()));
        // Even the environment override does not put it back — the reference
        // checks coordinator mode before it reads the flag's "on" spelling.
        Assert.False(ForkAgent.IsEnabled(interactive: true, coordinatorMode: true, Env("1")));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    public void The_environment_variable_forces_it_either_way(string value, bool expected)
    {
        Assert.Equal(expected, ForkAgent.IsEnabled(interactive: false, coordinatorMode: false, Env(value)));
        Assert.Equal(expected, ForkAgent.IsEnabled(interactive: true, coordinatorMode: false, Env(value)));
    }

    [Fact]
    public void A_custom_agent_named_fork_takes_the_name_back()
    {
        Assert.False(ForkAgent.IsAvailable(enabled: true, ["Explore", "fork"], allowedAgentTypes: null));
        Assert.True(ForkAgent.IsAvailable(enabled: true, ["Explore"], allowedAgentTypes: null));
    }

    [Fact]
    public void A_narrowed_allowlist_must_still_admit_it()
    {
        Assert.False(ForkAgent.IsAvailable(enabled: true, [], ["Explore", "Plan"]));
        Assert.True(ForkAgent.IsAvailable(enabled: true, [], ["Explore", "fork"]));
    }

    [Fact]
    public void The_directive_is_wrapped_in_the_reference_tag()
    {
        var directive = ForkAgent.Directive("audit the branch");

        Assert.StartsWith("<fork-boilerplate>\n", directive);
        Assert.Contains("</fork-boilerplate>\n\nYour directive: audit the branch", directive, StringComparison.Ordinal);
        Assert.Contains("You are a worker fork.", directive, StringComparison.Ordinal);
    }

    [Fact]
    public void A_conversation_carrying_a_fork_preamble_is_recognised()
    {
        var forked = new[] { ChatMessage.FromUserText(ForkAgent.Directive("go")) };

        Assert.True(ForkAgent.IsForkedConversation(forked));
        Assert.False(ForkAgent.IsForkedConversation([ChatMessage.FromUserText("go")]));
        Assert.False(ForkAgent.IsForkedConversation(null));
    }

    [Fact]
    public void The_fork_answers_every_call_that_was_in_flight()
    {
        var parent = new List<ChatMessage>
        {
            ChatMessage.FromUserText("what is left on this branch?"),
            new(Role.Assistant,
            [
                new TextBlock("forking"),
                new ToolCallBlock("call-1", "Agent", "{}"),
                new ToolCallBlock("call-2", "Agent", "{}"),
            ]),
        };

        var conversation = ForkAgent.BuildConversation(parent, "audit the branch");

        Assert.Equal(3, conversation.Count);
        // The parent's turns are carried through unchanged — that is what the
        // fork inherits.
        Assert.Same(parent[0], conversation[0]);
        Assert.Same(parent[1], conversation[1]);
        var tail = conversation[2];
        Assert.Equal(Role.User, tail.Role);
        var results = tail.Content.OfType<ToolResultBlock>().ToList();
        Assert.Equal(["call-1", "call-2"], results.Select(r => r.ToolCallId));
        Assert.All(results, r => Assert.Equal(ForkAgent.LaunchedResult, r.Content));
        var text = Assert.IsType<TextBlock>(tail.Content[^1]);
        Assert.Contains("Your directive: audit the branch", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_assistant_turn_with_no_calls_falls_back_to_the_directive_alone()
    {
        var parent = new List<ChatMessage> { ChatMessage.FromUserText("hello") };

        var conversation = ForkAgent.BuildConversation(parent, "audit");

        Assert.Equal(2, conversation.Count);
        Assert.Equal(Role.User, conversation[1].Role);
        Assert.Empty(conversation[1].Content.OfType<ToolResultBlock>());
    }

    [Fact]
    public void An_empty_parent_conversation_still_yields_the_directive()
    {
        var conversation = ForkAgent.BuildConversation([], "audit");

        Assert.Single(conversation);
        Assert.Contains("Your directive: audit", conversation[0].Content.OfType<TextBlock>().Single().Text,
            StringComparison.Ordinal);
    }
}

/// <summary>
/// The Agent tool's fork path end to end: what the child's request carries, and
/// the two refusals.
/// </summary>
public sealed class SubagentForkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static ProviderEvent[] TextTurn(string text) =>
        [new TextDeltaEvent(text), new ResponseCompletedEvent(WantsToolUse: false, new Usage(1, 1))];

    private (SubagentTool Tool, ToolExecutionContext Context, ScriptedProvider Provider) Harness(
        bool forkEnabled = true, IReadOnlyList<ChatMessage>? conversation = null, bool isSubagent = false)
    {
        var provider = new ScriptedProvider(TextTurn("the fork's report"));
        var tool = new SubagentTool(new AgentOrchestrator());
        var services = new SubagentServices
        {
            ParentProvider = provider,
            ParentModelId = "parent-model",
            Models = [new ModelInfo("scripted", "parent-model", "Parent", 200_000)],
            Providers = new ProviderRegistry([provider]),
            ParentTools = new ToolRegistry([tool]),
            PermissionGate = new AutoApprovePermissionGate(),
            ForkEnabled = forkEnabled,
            ParentSystemPrompt = "the parent's own prompt",
        };
        var context = new ToolExecutionContext
        {
            WorkingDirectory = _temp.Path,
            CallId = "call-1",
            IsSubagent = isSubagent,
            Subagents = services,
            ConversationSnapshot = conversation is null ? null : () => conversation,
        };
        return (tool, context, provider);
    }

    private static List<ChatMessage> ParentConversation() =>
    [
        ChatMessage.FromUserText("what is left on this branch?"),
        new(Role.Assistant, [new ToolCallBlock("call-1", "Agent", "{}")]),
    ];

    [Fact]
    public async Task The_fork_inherits_the_conversation_the_model_and_the_prompt()
    {
        var (tool, context, provider) = Harness(conversation: ParentConversation());

        var result = await tool.ExecuteAsync(
            new JsonObject { ["prompt"] = "audit the branch", ["subagent_type"] = "fork" },
            context,
            CancellationToken.None);

        Assert.False(result.IsError);
        var request = Assert.Single(provider.Requests);
        Assert.Equal("the parent's own prompt", request.SystemPrompt);
        Assert.Equal("parent-model", request.ModelId);
        // The parent's two turns, then the fork's own directive turn.
        Assert.Equal(3, request.Messages.Count);
        Assert.Equal("what is left on this branch?", request.Messages[0].Content.OfType<TextBlock>().Single().Text);
        var tail = request.Messages[2];
        Assert.Equal(ForkAgent.LaunchedResult, tail.Content.OfType<ToolResultBlock>().Single().Content);
        Assert.Contains("Your directive: audit the branch",
            tail.Content.OfType<TextBlock>().Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_type_is_what_fork_is_while_the_gate_is_off()
    {
        var (tool, context, _) = Harness(forkEnabled: false, conversation: ParentConversation());

        var result = await tool.ExecuteAsync(
            new JsonObject { ["prompt"] = "audit", ["subagent_type"] = "fork" },
            context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Unknown subagent_type 'fork'", result.Content, StringComparison.Ordinal);
        // ...and the offered list does not name it either.
        Assert.DoesNotContain("Available: fork", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_isolation_is_refused_in_the_reference_words()
    {
        var (tool, context, _) = Harness(conversation: ParentConversation());

        var result = await tool.ExecuteAsync(
            new JsonObject { ["prompt"] = "audit", ["subagent_type"] = "fork", ["isolation"] = "remote" },
            context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ForkAgent.RemoteIsolationRefusal, result.Content);
    }

    [Fact]
    public async Task A_fork_inside_a_fork_is_refused()
    {
        var (tool, context, _) = Harness(isSubagent: true, conversation: ParentConversation());

        var result = await tool.ExecuteAsync(
            new JsonObject { ["prompt"] = "audit", ["subagent_type"] = "fork" },
            context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ForkAgent.RecursiveForkRefusal, result.Content);
    }

    [Fact]
    public async Task A_model_override_is_ignored_on_a_fork()
    {
        var (tool, context, provider) = Harness(conversation: ParentConversation());

        await tool.ExecuteAsync(
            new JsonObject { ["prompt"] = "audit", ["subagent_type"] = "fork", ["model"] = "haiku" },
            context,
            CancellationToken.None);

        Assert.Equal("parent-model", Assert.Single(provider.Requests).ModelId);
    }
}
