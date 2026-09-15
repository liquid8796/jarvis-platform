using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using JarvisCode.Cli;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.Cli.Tests;

[Collection("repl")]
public sealed class ChatGptCliIntegrationTests
{
    private static LlmRequest Request() => new()
    {
        ConversationScopeId = "cli-session", ModelId = "browser-model", SystemPrompt = "system",
        Messages = [ChatMessage.FromUserText("hello")],
    };

    [Fact]
    public async Task Browser_transport_registration_is_lazy_and_disposed_with_the_cli()
    {
        ChatGptTransportRegistry.Reset();
        using var host = new CliBrowserSessionHost(Path.Combine(Path.GetTempPath(), "jarvis-cli-browser-test"));
        var inner = new StubProvider();
        var registry = host.Wrap(new ProviderRegistry([inner]));
        var provider = registry.Get(ChatGptWebProvider.ProviderId);
        Assert.False(host.IsStarted);
        Assert.False(ProviderCapabilities.For(provider).SupportsThinkingEffort);
        Assert.Same(inner, ProviderDecorators.Unwrap(provider));
        await foreach (var _ in provider.StreamChatAsync(Request(), default)) { }
        Assert.True(host.IsStarted);
        Assert.True(ChatGptTransportRegistry.HasBrowserTransport);
        Assert.Equal("cli-session", inner.Seen?.ConversationScopeId);
        host.Dispose();
        Assert.False(ChatGptTransportRegistry.HasBrowserTransport);
    }

    [Fact]
    public void Api_providers_are_not_wrapped_or_started()
    {
        using var host = new CliBrowserSessionHost(Path.GetTempPath());
        var provider = new StubProvider { Id = "openai" };
        Assert.Same(provider, host.Wrap(new ProviderRegistry([provider])).Get("openai"));
        Assert.False(host.IsStarted);
    }

    [Fact]
    public void Browser_previews_are_transient_snapshots_not_sdk_text_deltas()
    {
        using var writer = new StringWriter();
        var output = new StreamJson(writer, "session");
        var partial = new PartialJsonMessage(output);
        partial.Begin(Request());
        partial.Observe(new TextPreviewEvent("draft"));
        partial.Observe(new TextPreviewEvent("changed draft"));
        partial.Observe(new TextPreviewEvent(""));
        partial.Observe(new TextDeltaEvent("authoritative"));
        partial.Observe(new ResponseCompletedEvent(false, new Usage(10, 1) { IsEstimated = true }));
        var rows = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(3, rows.Count(row => row["subtype"]?.GetValue<string>() == "answer_preview"));
        Assert.All(rows.Take(3), row => Assert.True(row["transient"]!.GetValue<bool>()));
        var deltas = rows.Where(row => row["event"]?["type"]?.GetValue<string>() == "content_block_delta").ToArray();
        Assert.Equal("authoritative", Assert.Single(deltas)["event"]!["delta"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Estimated_usage_cannot_be_charged_or_admitted_under_a_dollar_budget()
    {
        var model = new ModelInfo(ChatGptWebProvider.ProviderId, "browser-model", "Browser", 128000, 10, 30);
        var usage = new Usage(100, 20) { IsEstimated = true };
        Assert.Null(CliUsageLedger.Price(model, usage, null));
        var provider = new StubProvider();
        var ledger = new CliUsageLedger((_, _) => model, 1m);
        var error = await Assert.ThrowsAsync<CliError>(async () =>
        {
            await foreach (var _ in ledger.RunAsync(provider, Request(), default)) { }
        });
        Assert.Contains("estimated tokens", error.Message);
        Assert.Null(provider.Seen);
    }

    [Fact]
    public void Result_distinguishes_estimated_and_measured_usage()
    {
        using var writer = new StringWriter();
        var output = new StreamJson(writer, "session");
        JsonObject Result(Usage usage) => output.BuildResult(false, "success", "ok", 1, 1, 1, usage,
            "browser-model", ChatGptWebProvider.ProviderId, 128000, 0, "completed");
        Assert.True(Result(new Usage(10, 2) { IsEstimated = true })["usage"]!["is_estimated"]!.GetValue<bool>());
        Assert.Null(Result(new Usage(10, 2))["usage"]!["is_estimated"]);
    }

    private sealed class StubProvider : ILlmProvider, IProviderCapabilities
    {
        public string Id { get; init; } = ChatGptWebProvider.ProviderId;
        public string DisplayName => "Browser stub";
        public ProviderCapabilities Capabilities => new(false, false, false, false);
        public LlmRequest? Seen { get; private set; }
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Seen = request;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TextDeltaEvent("ok");
            yield return new ResponseCompletedEvent(false, new Usage(10, 1) { IsEstimated = true });
        }
    }
}
