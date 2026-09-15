using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Settings;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Hooks;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;
using JarvisCode.Providers;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.App.Tests.Services;

[CollectionDefinition("Browser auxiliary registry", DisableParallelization = true)]
public sealed class BrowserAuxiliaryRegistryCollection;

[Collection("Browser auxiliary registry")]
public sealed class BrowserAuxiliaryIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-aux-isolation-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _profiles = [];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserAdvisorIsRejectedBeforeReadingOrForwardingTheParentTranscript(bool decorated)
    {
        var browser = new RecordingProvider(false);
        using var services = CreateServices(Wrap(browser, decorated));
        var tool = new AdvisorTool(services, () => "fixture-model");
        var result = await tool.ExecuteAsync(new JsonObject(), new ToolExecutionContext
        {
            WorkingDirectory = _root,
            ConversationSnapshot = () => throw new InvalidOperationException("An unavailable advisor must not read the transcript."),
        }, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("API advisor model", result.Content);
        Assert.Empty(browser.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BrowserHookEvaluationFailsClosedWithoutSendingPayloadOrReadingTranscript(bool decorated, bool stop)
    {
        var browser = new RecordingProvider(false);
        var provider = Wrap(browser, decorated);
        using var services = CreateServices(provider);
        var evaluator = PromptHookEvaluation.Create(services, provider,
            new ModelInfo(provider.Id, "fixture-model", "Fixture model", 200000),
            () => throw new InvalidOperationException("An unavailable hook must not read the transcript."), _root);
        var verdict = await evaluator(new PromptHookRequest(stop ? HookEvent.Stop : HookEvent.PreToolUse,
            "Only proceed when the action is authorized.", "{\"command\":\"npm publish\"}", null,
            TimeSpan.FromSeconds(1), stop), CancellationToken.None);

        Assert.NotNull(verdict);
        Assert.False(verdict.Ok);
        Assert.False(verdict.Impossible);
        Assert.Contains("API hook model", verdict.Reason);
        Assert.Empty(browser.Requests);
    }

    [Fact]
    public async Task AnExplicitHookModelOverrideIsCheckedIndependentlyOfTheMainApiModel()
    {
        var api = new RecordingProvider(true, "main-api");
        var browser = new RecordingProvider(false);
        using var services = CreateServices(Wrap(browser, true));
        var evaluator = PromptHookEvaluation.Create(services, api,
            new ModelInfo(api.Id, "main-model", "Main API", 200000), () => [], _root);
        var verdict = await evaluator(new PromptHookRequest(HookEvent.PreToolUse, "Check permission", "{}",
            "fixture-model", TimeSpan.FromSeconds(1), false), CancellationToken.None);

        Assert.NotNull(verdict);
        Assert.False(verdict.Ok);
        Assert.Empty(api.Requests);
        Assert.Empty(browser.Requests);
    }

    [Fact]
    public async Task ApiAdvisorStillReceivesForwardedEvidenceWithNoTools()
    {
        var api = new RecordingProvider(true);
        using var services = CreateServices(api);
        var result = await new AdvisorTool(services, () => "fixture-model").ExecuteAsync(new JsonObject(),
            new ToolExecutionContext
            {
                WorkingDirectory = _root,
                ConversationSnapshot = () => [ChatMessage.FromUserText("Review the parser fix.")],
            }, CancellationToken.None);

        Assert.False(result.IsError);
        var request = Assert.Single(api.Requests);
        Assert.Empty(request.Tools);
        Assert.Contains("Review the parser fix.", string.Join("\n", request.Messages.Select(message => message.GetText())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsafePermissionClassifierAsksLocallyWithoutReviewingOrExecuting(bool decorated)
    {
        var browser = new RecordingProvider(false);
        var provider = Wrap(browser, decorated);
        var verdict = await AutoPermissionClassifier.ClassifyAsync(provider, "model",
            [ChatMessage.FromUserText("Fix and publish the project.")],
            new PermissionRequest(new NeverExecuteTool(), new JsonObject { ["command"] = "npm publish" }, "Publish"),
            _root, CancellationToken.None, standingAuthorization: "Allow routine coding actions.");

        Assert.Equal("ask", verdict.Decision);
        Assert.Contains("tool-free", verdict.Reason);
        Assert.Empty(browser.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsafeLiveSummaryNeverSendsTranscriptOrPersistsAResult(bool decorated)
    {
        var browser = new RecordingProvider(false);
        var store = new SessionSummaryStore(_root, "session");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.GenerateAsync(Wrap(browser, decorated),
            "model", [ChatMessage.FromUserText("Create the files and deploy them.")], CancellationToken.None));

        Assert.Contains("no browser prompt was sent", error.Message);
        Assert.Empty(browser.Requests);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task CachedSummaryRemainsReadableButCannotTriggerUnsafeRefresh()
    {
        var provider = new RecordingProvider(true);
        var store = new SessionSummaryStore(_root, "session");
        ChatMessage[] messages = [ChatMessage.FromUserText("Fix the parser.")];
        var cached = await store.GenerateAsync(provider, "model", messages, CancellationToken.None);
        provider.ToolFree = false;

        Assert.Equal(cached, await store.GenerateAsync(provider, "model", messages, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GenerateAsync(provider, "model", messages,
            CancellationToken.None, force: true));
        Assert.Single(provider.Requests);
        Assert.Equal(cached, store.Load());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MisroutedWebSearchDoesNotOpenAuxiliaryBrowserOrConsumeSearchBudget(bool decorated)
    {
        var browser = new RecordingProvider(false);
        var session = "search-isolation-" + Guid.NewGuid().ToString("N");
        var tool = new VendorWebSearchTool(Wrap(browser, decorated), "model", ThinkingEffort.High, session);
        var result = await tool.ExecuteAsync(new JsonObject { ["query"] = "Jarvis tools" },
            new ToolExecutionContext { WorkingDirectory = _root, SessionId = session }, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("isolated WebSearch", result.Content);
        Assert.Empty(browser.Requests);
        Assert.False(VendorWebSearchTool.SessionSearchCounts.ContainsKey(session));
        Assert.False(VendorWebSearchTool.IsEnabled(ChatGptWebProvider.ProviderId, "model", true));
    }

    [Fact]
    public async Task ApiWebSearchRetainsItsDeclaredServerSearchTool()
    {
        var api = new RecordingProvider(true);
        var tool = new VendorWebSearchTool(api, "claude-sonnet-5", ThinkingEffort.High, Guid.NewGuid().ToString("N"));
        var result = await tool.ExecuteAsync(new JsonObject { ["query"] = "Jarvis tools" },
            new ToolExecutionContext { WorkingDirectory = _root }, CancellationToken.None);

        Assert.False(result.IsError);
        var request = Assert.Single(api.Requests);
        Assert.Equal("web_search", request.BodyOverride!["tools"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownUnsafeConnectionProviderIsNotSentATestPrompt()
    {
        var browser = new RecordingProvider(false);
        var result = await ProviderConnectionCheck.RunAsync(new ProviderRegistry([Wrap(browser, true)]),
            ProviderConnectionPlan.ForApiKey(browser.Id, browser.DisplayName, "model"));

        Assert.Contains("No prompt was sent", result);
        Assert.Empty(browser.Requests);
    }

    [Fact]
    public async Task ApiConnectionCheckStillTestsARealResponse()
    {
        var api = new RecordingProvider(true);
        var result = await ProviderConnectionCheck.RunAsync(new ProviderRegistry([api]),
            ProviderConnectionPlan.ForApiKey(api.Id, api.DisplayName, "model"));

        Assert.StartsWith("Works", result);
        Assert.Equal(ProviderConnectionCheck.Prompt, Assert.Single(api.Requests).Messages[0].GetText());
    }

    [Theory]
    [InlineData("real", true)]
    [InlineData("empty", false)]
    [InlineData("auto", false)]
    [InlineData("namespaced-auto", false)]
    public async Task BrowserConnectionCheckReadsMetadataWithoutAskOrModelSelection(string modelSet, bool hasModels)
    {
        var transport = new MetadataTransport(modelSet);
        string? scope = null;
        ChatGptTransportRegistry.Register((cookies, _) => { scope = cookies.ScopeId; return transport; });
        try
        {
            var browser = new ChatGptWebProvider(new FixtureCookies(), new FixtureOptions());
            var result = await ProviderConnectionCheck.RunAsync(new ProviderRegistry([Wrap(browser, true)]),
                ProviderConnectionPlan.ForBrowserSession());

            Assert.Equal("connection-check", scope);
            Assert.Equal(1, transport.Reads);
            Assert.Contains("no prompt was sent", result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(hasModels ? "model inference was not tested" : "did not expose", result);
            Assert.DoesNotContain("signed-in", result, StringComparison.OrdinalIgnoreCase);
            if (!hasModels) Assert.DoesNotContain("is available", result);
        }
        finally { ChatGptTransportRegistry.Reset(); }
    }

    private static ILlmProvider Wrap(ILlmProvider provider, bool decorated) => decorated ? new Wrapper(provider) : provider;

    private AppServices CreateServices(ILlmProvider provider)
    {
        var paths = ProfilePaths.Create("aux-isolation-test-" + Guid.NewGuid().ToString("N"));
        _profiles.Add(paths.Root);
        var services = new AppServices(paths, headless: true, decorateProviders: _ => new ProviderRegistry([provider]));
        services.Settings.Current.CustomModels.Add(new(provider.Id, "fixture-model", "Fixture model", 200000));
        return services;
    }

    private sealed class RecordingProvider(bool supportsToolFree, string id = "fixture-provider") : ILlmProvider, IProviderCapabilities
    {
        public bool ToolFree { get; set; } = supportsToolFree;
        public ProviderCapabilities Capabilities => new() { SupportsToolFreeInference = ToolFree };
        public string Id => id;
        public string DisplayName => "Fixture provider";
        public List<LlmRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new TextDeltaEvent("ready");
            yield return new ResponseCompletedEvent(false, Usage.Zero, StopReasons.EndTurn);
        }
    }

    private sealed class Wrapper(ILlmProvider inner) : ILlmProvider, IDecoratedProvider
    {
        public ILlmProvider InnerProvider => inner;
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, CancellationToken cancellationToken) =>
            inner.StreamChatAsync(request, cancellationToken);
    }

    private sealed class NeverExecuteTool : ITool
    {
        public string Name => "PowerShell";
        public string Description => "Fixture command";
        public JsonObject InputSchema => new();
        public bool IsReadOnly => false;
        public string DescribeCall(JsonObject arguments) => "Fixture command";
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Permission review must not execute an action.");
    }

    private sealed class MetadataTransport(string modelSet) : IChatGptTransport
    {
        public int Reads { get; private set; }
        public Task<ChatGptComposerControls> ReadComposerControlsAsync(string? modelLabel, bool includeEfforts,
            CancellationToken cancellationToken)
        {
            Reads++;
            Assert.Null(modelLabel);
            Assert.False(includeEfforts);
            IReadOnlyList<ChatGptPickerOption> models = modelSet switch
            {
                "real" => [new ChatGptPickerOption("auto", "Auto"), new ChatGptPickerOption("Latest", "Latest")],
                "auto" => [new ChatGptPickerOption("auto", "Auto")],
                "namespaced-auto" => [new ChatGptPickerOption("chatgpt-web:auto", "Auto")],
                _ => [],
            };
            return Task.FromResult(new ChatGptComposerControls("Latest", models, []));
        }
        public Task<ChatGptResponse> SendAsync(HttpMethod method, string path, string? jsonBody, string? bearerToken,
            string accept, IReadOnlyDictionary<string, string>? extraHeaders, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Connection metadata must not call the backend.");
        public Task<ChatGptUiReply> AskAsync(ChatGptAsk ask, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Connection metadata must not submit a prompt.");
    }

    private sealed class FixtureCookies : IApiKeySource
    {
        public string? GetKey(string providerId) => "__Secure-next-auth.session-token=fixture-only";
    }

    private sealed class FixtureOptions : IChatGptWebOptions
    {
        public string ChatGptProjectName => "";
        public string ChatGptProjectId => "";
        public int ChatGptRotateAfterMessages => 100;
        public void SaveChatGptProjectId(string projectId) => throw new InvalidOperationException("No project may be created.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        foreach (var profile in _profiles)
            if (Directory.Exists(profile)) Directory.Delete(profile, true);
    }
}
