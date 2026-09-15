using System.Runtime.CompilerServices;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Tests.Agent;

public sealed class TerminalProviderFailureTests
{
    private const string OverflowFailure = "terminal boundary: quoted 429; prompt is too long: 200001 tokens > 200000 maximum";
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_classification_survives_the_agent_loop(bool canRetry)
    {
        var provider = new FailureProvider(canRetry);
        var message = ChatMessage.FromUserText("Only use the approved local tools.");
        var context = new AgentTurnContext
        {
            Provider = provider,
            ModelId = "fixture",
            SystemPrompt = "fixture",
            Messages = new List<ChatMessage> { message },
            Tools = new ToolRegistry([]),
            PermissionGate = new AutoApprovePermissionGate(),
            ToolContext = new ToolExecutionContext { WorkingDirectory = Path.GetTempPath() },
        };
        var events = new List<AgentEvent>();

        await foreach (var evt in new AgentOrchestrator().RunTurnAsync(context, default)) events.Add(evt);

        var completed = Assert.Single(events.OfType<TurnCompleted>());
        Assert.Equal(TurnEndReason.Error, completed.Reason);
        Assert.Equal(canRetry, completed.CanRetry);
        Assert.Equal(1, provider.Calls);
        Assert.Same(message, Assert.Single(context.Messages));
    }

    [Fact]
    public void Existing_provider_errors_and_turn_results_remain_retryable_by_default()
    {
        Assert.True(new ProviderException("ordinary API error").CanRetry);
        Assert.True(new TurnCompleted(TurnEndReason.Error, "ordinary API error").CanRetry);
    }

    [Fact]
    public async Task Terminal_compaction_error_is_not_retried_as_context_overflow()
    {
        var provider = new FailureProvider(false, OverflowFailure);
        var messages = History();
        var original = messages.ToArray();
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            new ConversationCompactor().CompactAsync(provider, "fixture", messages, default));
        Assert.False(error.CanRetry);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(original, messages);
    }

    [Fact]
    public async Task Ordinary_compaction_overflow_retains_its_existing_retry_path()
    {
        var provider = new SummaryRecoveryProvider();
        var result = await new ConversationCompactor().CompactAsync(provider, "fixture", History(), default);
        Assert.Equal(2, provider.Calls);
        Assert.Contains("recovered summary", result.Summary);
    }

    [Fact]
    public async Task Terminal_automatic_compaction_error_does_not_continue_into_a_main_request()
    {
        var provider = new FailureProvider(false, OverflowFailure);
        var messages = History();
        var context = Context(provider, messages) with { AutoCompact = new AutoCompactOptions(200_000, 190_000) };
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentOrchestrator().RunTurnAsync(context, default)) events.Add(evt);
        var end = Assert.Single(events.OfType<TurnCompleted>());
        Assert.False(end.CanRetry);
        Assert.Equal(TurnEndReason.Error, end.Reason);
        Assert.Equal(1, provider.Calls);
        Assert.Empty(events.OfType<ConversationCompacted>());
    }

    [Fact]
    public async Task Terminal_precomputation_error_disables_future_background_attempts()
    {
        var provider = new FailureProvider(false, OverflowFailure);
        var store = new PrecomputeStore();
        try
        {
            store.Arm(provider, "fixture", History(), false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (store.Status == PrecomputeStatus.Running) await Task.Delay(10, timeout.Token);
            Assert.False(store.CanArm);
            store.Arm(provider, "fixture", History(), false);
            Assert.Equal(1, store.Attempts);
            Assert.Equal(1, provider.Calls);
        }
        finally { store.Cancel(); }
    }

    [Fact]
    public async Task Terminal_error_after_stall_cancellation_is_not_replayed()
    {
        var provider = new FailureProvider(false, OverflowFailure, delayed: true);
        var context = Context(provider,
        [
            ChatMessage.FromUserText("local task"),
            new(Role.Assistant, [new ToolCallBlock("read-1", "Read", "{}")]),
            ChatMessage.FromToolResults([new ToolResultBlock("read-1", "Read", "local result", false)]),
        ]) with { PostToolStallTimeout = TimeSpan.FromMilliseconds(5) };
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentOrchestrator().RunTurnAsync(context, default)) events.Add(evt);
        var end = Assert.Single(events.OfType<TurnCompleted>());
        Assert.False(end.CanRetry);
        Assert.Equal(TurnEndReason.Error, end.Reason);
        Assert.Equal(1, provider.Calls);
    }

    private static List<ChatMessage> History() =>
    [
        ChatMessage.FromUserText("Keep the full local-only constraint."),
        new(Role.Assistant, [new TextBlock("I will use local tools.")]),
        ChatMessage.FromUserText("Keep working locally."),
        new(Role.Assistant, [new TextBlock("Current work.")]),
    ];

    private static AgentTurnContext Context(ILlmProvider provider, List<ChatMessage> messages) => new()
    {
        Provider = provider, ModelId = "fixture", SystemPrompt = "main fixture", Messages = messages,
        Tools = new ToolRegistry([]), PermissionGate = new AutoApprovePermissionGate(),
        ToolContext = new ToolExecutionContext { WorkingDirectory = Path.GetTempPath() },
    };

    private sealed class FailureProvider(bool canRetry, string message = "terminal fixture failure", bool delayed = false) : ILlmProvider
    {
        public string Id => "fixture";
        public string DisplayName => "Fixture";
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            if (delayed) await Task.Delay(40); // Deliberately expose a terminal error after cancellation.
            else cancellationToken.ThrowIfCancellationRequested();
            if (Calls > 0) throw new ProviderException(message) { CanRetry = canRetry };
            yield break;
        }
    }

    private sealed class SummaryRecoveryProvider : ILlmProvider
    {
        public string Id => "fixture";
        public string DisplayName => "Fixture";
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Calls == 1) throw new ProviderException("prompt is too long: 200001 tokens > 200000 maximum");
            yield return new TextDeltaEvent("<summary>recovered summary</summary>");
            yield return new ResponseCompletedEvent(false, new Usage(10, 2), "end_turn");
        }
    }
}
