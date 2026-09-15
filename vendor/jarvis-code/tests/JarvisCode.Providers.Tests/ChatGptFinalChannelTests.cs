using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.Providers.Tests;

// The registry is process-wide; share the existing non-parallel transport collection.
[Collection("ChatGptForeignToolTransport")]
public sealed class ChatGptFinalChannelTests : IDisposable
{
    private const string Action = """JARVIS_ACT {"name":"list_local_skills","arguments":{}}""";
    private const string Plan = "I'll inspect the installed local skills before building the website.";

    public void Dispose() => ChatGptTransportRegistry.Reset();

    [Fact]
    public async Task CommentaryBeforeFinalActionDispatchesAndTheLocalToolResultContinuesTheSameConversation()
    {
        var first = Conversation(
            Message("user", "build it"),
            Message("assistant", Plan, "commentary"),
            Message("assistant", "Need the available local skills.", "analysis"),
            Message("assistant", Action, "final"));
        var second = Conversation(
            Message("user", "build it"),
            Message("assistant", Plan, "commentary"),
            Message("assistant", Action, "final"),
            Message("user", "RESULT list_local_skills: local-skill"),
            Message("assistant", "The local skill list is available now.", "final"));
        var transport = new Transport(first, second) { RenderedText = Plan + "\n\n" + Action };
        var options = Register(transport);
        var request = Request();

        var events = await RunAsync(new ChatGptWebProvider(new Cookies(), options), request);

        Assert.Empty(events.OfType<TextDeltaEvent>());
        Assert.Empty(events.OfType<TextPreviewEvent>());
        var call = Assert.Single(events.OfType<ToolCallStartedEvent>());
        Assert.Equal("list_local_skills", call.Name);
        var arguments = Assert.Single(events.OfType<ToolCallArgumentsDeltaEvent>());
        Assert.Equal(call.Index, arguments.Index);
        Assert.Equal("{}", arguments.Delta);
        var completed = Assert.Single(events.OfType<ResponseCompletedEvent>());
        Assert.True(completed.WantsToolUse);
        Assert.Equal(StopReasons.ToolUse, completed.StopReason);
        Assert.Equal(1, Assert.Single(options.ChatGptChats).SentMessages);

        var continuation = request with
        {
            Messages =
            [
                .. request.Messages,
                new ChatMessage(Role.Assistant, [new ToolCallBlock(call.Id, call.Name, arguments.Delta)]),
                new ChatMessage(Role.User, [new ToolResultBlock(call.Id, call.Name, "local-skill", false)]),
            ],
        };
        // Re-instantiation also proves that only the accepted final was persisted for resumption.
        var next = await RunAsync(new ChatGptWebProvider(new Cookies(), options), continuation);

        Assert.Equal(2, transport.Asks.Count);
        Assert.Null(transport.Asks[0].ConversationId);
        Assert.Equal("conv-final", transport.Asks[1].ConversationId);
        Assert.Contains("RESULT list_local_skills: local-skill", transport.Asks[1].Prompt);
        Assert.DoesNotContain(Plan, transport.Asks[1].Prompt);
        Assert.Equal("The local skill list is available now.", Assert.Single(next.OfType<TextDeltaEvent>()).Delta);
        Assert.Empty(next.OfType<ToolCallStartedEvent>());
        Assert.Equal(StopReasons.EndTurn, Assert.Single(next.OfType<ResponseCompletedEvent>()).StopReason);
        Assert.Equal(2, Assert.Single(options.ChatGptChats).SentMessages);
    }

    [Theory]
    [InlineData("commentary")]
    [InlineData("analysis")]
    public async Task AControlLineOutsideFinalIsNotExecutedOrMergedIntoTheFinalAnswer(string channel)
    {
        var transport = new Transport(Conversation(
            Message("user", "build it"), Message("assistant", Action, channel),
            Message("assistant", "Please choose a local skill.", "final")));
        var options = Register(transport);

        var events = await RunAsync(new ChatGptWebProvider(new Cookies(), options), Request());

        Assert.Empty(events.OfType<ToolCallStartedEvent>());
        Assert.Equal("Please choose a local skill.", Assert.Single(events.OfType<TextDeltaEvent>()).Delta);
        Assert.False(Assert.Single(events.OfType<ResponseCompletedEvent>()).WantsToolUse);
    }

    public static IEnumerable<object[]> QuotedFinalActions()
    {
        yield return ["> " + Action];
        yield return ["```json\n" + Action + "\n```"];
        yield return ["`" + Action + "`"];
        yield return ["    " + Action];
        yield return ["Example only:\n" + Action];
    }

    [Theory]
    [MemberData(nameof(QuotedFinalActions))]
    public async Task ChannelSelectionDoesNotWeakenTheWholeAnswerActionEnvelope(string final)
    {
        var transport = new Transport(Conversation(
            Message("user", "build it"), Message("assistant", Plan, "commentary"),
            Message("assistant", final, "final")));
        var options = Register(transport);

        var events = await RunAsync(new ChatGptWebProvider(new Cookies(), options), Request());

        Assert.Empty(events.OfType<ToolCallStartedEvent>());
        Assert.Equal(final, Assert.Single(events.OfType<TextDeltaEvent>()).Delta);
        Assert.Equal(StopReasons.EndTurn, Assert.Single(events.OfType<ResponseCompletedEvent>()).StopReason);
    }

    [Theory]
    [InlineData("JARVIS_ACT {\"name\":\"list_local_skills\",\"arguments\":")]
    [InlineData("JARVIS_ACT {\"name\":\"list_local_skills\",\"arguments\":null}")]
    [InlineData("JARVIS_ACT {\"name\":\"list_local_skills\",\"arguments\":{},\"unexpected\":true}")]
    [InlineData("This trailing prose is not an action.")]
    public async Task AMalformedFinalBatchRejectsEvenItsValidPrefixBeforeAnyDispatch(string tail)
    {
        var malformed = Action + "\n" + tail;
        // Every correction must be verified against its own new user turn. Repeating the first
        // snapshot would test a stale-store failure, not exhaustion of malformed final batches.
        var snapshots = Enumerable.Range(1, 3).Select(turnCount => Conversation(
            Enumerable.Range(0, turnCount).SelectMany(turn => new[]
            {
                Message("user", turn == 0 ? "build it" : $"correct action format {turn}"),
                Message("assistant", Plan, "commentary"),
                Message("assistant", malformed, "final"),
            }).ToArray())).ToArray();
        var transport = new Transport(snapshots);
        var options = Register(transport);
        var events = new List<ProviderEvent>();

        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            RunAsync(new ChatGptWebProvider(new Cookies(), options), Request(), events));

        Assert.Contains("No actions were run", failure.Message);
        AssertNotAccepted(events, transport, options);
        Assert.Equal(3, transport.Asks.Count);
        Assert.Null(transport.Asks[0].ConversationId);
        Assert.All(transport.Asks.Skip(1), ask => Assert.Equal("conv-final", ask.ConversationId));
    }

    [Theory]
    [InlineData("commentary")]
    [InlineData("analysis")]
    [InlineData("notification")]
    public async Task AnExplicitNonFinalChannelCannotBorrowAnActionFromTheRenderedPage(string channel)
    {
        var transport = new Transport(Conversation(
            Message("user", "build it"), Message("assistant", Action, channel)))
        {
            RenderedText = Action,
        };
        var options = Register(transport);
        var events = new List<ProviderEvent>();

        await Assert.ThrowsAsync<ProviderException>(() =>
            RunAsync(new ChatGptWebProvider(new Cookies(), options), Request(), events));

        AssertNotAccepted(events, transport, options);
    }

    [Theory]
    [InlineData("commentary", "text", "Status only")]
    [InlineData("final", "text", "")]
    [InlineData("final", "execution_output", "Unrecognized non-text result")]
    public async Task ModernChannelMetadataPreventsFallingBackToALegacyActionInTheSameTurn(
        string channel, string contentType, string text)
    {
        var modern = Message("assistant", text, channel);
        modern["content"]!["content_type"] = contentType;
        var transport = new Transport(Conversation(
            Message("user", "build it"), Message("assistant", Action), modern)) { RenderedText = Action };
        var options = Register(transport);
        var events = new List<ProviderEvent>();

        await Assert.ThrowsAsync<ProviderException>(() =>
            RunAsync(new ChatGptWebProvider(new Cookies(), options), Request(), events));

        AssertNotAccepted(events, transport, options);
    }

    [Fact]
    public async Task ExplicitFinalExcludesLegacyTextInTheSameTurn()
    {
        var transport = new Transport(Conversation(
            Message("user", "build it"), Message("assistant", Plan), Message("assistant", Action, "final")));
        var options = Register(transport);

        var events = await RunAsync(new ChatGptWebProvider(new Cookies(), options), Request());

        Assert.Equal("list_local_skills", Assert.Single(events.OfType<ToolCallStartedEvent>()).Name);
        Assert.Empty(events.OfType<TextDeltaEvent>());
        Assert.Equal(StopReasons.ToolUse, Assert.Single(events.OfType<ResponseCompletedEvent>()).StopReason);
    }

    [Theory]
    [InlineData("container.exec")]
    [InlineData("api_tool.call_tool")]
    public async Task CommentaryToolRoutingStillRejectsForeignWorkBeforeDownloadsOrFinalActions(string recipient)
    {
        var foreign = Message("assistant", "Execute remotely", "commentary", recipient);
        foreign["content"]!["parts"]!.AsArray().Add(new JsonObject { ["asset_pointer"] = "sediment://file_foreign" });
        var transport = new Transport(Conversation(
            Message("user", "build it"), foreign, Message("assistant", Action, "final")));
        var options = Register(transport);
        var events = new List<ProviderEvent>();

        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            RunAsync(new ChatGptWebProvider(new Cookies(), options), Request(), events));

        Assert.Contains("own sandbox or connectors", failure.Message);
        AssertNotAccepted(events, transport, options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyUnchanneledRepliesRetainPlainTextAndStandaloneControlBehavior(bool action)
    {
        var expected = action ? Action : "Legacy answer.";
        var transport = new Transport(Conversation(Message("user", "build it"), Message("assistant", expected)));
        var options = Register(transport);

        var events = await RunAsync(new ChatGptWebProvider(new Cookies(), options), Request());

        Assert.Equal(action ? 1 : 0, events.OfType<ToolCallStartedEvent>().Count());
        if (action) Assert.Empty(events.OfType<TextDeltaEvent>());
        else Assert.Equal(expected, Assert.Single(events.OfType<TextDeltaEvent>()).Delta);
        Assert.Equal(action, Assert.Single(events.OfType<ResponseCompletedEvent>()).WantsToolUse);
    }

    [Fact]
    public async Task LegacyUnchanneledProseAndControlRemainProseRatherThanExecutingAnExample()
    {
        var expected = Plan + "\n\n" + Action;
        var transport = new Transport(Conversation(
            Message("user", "build it"), Message("assistant", Plan), Message("assistant", Action)));
        var options = Register(transport);

        var events = await RunAsync(new ChatGptWebProvider(new Cookies(), options), Request());

        Assert.Empty(events.OfType<ToolCallStartedEvent>());
        Assert.Equal(expected, Assert.Single(events.OfType<TextDeltaEvent>()).Delta);
    }

    [Fact]
    public void FinalChannelSelectionNeverReadsAnEarlierTurnOrAnAbandonedBranch()
    {
        var conversation = JsonNode.Parse(Conversation(
            Message("user", "earlier"), Message("assistant", Action, "final"),
            Message("user", "current"), Message("assistant", Plan, "commentary"),
            Message("assistant", "Current answer.", "final")))!.AsObject();
        conversation["mapping"]!["abandoned"] = new JsonObject
        {
            ["parent"] = "n2",
            ["message"] = Message("assistant", Action, "final", "container.exec"),
        };

        var turn = ChatGptWebProvider.ExactAnswer(conversation.ToJsonString(), 2);

        Assert.NotNull(turn);
        Assert.Equal("Current answer.", turn.Text);
        Assert.False(turn.UsedOwnTools);
        Assert.Null(ChatGptWebProvider.ExactAnswer(conversation.ToJsonString(), 3));
    }

    private static void AssertNotAccepted(IReadOnlyList<ProviderEvent> events, Transport transport, Options options)
    {
        Assert.Empty(events.OfType<TextDeltaEvent>());
        Assert.Empty(events.OfType<TextPreviewEvent>());
        Assert.Empty(events.OfType<ToolCallStartedEvent>());
        Assert.Empty(events.OfType<ToolCallArgumentsDeltaEvent>());
        Assert.Empty(events.OfType<ResponseCompletedEvent>());
        Assert.Empty(transport.SavedAssets);
        Assert.Empty(options.ChatGptChats);
    }

    private static Options Register(Transport transport)
    {
        ChatGptTransportRegistry.Register((_, _) => transport);
        return new Options();
    }

    private static async Task<List<ProviderEvent>> RunAsync(ChatGptWebProvider provider, LlmRequest request,
        List<ProviderEvent>? events = null)
    {
        events ??= [];
        await foreach (var item in provider.StreamChatAsync(request, CancellationToken.None)) events.Add(item);
        return events;
    }

    private static LlmRequest Request() => new()
    {
        ModelId = "auto",
        ConversationScopeId = "final-channel-fixture",
        SystemPrompt = "Use the local tools to build the requested project.",
        Messages = [new ChatMessage(Role.User, [new TextBlock("build it")])],
        Tools = [new ToolDefinition("list_local_skills", "List locally installed skills.", new JsonObject())],
    };

    private static JsonObject Message(string role, string text, string? channel = null, string recipient = "all")
    {
        var message = new JsonObject
        {
            ["author"] = new JsonObject { ["role"] = role },
            ["recipient"] = recipient,
            ["status"] = "finished_successfully",
            ["content"] = new JsonObject { ["content_type"] = "text", ["parts"] = new JsonArray(text) },
        };
        if (channel is not null) message["channel"] = channel;
        return message;
    }

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

    private sealed class Cookies : IApiKeySource
    {
        public string? GetKey(string providerId) => $"{ChatGptCookieJar.SessionTokenName}=fake-final-channel";
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

    private sealed class Transport(params string[] conversations) : IChatGptTransport
    {
        public List<ChatGptAsk> Asks { get; } = [];
        public List<string> SavedAssets { get; } = [];
        public string RenderedText { get; init; } = "Rendered text is not authoritative.";

        public Task<ChatGptResponse> SendAsync(HttpMethod method, string path, string? jsonBody,
            string? bearerToken, string accept, IReadOnlyDictionary<string, string>? extraHeaders,
            CancellationToken cancellationToken) => Task.FromResult(
                path == "/api/auth/session" ? new ChatGptResponse(200, """{"accessToken":"fake"}""")
                : method == HttpMethod.Get && path == "/backend-api/conversation/conv-final"
                    ? new ChatGptResponse(200, conversations[Math.Min(Asks.Count - 1, conversations.Length - 1)])
                    : new ChatGptResponse(200, "{}"));

        public Task<ChatGptUiReply> AskAsync(ChatGptAsk ask, CancellationToken cancellationToken)
        {
            Asks.Add(ask);
            ask.AnswerPreview?.Invoke(RenderedText);
            return Task.FromResult(new ChatGptUiReply(RenderedText, "conv-final", null));
        }

        public Task<string?> SaveAssetAsync(string assetId, string bearer, CancellationToken cancellationToken)
        {
            SavedAssets.Add(assetId);
            return Task.FromResult<string?>("C:/fixture/downloaded.png");
        }
    }
}
