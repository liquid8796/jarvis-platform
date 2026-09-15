using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Sessions;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;
using JarvisCode.Core.Utilities;

namespace JarvisCode.Core.Tests.Agent;

public sealed class BrowserSessionIntegrationTests
{
    private static ProviderEvent[] Answer(string text) =>
        [new TextDeltaEvent(text), new ResponseCompletedEvent(false, new Usage(1, 1), StopReasons.EndTurn)];

    private static AgentTurnContext Context(ILlmProvider provider, string? sessionId = "session-a", ITool? tool = null) => new()
    {
        Provider = provider,
        ModelId = "browser-model",
        SystemPrompt = "same system prompt",
        Messages = new List<ChatMessage> { ChatMessage.FromUserText("same prompt") },
        Tools = new ToolRegistry(tool is null ? [] : [tool]),
        PermissionGate = new AutoApprovePermissionGate(),
        ToolContext = new ToolExecutionContext { WorkingDirectory = Path.GetTempPath(), SessionId = sessionId },
    };

    [Fact]
    public void RequestScopeDefaultsToSessionIdentityAndAnExplicitChildScopeTakesPrecedence()
    {
        var first = Context(new ScriptedProvider());
        var copy = first with { Messages = new List<ChatMessage>(first.Messages) };
        Assert.Equal("session-a", first.ToRequest().ConversationScopeId);
        Assert.Equal(first.ToRequest().ConversationScopeId, copy.ToRequest().ConversationScopeId);
        Assert.Equal("child-scope", (first with { ConversationScopeId = "child-scope" }).ToRequest().ConversationScopeId);
        Assert.Equal("session-b", (first with
        {
            ToolContext = first.ToolContext with { SessionId = "session-b" },
        }).ToRequest().ConversationScopeId);
        Assert.Null(Context(new ScriptedProvider(), sessionId: null).ToRequest().ConversationScopeId);
    }

    [Fact]
    public async Task OpaqueProviderOptionsReachEveryModelCallWithoutEnteringTheConversation()
    {
        var probe = new ProbeTool();
        var provider = new ScriptedProvider(
        [
            new ToolCallStartedEvent(0, "probe-1", probe.Name),
            new ToolCallArgumentsDeltaEvent(0, "{}"),
            new ResponseCompletedEvent(true, new Usage(1, 1), StopReasons.ToolUse),
        ], Answer("done"));
        var context = Context(provider, tool: probe) with
        {
            ProviderOptions = new Dictionary<string, string>(StringComparer.Ordinal) { ["power"] = "4" },
        };

        await foreach (var _ in new AgentOrchestrator().RunTurnAsync(context, default)) { }

        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, request => Assert.Equal("4", request.ProviderOptions["power"]));
        Assert.DoesNotContain("power", string.Join("\n", context.Messages.Select(message => message.GetText())),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("general-purpose")]
    [InlineData("fork")]
    public async Task SeparateChildInvocationsHaveDifferentScopesEvenWhenTheOuterToolCallIdIsReused(string agentType)
    {
        using var directory = new TempDirectory();
        var probe = new ProbeTool();
        ProviderEvent[] ToolTurn() =>
        [
            new ToolCallStartedEvent(0, "inner-call", probe.Name),
            new ToolCallArgumentsDeltaEvent(0, "{}"),
            new ResponseCompletedEvent(true, new Usage(1, 1), StopReasons.ToolUse),
        ];
        var provider = new ScriptedProvider(ToolTurn(), Answer("first child"), ToolTurn(), Answer("second child"));
        var tool = new SubagentTool(new AgentOrchestrator());
        var services = Services(provider, new ToolRegistry([probe, tool]));
        var parentHistory = new List<ChatMessage>
        {
            ChatMessage.FromUserText("parent prompt"),
            new(Role.Assistant, [new ToolCallBlock("outer-call", "Agent", "{}")]),
        };
        var context = new ToolExecutionContext
        {
            WorkingDirectory = directory.Path,
            SessionId = "parent-session",
            // Workflow agent() calls share the containing Workflow invocation's call id.
            CallId = "outer-call",
            Subagents = services,
            ConversationSnapshot = () => parentHistory,
        };
        for (var invocation = 0; invocation < 2; invocation++)
        {
            var result = await tool.ExecuteAsync(
                new JsonObject { ["prompt"] = "same directive", ["subagent_type"] = agentType }, context, default);
            Assert.False(result.IsError, result.Content);
        }

        Assert.Equal(4, provider.Requests.Count);
        var scopes = provider.Requests.Select(request => request.ConversationScopeId).ToArray();
        Assert.All(scopes, scope =>
        {
            Assert.False(string.IsNullOrWhiteSpace(scope));
            Assert.NotEqual("parent-session", scope);
        });
        Assert.Equal(scopes[0], scopes[1]);
        Assert.Equal(scopes[2], scopes[3]);
        Assert.NotEqual(scopes[0], scopes[2]);
        Assert.Equal(2, probe.Executions);
        if (agentType == "fork")
        {
            Assert.All(provider.Requests, request => Assert.Equal(services.ParentSystemPrompt, request.SystemPrompt));
            Assert.All(provider.Requests, request => Assert.Equal("parent prompt", request.Messages[0].GetText()));
        }
    }

    [Fact]
    public async Task SeparateForkedSkillInvocationsDoNotReuseTheirParentsConversationScope()
    {
        using var directory = new TempDirectory();
        var provider = new ScriptedProvider(Answer("first skill"), Answer("second skill"));
        var tool = new SkillTool(new AgentOrchestrator());
        var skill = new SkillDefinition("audit", "Audit", "test", "Read the task and report.",
            Path.Combine(directory.Path, "SKILL.md"), DateTimeOffset.UnixEpoch, "test")
        {
            Directory = directory.Path, Fork = true, Background = false,
        };
        var context = new ToolExecutionContext
        {
            WorkingDirectory = directory.Path, SessionId = "parent-session", CallId = "same-call",
            Skills = [skill], Subagents = Services(provider, new ToolRegistry([])),
        };
        for (var invocation = 0; invocation < 2; invocation++)
        {
            var result = await tool.ExecuteAsync(new JsonObject { ["skill"] = skill.Name }, context, default);
            Assert.False(result.IsError, result.Content);
        }

        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, request =>
        {
            Assert.False(string.IsNullOrWhiteSpace(request.ConversationScopeId));
            Assert.NotEqual(context.SessionId, request.ConversationScopeId);
        });
        Assert.NotEqual(provider.Requests[0].ConversationScopeId, provider.Requests[1].ConversationScopeId);
    }

    [Fact]
    public async Task BackgroundWorkerFollowupRetainsItsOwnScopeAndHistory()
    {
        var provider = new ScriptedProvider(Answer("first answer"), Answer("follow-up answer"));
        var context = Context(provider) with { ConversationScopeId = "child-scope" };
        var workers = new AgentWorkerManager();
        var finished = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        workers.WorkerFinished += (_, result) => finished.TrySetResult(result);
        var id = workers.Start("general-purpose", "test", context, async (workerContext, token) =>
        {
            await foreach (var _ in new AgentOrchestrator().RunTurnAsync(workerContext, token)) { }
            return ToolResult.Success("done");
        });
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        finished = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Null(workers.Continue(id, "follow up"));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, request => Assert.Equal("child-scope", request.ConversationScopeId));
        Assert.Equal(new[] { "same prompt", "first answer", "follow up" },
            provider.Requests[1].Messages.Select(message => message.GetText()));
    }

    [Fact]
    public async Task BrowserPreviewIsDisplayOnlyAndCannotBecomeHistoryOrAToolCall()
    {
        const string preview = "JARVIS_ACT {\"name\":\"probe\",\"arguments\":{}}";
        var probe = new ProbeTool();
        var provider = new ScriptedProvider(
        [
            new TextPreviewEvent("partial browser text"), new TextPreviewEvent(preview),
            new TextDeltaEvent("authoritative answer"),
            new ResponseCompletedEvent(false, new Usage(10, 5) { IsEstimated = true }, StopReasons.EndTurn),
        ]);
        var context = Context(provider, tool: probe);
        var events = new List<AgentEvent>();
        await foreach (var entry in new AgentOrchestrator().RunTurnAsync(context, default)) events.Add(entry);

        Assert.Equal(new[] { "partial browser text", preview }, events.OfType<AssistantTextPreviewed>().Select(item => item.Text));
        Assert.Equal("authoritative answer", Assert.Single(events.OfType<AssistantMessageCompleted>()).Message.GetText());
        Assert.Empty(events.OfType<ToolInputStarted>());
        Assert.Empty(events.OfType<ToolExecutionStarted>());
        Assert.Equal(0, probe.Executions);
        var persisted = JsonSerializer.Serialize(new Session { Id = "session-a", Messages = [.. context.Messages] });
        Assert.DoesNotContain("partial browser text", persisted);
        Assert.DoesNotContain("JARVIS_ACT", persisted);
        Assert.True(Assert.Single(events.OfType<UsageReported>()).Total.IsEstimated);
    }

    [Fact]
    public async Task CancelledBrowserPreviewLeavesNoAssistantMessageToResume()
    {
        var provider = new ScriptedProvider([new TextPreviewEvent("unfinished preview"), .. Answer("never delivered")]);
        var context = Context(provider);
        using var cancelled = new CancellationTokenSource();
        var events = new List<AgentEvent>();
        await foreach (var entry in new AgentOrchestrator().RunTurnAsync(context, cancelled.Token))
        {
            events.Add(entry);
            if (entry is AssistantTextPreviewed) cancelled.Cancel();
        }
        Assert.Equal(TurnEndReason.Cancelled, Assert.Single(events.OfType<TurnCompleted>()).Reason);
        Assert.Empty(events.OfType<AssistantMessageCompleted>());
        Assert.Single(context.Messages);
        Assert.Equal(Role.User, context.Messages[0].Role);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void AccumulatedUsageKeepsEstimateProvenance(bool firstEstimated, bool secondEstimated, bool expected)
    {
        var total = new Usage(10, 20, 30, 40) { IsEstimated = firstEstimated }
            .Add(new Usage(1, 2, 3, 4) { IsEstimated = secondEstimated });
        Assert.Equal(expected, total.IsEstimated);
        Assert.Equal(new Usage(11, 22, 33, 44) { IsEstimated = expected }, total);
        var restored = JsonSerializer.Deserialize<Usage>(JsonSerializer.Serialize(total))!;
        Assert.Equal(total, restored);
    }

    [Fact]
    public void EstimatedBrowserUsageDoesNotTurnConfiguredApiPricesIntoAnApparentCharge()
    {
        var model = new ModelInfo("browser", "browser-model", "Browser", 128_000, 5, 10);
        var estimated = new Usage(1_000_000, 1_000_000) { IsEstimated = true };
        Assert.Null(CostCalculator.Estimate(model, estimated));
        Assert.Equal(15d, CostCalculator.Estimate(model, estimated with { IsEstimated = false }));
    }

    [Fact]
    public void BrowserCapabilitiesPassThroughNestedWrappersAndClampUnsupportedRequestControls()
    {
        var declared = new ProviderCapabilities(false, false, false, false);
        var browser = new CapableProvider(declared);
        var wrapped = new WrapperProvider(new WrapperProvider(browser));
        Assert.Same(browser, ProviderDecorators.Unwrap(wrapped));
        Assert.Equal(declared, ProviderCapabilities.For(wrapped));
        var request = (Context(wrapped) with { EnableWebSearch = true, ThinkingEffort = ThinkingEffort.Max }).ToRequest();
        Assert.False(request.EnableWebSearch);
        Assert.Equal(ThinkingEffort.Off, request.ThinkingEffort);

        var ordinary = new WrapperProvider(new ScriptedProvider());
        Assert.Equal(new ProviderCapabilities(), ProviderCapabilities.For(ordinary));
        var ordinaryRequest = (Context(ordinary) with { EnableWebSearch = true, ThinkingEffort = ThinkingEffort.Max }).ToRequest();
        Assert.True(ordinaryRequest.EnableWebSearch);
        Assert.Equal(ThinkingEffort.Max, ordinaryRequest.ThinkingEffort);
    }

    [Theory]
    [InlineData(false, 1, 0)]
    [InlineData(true, 2, 1)]
    public async Task QueuedBrowserResponsesAreNotCancelledAndResentByThePostToolStallNudge(
        bool supportsRetry, int expectedCalls, int expectedCancellations)
    {
        var provider = new DelayedProvider(supportsRetry);
        var context = Context(new WrapperProvider(provider)) with
        {
            PostToolStallTimeout = TimeSpan.FromMilliseconds(15),
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromUserText("read the result"),
                new(Role.Assistant, [new ToolCallBlock("read-1", "Read", "{}")]),
                ChatMessage.FromToolResults([new ToolResultBlock("read-1", "Read", "result", false)]),
            },
        };
        var events = new List<AgentEvent>();
        await foreach (var item in new AgentOrchestrator().RunTurnAsync(context, default)) events.Add(item);

        Assert.Equal(TurnEndReason.Completed, Assert.Single(events.OfType<TurnCompleted>()).Reason);
        Assert.Equal("queued answer", Assert.Single(events.OfType<AssistantMessageCompleted>()).Message.GetText());
        Assert.Equal(expectedCalls, provider.Calls);
        Assert.Equal(expectedCancellations, provider.CancelledCalls);
    }

    private static SubagentServices Services(ILlmProvider provider, IToolRegistry tools) => new()
    {
        ParentProvider = provider, ParentModelId = "browser-model",
        Models = [new ModelInfo(provider.Id, "browser-model", "Browser", 128_000)],
        Providers = new ProviderRegistry([provider]), ParentTools = tools,
        PermissionGate = new AutoApprovePermissionGate(), ForkEnabled = true,
        ParentSystemPrompt = "same parent system prompt", SystemPrompt = _ => "same child system prompt",
    };

    private sealed class ProbeTool : ITool
    {
        public int Executions { get; private set; }
        public string Name => "probe";
        public string Description => "A test-only tool.";
        public JsonObject InputSchema => new() { ["type"] = "object" };
        public bool IsReadOnly => true;
        public string DescribeCall(JsonObject arguments) => Name;
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Executions++;
            return Task.FromResult(ToolResult.Success("probe result"));
        }
    }

    private sealed class CapableProvider(ProviderCapabilities capabilities) : ILlmProvider, IProviderCapabilities
    {
        public string Id => "browser";
        public string DisplayName => "Browser";
        public ProviderCapabilities Capabilities => capabilities;
        public IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("This fixture only exposes capabilities.");
    }

    private sealed class DelayedProvider(bool supportsRetry) : ILlmProvider, IProviderCapabilities
    {
        public string Id => "browser";
        public string DisplayName => "Queued browser";
        public ProviderCapabilities Capabilities => new(false, false, false, false)
        {
            SupportsPostToolStallRetry = supportsRetry,
        };
        public int Calls { get; private set; }
        public int CancelledCalls { get; private set; }
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            try { await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken); }
            catch (OperationCanceledException) { CancelledCalls++; throw; }
            yield return new TextDeltaEvent("queued answer");
            yield return new ResponseCompletedEvent(false, new Usage(10, 2), StopReasons.EndTurn);
        }
    }

    private sealed class WrapperProvider(ILlmProvider inner) : ILlmProvider, IDecoratedProvider
    {
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public ILlmProvider InnerProvider => inner;
        public IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, CancellationToken cancellationToken)
            => inner.StreamChatAsync(request, cancellationToken);
    }
}
