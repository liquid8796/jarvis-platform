using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.Providers.Tests;

// Other browser-provider fixtures use the process-wide transport registry. A non-parallel
// collection prevents either fixture class from replacing the other's transport mid-request.
[CollectionDefinition("ChatGptForeignToolTransport", DisableParallelization = true)]
public sealed class ChatGptForeignToolTransportCollection;

[Collection("ChatGptForeignToolTransport")]
public sealed class ChatGptForeignToolTests : IDisposable
{
    private const string Action = """JARVIS_ACT {"name":"Write","arguments":{"path":"index.html","content":"local"}}""";

    public void Dispose() => ChatGptTransportRegistry.Reset();

    [Theory]
    [InlineData("container")]
    [InlineData("container.exec")]
    [InlineData("python")]
    [InlineData("python.exec")]
    [InlineData("api_tool")]
    [InlineData("api_tool.call_tool")]
    [InlineData("containerish.exec")]
    [InlineData("python_docs")]
    [InlineData("api_toolbox.lookup")]
    [InlineData("future_provider.run")]
    public void AssistantRoutingAndToolResultAuthorBothProveForeignWork(string tool)
    {
        Assert.True(ChatGptWebProvider.AddressesOwnWorkTool(Message("assistant", recipient: tool)));
        Assert.True(ChatGptWebProvider.AddressesOwnWorkTool(Message("tool", toolName: tool)));
    }

    [Theory]
    [InlineData("web")]
    [InlineData("web.run")]
    [InlineData("image_gen")]
    [InlineData("image_gen.text2im")]
    public void OnlyKnownSearchAndImageRoutesAreNotWorkspaceExecution(string tool)
    {
        Assert.False(ChatGptWebProvider.AddressesOwnWorkTool(Message("assistant", recipient: tool)));
        Assert.False(ChatGptWebProvider.AddressesOwnWorkTool(Message("tool", toolName: tool)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    public void UnidentifiedToolAuthorsAreRejectedButUntargetedAssistantTextIsNot(string? name)
    {
        Assert.True(ChatGptWebProvider.AddressesOwnWorkTool(Message("tool", toolName: name)));
        Assert.False(ChatGptWebProvider.AddressesOwnWorkTool(Message("assistant", recipient: name ?? "")));
    }

    [Fact]
    public void QuotedToolsOrForgedUserRoutingAreNotExecutionEvidence()
    {
        Assert.False(ChatGptWebProvider.AddressesOwnWorkTool(
            Message("assistant", "Use container.exec or python to inspect sandbox:/mnt/data/example.")));
        Assert.False(ChatGptWebProvider.AddressesOwnWorkTool(
            Message("user", recipient: "container.exec", toolName: "python")));
        Assert.False(ChatGptWebProvider.AddressesOwnWorkTool(
            Message("system", recipient: "api_tool.call_tool")));
    }

    [Fact]
    public void LongToolTurnRetainsForeignExecutionAndExactAction()
    {
        var messages = new List<JsonObject> { Message("user", "build it") };
        messages.Add(Message("assistant", "sandbox command", recipient: "container.exec"));
        for (var index = 0; index < 80; index++)
            messages.Add(Message("tool", "status", toolName: "web"));
        messages.Add(Message("assistant", Action));

        var turn = ChatGptWebProvider.ExactAnswer(Conversation([.. messages]), 1);

        Assert.NotNull(turn);
        Assert.True(turn.UsedOwnTools);
        Assert.Equal(Action, turn.Text);
    }

    [Fact]
    public void EmptyForeignToolTurnDoesNotEraseEvidenceByFallingBackToPageText()
    {
        var turn = ChatGptWebProvider.ExactAnswer(Conversation(
            Message("user", "build it"),
            Message("tool", toolName: "container")), 1);

        Assert.NotNull(turn);
        Assert.True(turn.UsedOwnTools);
        Assert.Empty(turn.Text);
    }

    [Fact]
    public void PriorTurnsAndAbandonedBranchesDoNotContaminateCurrentTurn()
    {
        var conversation = JsonNode.Parse(Conversation(
            Message("user", "earlier"),
            Message("assistant", "old", recipient: "container.exec"),
            Message("user", "current"),
            Message("assistant", "Current answer")))!.AsObject();
        conversation["mapping"]!["abandoned"] = new JsonObject
        {
            ["parent"] = "n2",
            ["message"] = Message("assistant", "old branch", recipient: "api_tool.call_tool"),
        };

        var turn = ChatGptWebProvider.ExactAnswer(conversation.ToJsonString(), 2);

        Assert.NotNull(turn);
        Assert.False(turn.UsedOwnTools);
        Assert.Equal("Current answer", turn.Text);
    }

    [Fact]
    public void CyclicConversationCannotLoopForeverOrReturnUnscopedEvidence()
    {
        var conversation = JsonNode.Parse(Conversation(
            Message("user", "build it"), Message("assistant", "answer")))!.AsObject();
        conversation["mapping"]!["n1"]!["parent"] = "n1";
        Assert.Null(ChatGptWebProvider.ExactAnswer(conversation.ToJsonString(), 1));
    }

    [Theory]
    [InlineData("container.exec", true)]
    [InlineData("python", true)]
    [InlineData("functions.exec", true)]
    [InlineData("future_agent.execute", true)]
    [InlineData("web.run", false)]
    [InlineData("image_gen.text2im", false)]
    public void InflightGuardFindsNativeRoutingBeforeFinalExists(string recipient, bool expected)
    {
        var working = Message("assistant", "still processing", recipient);
        working["channel"] = "analysis";
        working["status"] = "in_progress";
        working["end_turn"] = false;
        var conversation = Conversation(Message("user", "current task"), working);

        Assert.Equal(expected, ChatGptWebProvider.CurrentTurnUsesOwnWorkTools(conversation, 1));
        Assert.False(ChatGptWebProvider.CurrentTurnUsesOwnWorkTools(conversation, 0));
        Assert.False(ChatGptWebProvider.CurrentTurnUsesOwnWorkTools(conversation, 2));
    }

    [Fact]
    public void InflightGuardUsesOnlyTheExpectedCurrentTurnAndActiveBranch()
    {
        var conversation = JsonNode.Parse(Conversation(
            Message("user", "prior task"), Message("tool", toolName: "container"),
            Message("user", "current task"), Message("assistant", "still thinking")))!.AsObject();
        conversation["mapping"]!["abandoned"] = new JsonObject
        {
            ["parent"] = "n2",
            ["message"] = Message("assistant", recipient: "container.exec"),
        };

        Assert.False(ChatGptWebProvider.CurrentTurnUsesOwnWorkTools(conversation.ToJsonString(), 2));
        Assert.False(ChatGptWebProvider.CurrentTurnUsesOwnWorkTools(conversation.ToJsonString(), 1));
        conversation["current_node"] = "abandoned";
        Assert.True(ChatGptWebProvider.CurrentTurnUsesOwnWorkTools(conversation.ToJsonString(), 2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("{\"mapping\":{},\"current_node\":\"missing\"}")]
    public void UnreadableInflightStateIsNotExecutionEvidence(string conversation)
        => Assert.False(ChatGptWebProvider.CurrentTurnUsesOwnWorkTools(conversation, 1));

    [Fact]
    public void CyclicInflightStateCannotAcceptUnscopedNativeEvidence()
    {
        var conversation = JsonNode.Parse(Conversation(
            Message("user", "task"), Message("assistant", recipient: "container.exec")))!;
        conversation["mapping"]!["n0"]!["parent"] = "n1";
        Assert.False(ChatGptWebProvider.CurrentTurnUsesOwnWorkTools(conversation.ToJsonString(), 1));
    }

    [Fact]
    public void MissingAncestorCannotProvideCompleteInflightExecutionEvidence()
    {
        var conversation = JsonNode.Parse(Conversation(
            Message("user", "task"), Message("assistant", recipient: "container.exec")))!;
        conversation["mapping"]!["n0"]!["parent"] = "not-present";
        Assert.False(ChatGptWebProvider.CurrentTurnUsesOwnWorkTools(conversation.ToJsonString(), 1));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ForeignTurnIsRejectedBeforeAssetsActionsTextOrHistoryAreAccepted(bool action, bool withTools)
    {
        var asset = Message("tool", toolName: "container");
        asset["content"]!["parts"] = new JsonArray(new JsonObject { ["asset_pointer"] = "sediment://file_foreign" });
        var transport = new Transport(Conversation(
            Message("user", "build it"), asset,
            Message("assistant", action ? Action : "Built the entire project in my sandbox.")));
        ChatGptTransportRegistry.Register((_, _) => transport);
        var options = new Options();
        var events = new List<ProviderEvent>();

        var failure = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var item in new ChatGptWebProvider(new Cookies(), options)
                .StreamChatAsync(Request(withTools), CancellationToken.None)) events.Add(item);
        });

        Assert.Contains("own sandbox or connectors", failure.Message);
        Assert.False(failure.CanRetry);
        Assert.DoesNotContain("directory is untouched", failure.Message);
        Assert.Empty(transport.SavedAssets);
        Assert.Empty(events.OfType<TextDeltaEvent>());
        Assert.Empty(events.OfType<ToolCallStartedEvent>());
        Assert.Empty(events.OfType<ResponseCompletedEvent>());
        Assert.Empty(options.ChatGptChats);
    }

    [Fact]
    public async Task ForeignEmptyStoredTurnCannotAcceptRenderedSuccess()
    {
        var transport = new Transport(Conversation(
            Message("user", "build it"), Message("tool", toolName: "python")));
        ChatGptTransportRegistry.Register((_, _) => transport);
        await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var _ in new ChatGptWebProvider(new Cookies(), new Options())
                .StreamChatAsync(Request(withTools: true), CancellationToken.None)) { }
        });
    }

    [Theory]
    [InlineData("web", false)]
    [InlineData("web", true)]
    [InlineData("image_gen", false)]
    [InlineData("image_gen", true)]
    public async Task KnownNativeSearchOrImagesRetainTheirBehavior(string tool, bool withTools)
    {
        var result = Message("tool", toolName: tool);
        result["content"]!["parts"] = new JsonArray(new JsonObject { ["asset_pointer"] = "sediment://file_image" });
        var transport = new Transport(Conversation(
            Message("user", "answer"), result, Message("assistant", "Here it is.")));
        ChatGptTransportRegistry.Register((_, _) => transport);
        var options = new Options();
        var events = new List<ProviderEvent>();

        await foreach (var item in new ChatGptWebProvider(new Cookies(), options)
            .StreamChatAsync(Request(withTools), CancellationToken.None)) events.Add(item);

        Assert.Equal("file_image", Assert.Single(transport.SavedAssets));
        Assert.Contains("Here it is.", Assert.Single(events.OfType<TextDeltaEvent>()).Delta);
        Assert.Single(events.OfType<ResponseCompletedEvent>());
        Assert.Single(options.ChatGptChats);
    }

    [Fact]
    public async Task LocalActionWithoutForeignExecutionStillDispatches()
    {
        var transport = new Transport(Conversation(Message("user", "build it"), Message("assistant", Action)));
        ChatGptTransportRegistry.Register((_, _) => transport);
        var events = new List<ProviderEvent>();
        await foreach (var item in new ChatGptWebProvider(new Cookies(), new Options())
            .StreamChatAsync(Request(withTools: true), CancellationToken.None)) events.Add(item);
        Assert.Equal("Write", Assert.Single(events.OfType<ToolCallStartedEvent>()).Name);
        Assert.Single(events.OfType<ResponseCompletedEvent>());
    }

    [Fact]
    public async Task CodeNeverDispatchesRenderedActionsWhenStoredTurnCannotBeVerified()
    {
        var transport = new Transport("{}") { RenderedText = Action };
        ChatGptTransportRegistry.Register((_, _) => transport);
        var events = new List<ProviderEvent>();
        var failure = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var item in new ChatGptWebProvider(new Cookies(), new Options())
                .StreamChatAsync(Request(withTools: true), CancellationToken.None)) events.Add(item);
        });

        Assert.Contains("stored response could not be verified", failure.Message);
        Assert.False(failure.CanRetry);
        Assert.Empty(events.OfType<ToolCallStartedEvent>());
        Assert.Empty(events.OfType<TextPreviewEvent>());
        Assert.Empty(events.OfType<ResponseCompletedEvent>());
    }

    [Fact]
    public async Task VerifiedImageOnlyTurnNeverBorrowsRenderedActionText()
    {
        var result = Message("tool", toolName: "image_gen");
        result["content"]!["parts"] = new JsonArray(new JsonObject { ["asset_pointer"] = "sediment://file_image" });
        var transport = new Transport(Conversation(Message("user", "draw it"), result)) { RenderedText = Action };
        ChatGptTransportRegistry.Register((_, _) => transport);
        var events = new List<ProviderEvent>();
        await foreach (var item in new ChatGptWebProvider(new Cookies(), new Options())
            .StreamChatAsync(Request(withTools: true), CancellationToken.None)) events.Add(item);

        Assert.Empty(events.OfType<ToolCallStartedEvent>());
        Assert.DoesNotContain("JARVIS_ACT", Assert.Single(events.OfType<TextDeltaEvent>()).Delta);
        Assert.Equal("file_image", Assert.Single(transport.SavedAssets));
        Assert.Single(events.OfType<ResponseCompletedEvent>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnverifiedAnswerPreviewsAreDisabledForEveryRequest(bool withTools)
    {
        var transport = new Transport(Conversation(Message("user", "answer"), Message("assistant", "Verified answer")));
        ChatGptTransportRegistry.Register((_, _) => transport);
        var events = new List<ProviderEvent>();
        await foreach (var item in new ChatGptWebProvider(new Cookies(), new Options())
            .StreamChatAsync(Request(withTools), CancellationToken.None)) events.Add(item);

        Assert.False(transport.PreviewRequested);
        Assert.Empty(events.OfType<TextPreviewEvent>());
        var ask = Assert.Single(transport.Asks);
        Assert.True(ask.EnforceLocalExecution);
        Assert.Equal(1, ask.ExpectedUserMessageCount);
        Assert.Equal("fake", ask.ProvenanceAccessToken!());
        Assert.DoesNotContain("fake", ask.ToString());
        if (!withTools)
        {
            Assert.Contains(ChatGptToolProtocol.TextOnlyBoundary, ask.Prompt);
            Assert.DoesNotContain(ChatGptToolProtocol.Marker, ask.Prompt);
        }
        Assert.Equal("Verified answer", Assert.Single(events.OfType<TextDeltaEvent>()).Delta);
    }

    [Fact]
    public async Task PlainChatCannotAcceptRenderedFallbackWhenStoredTurnCannotBeVerified()
    {
        var transport = new Transport("{}");
        ChatGptTransportRegistry.Register((_, _) => transport);
        var events = new List<ProviderEvent>();
        await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var item in new ChatGptWebProvider(new Cookies(), new Options())
                .StreamChatAsync(Request(withTools: false), CancellationToken.None)) events.Add(item);
        });
        Assert.Empty(events.OfType<TextDeltaEvent>());
        Assert.Empty(events.OfType<ResponseCompletedEvent>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrowserAskFailuresAreTerminalEvenWhenTheirNativeProvenanceIsUnknown(bool nativeViolation)
    {
        var transport = new Transport("{}") { NativeViolation = nativeViolation, Failure = "fixture transport failure" };
        var options = new Options();
        ChatGptTransportRegistry.Register((_, _) => transport);
        var failure = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var _ in new ChatGptWebProvider(new Cookies(), options)
                .StreamChatAsync(Request(withTools: false), default)) { }
        });
        Assert.False(failure.CanRetry);
        Assert.Single(transport.Asks);
        Assert.Empty(options.ChatGptChats);
        Assert.Empty(transport.SavedAssets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedAskExceptionsCannotBypassTerminalReplayPolicy(bool asynchronously)
    {
        var cause = new InvalidOperationException("fixture cleanup failed after sending");
        var transport = new Transport("{}") { AskException = cause, FailAsynchronously = asynchronously };
        var options = new Options();
        ChatGptTransportRegistry.Register((_, _) => transport);
        var events = new List<ProviderEvent>();
        var failure = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var item in new ChatGptWebProvider(new Cookies(), options)
                .StreamChatAsync(Request(withTools: true), default)) events.Add(item);
        });
        Assert.False(failure.CanRetry);
        Assert.Same(cause, failure.InnerException);
        Assert.Single(transport.Asks);
        Assert.Empty(events.OfType<ToolCallStartedEvent>());
        Assert.Empty(events.OfType<ResponseCompletedEvent>());
        Assert.Empty(options.ChatGptChats);
        Assert.Empty(transport.SavedAssets);
    }

    private static JsonObject Message(string role, string text = "", string recipient = "all", string? toolName = null) => new()
    {
        ["author"] = new JsonObject { ["role"] = role, ["name"] = toolName },
        ["recipient"] = recipient,
        ["content"] = new JsonObject { ["content_type"] = "text", ["parts"] = new JsonArray(text) },
    };

    private static string Conversation(params JsonObject[] messages)
    {
        var mapping = new JsonObject();
        for (var index = 0; index < messages.Length; index++)
            mapping[$"n{index}"] = new JsonObject
            {
                ["parent"] = index == 0 ? null : $"n{index - 1}",
                ["message"] = messages[index],
            };
        return new JsonObject { ["current_node"] = $"n{messages.Length - 1}", ["mapping"] = mapping }.ToJsonString();
    }

    private static LlmRequest Request(bool withTools) => new()
    {
        ModelId = "auto",
        ConversationScopeId = "foreign-tool-fixture",
        SystemPrompt = "be helpful",
        Messages = [new ChatMessage(Role.User, [new TextBlock("build it")])],
        Tools = withTools ? [new ToolDefinition("Write", "write a file", new JsonObject())] : [],
    };

    private sealed class Cookies : IApiKeySource
    {
        public string? GetKey(string providerId) => $"{ChatGptCookieJar.SessionTokenName}=fake";
    }

    private sealed class Options : IChatGptWebOptions
    {
        public string ChatGptProjectName => "";
        public string ChatGptProjectId => "";
        public int ChatGptRotateAfterMessages => 100;
        public IReadOnlyList<ChatGptChatEntry> ChatGptChats { get; private set; } = [];
        public void SaveChatGptProjectId(string projectId) { }
        public void SaveChatGptChats(IReadOnlyList<ChatGptChatEntry> chats) => ChatGptChats = chats;
    }

    private sealed class Transport(string conversation) : IChatGptTransport
    {
        public List<ChatGptAsk> Asks { get; } = [];
        public List<string> SavedAssets { get; } = [];
        public string RenderedText { get; init; } = "Rendered success";
        public bool PreviewRequested { get; private set; }
        public string? Failure { get; init; }
        public bool NativeViolation { get; init; }
        public Exception? AskException { get; init; }
        public bool FailAsynchronously { get; init; }

        public Task<ChatGptResponse> SendAsync(HttpMethod method, string path, string? jsonBody,
            string? bearerToken, string accept, IReadOnlyDictionary<string, string>? extraHeaders,
            CancellationToken cancellationToken) => Task.FromResult(
                path == "/api/auth/session" ? new ChatGptResponse(200, """{"accessToken":"fake"}""")
                : method == HttpMethod.Get && path == "/backend-api/conversation/conv-foreign"
                    ? new ChatGptResponse(200, conversation) : new ChatGptResponse(200, "{}"));

        public Task<ChatGptUiReply> AskAsync(ChatGptAsk ask, CancellationToken cancellationToken)
        {
            Asks.Add(ask);
            if (AskException is not null)
            {
                if (FailAsynchronously) return Task.FromException<ChatGptUiReply>(AskException);
                throw AskException;
            }
            if (Failure is not null)
                return Task.FromResult(ChatGptUiReply.Failed(Failure) with { NativeToolViolation = NativeViolation });
            PreviewRequested = ask.AnswerPreview is not null;
            ask.AnswerPreview?.Invoke("Unverified preview");
            return Task.FromResult(new ChatGptUiReply(RenderedText, "conv-foreign", null));
        }

        public Task<string?> SaveAssetAsync(string assetId, string bearer, CancellationToken cancellationToken)
        {
            SavedAssets.Add(assetId);
            return Task.FromResult<string?>("C:/fixture/image.png");
        }
    }
}
