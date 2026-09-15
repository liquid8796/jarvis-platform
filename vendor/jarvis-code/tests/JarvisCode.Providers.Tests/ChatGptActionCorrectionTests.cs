using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.Providers.Tests;

[Collection("ChatGptForeignToolTransport")]
public sealed class ChatGptActionCorrectionTests : IDisposable
{
    private const string Todo = """JARVIS_ACT {"name":"todo_write","arguments":{"todos":[]}}""";
    private const string Agent = """JARVIS_ACT {"name":"Agent","arguments":{"prompt":"Inspect the local files only.","cwd":"D:\\fixture\\project"}}""";
    private const string ValidBatch = Todo + "\n" + Agent;
    // The reported production failure: the first action is valid, but Agent has one extra brace.
    private const string InvalidBatch = ValidBatch + "}";
    private const string ConversationId = "conv-action-correction";

    public void Dispose() => ChatGptTransportRegistry.Reset();

    [Fact]
    public async Task ExtraBraceInSecondActionCorrectsTheWholeBatchBeforeEitherActionIsDispatched()
    {
        var events = new List<ProviderEvent>();
        var transport = new Transport(new Reply(InvalidBatch), new Reply(ValidBatch))
        {
            OnAsk = (count, _) =>
            {
                if (count > 1) AssertNoAcceptedOutput(events);
            },
        };
        var options = Register(transport);

        await RunAsync(new ChatGptWebProvider(new Cookies(), options), Request(), events);

        Assert.Equal(2, transport.Asks.Count);
        Assert.Equal(new[] { 1, 2 }, transport.Asks.Select(ask => ask.ExpectedUserMessageCount));
        Assert.All(transport.Asks, ask =>
        {
            Assert.True(ask.EnforceLocalExecution);
            Assert.NotNull(ask.ProvenanceAccessToken);
            Assert.Null(ask.AnswerPreview);
        });
        Assert.Null(transport.Asks[0].ConversationId);
        var correction = transport.Asks[1];
        Assert.Equal(ConversationId, correction.ConversationId);
        Assert.DoesNotContain(InvalidBatch, correction.Prompt);
        Assert.Equal(new[] { "todo_write", "Agent" }, events.OfType<ToolCallStartedEvent>().Select(call => call.Name));
        Assert.Equal(2, events.OfType<ToolCallArgumentsDeltaEvent>().Count());
        Assert.Equal("D:\\fixture\\project", JsonNode.Parse(events.OfType<ToolCallArgumentsDeltaEvent>().Last().Delta)!["cwd"]!.GetValue<string>());
        Assert.Empty(events.OfType<TextDeltaEvent>());
        Assert.Empty(events.OfType<TextPreviewEvent>());
        Assert.True(Assert.Single(events.OfType<ResponseCompletedEvent>()).WantsToolUse);
        Assert.Equal(2, Assert.Single(options.ChatGptChats).SentMessages);
        Assert.Empty(transport.SavedAssets);
    }

    [Fact]
    public async Task RepeatedMalformedRepliesStopAfterTwoCorrectionsWithoutDispatchingAValidPrefix()
    {
        var transport = new Transport(new Reply(InvalidBatch), new Reply(InvalidBatch), new Reply(InvalidBatch));
        var options = Register(transport);
        var events = new List<ProviderEvent>();

        await Assert.ThrowsAsync<ProviderException>(() =>
            RunAsync(new ChatGptWebProvider(new Cookies(), options), Request(), events));

        Assert.Equal(3, transport.Asks.Count);
        Assert.All(transport.Asks.Skip(1), ask => Assert.Equal(ConversationId, ask.ConversationId));
        AssertNotAccepted(events, transport, options);
    }

    [Fact]
    public async Task InProgressStoredFinalCatchesUpBeforeAnyFormatCorrectionIsRequested()
    {
        var transport = new Transport(new Reply(ValidBatch))
        {
            TransformConversation = (read, body) =>
            {
                if (read != 1) return body;
                var conversation = JsonNode.Parse(body)!;
                var current = conversation["current_node"]!.GetValue<string>();
                var final = conversation["mapping"]![current]!["message"]!;
                final["status"] = "in_progress";
                final["end_turn"] = false;
                final["content"]!["parts"] = new JsonArray(InvalidBatch[..^12]);
                return conversation.ToJsonString();
            },
        };
        var options = Register(transport);

        var events = await RunAsync(new ChatGptWebProvider(new Cookies(), options), Request());

        Assert.Single(transport.Asks);
        Assert.Equal(2, transport.ConversationReads);
        Assert.Equal(new[] { "todo_write", "Agent" }, events.OfType<ToolCallStartedEvent>().Select(call => call.Name));
        Assert.Single(events.OfType<ResponseCompletedEvent>());
        Assert.Equal(1, Assert.Single(options.ChatGptChats).SentMessages);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UsageIncludesEveryRejectedOutputAndCorrectionContext(int corrections)
    {
        var replies = Enumerable.Repeat(new Reply(InvalidBatch), corrections).Append(new Reply(ValidBatch)).ToArray();
        var transport = new Transport(replies);
        var options = Register(transport);
        var request = Request();

        var events = await RunAsync(new ChatGptWebProvider(new Cookies(), options), request);

        var usage = Assert.Single(events.OfType<ResponseCompletedEvent>()).Usage;
        var normal = ChatGptWebProvider.EstimatedUsage(request, ValidBatch);
        var rejected = ChatGptWebProvider.EstimatedUsage(request, InvalidBatch);
        Assert.Equal(normal.OutputTokens + corrections * rejected.OutputTokens, usage.OutputTokens);
        Assert.True(usage.InputTokens > normal.InputTokens * (corrections + 1));
        Assert.True(usage.IsEstimated);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationAfterStoredReplyCannotSendAgainDispatchOrAcceptHistory(int cancelAfterAsk)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new Transport(new Reply(InvalidBatch), new Reply(ValidBatch))
        {
            OnConversationRead = count =>
            {
                if (count == cancelAfterAsk) cancellation.Cancel();
            },
        };
        var options = Register(transport);
        var events = new List<ProviderEvent>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(new ChatGptWebProvider(new Cookies(), options), Request(), events, cancellation.Token));

        Assert.Equal(cancelAfterAsk, transport.Asks.Count);
        AssertNotAccepted(events, transport, options);
    }

    [Fact]
    public async Task CancellationDuringActiveCorrectionWaitsForCleanupWithoutDispatchOrHistory()
    {
        using var cancellation = new CancellationTokenSource();
        var correctionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var correctionCleanedUp = false;
        var transport = new Transport(new Reply(InvalidBatch), new Reply(ValidBatch))
        {
            OnAskAsync = async (count, token) =>
            {
                if (count != 2) return;
                correctionStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { correctionCleanedUp = true; }
            },
        };
        var options = Register(transport);
        var events = new List<ProviderEvent>();
        var running = RunAsync(new ChatGptWebProvider(new Cookies(), options), Request(), events, cancellation.Token);
        await correctionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.True(correctionCleanedUp);
        Assert.Equal(2, transport.Asks.Count);
        AssertNotAccepted(events, transport, options);
    }

    [Fact]
    public async Task CorrectedBatchPersistsRemoteMessageCountAndToolResultsResumeAfterProviderRestart()
    {
        var transport = new Transport(new Reply(InvalidBatch), new Reply(InvalidBatch),
            new Reply(ValidBatch), new Reply("The local checks are complete."));
        var options = Register(transport);
        var request = Request();
        var events = await RunAsync(new ChatGptWebProvider(new Cookies(), options), request);
        Assert.Equal(3, Assert.Single(options.ChatGptChats).SentMessages);
        var calls = events.OfType<ToolCallStartedEvent>().ToList();
        var arguments = events.OfType<ToolCallArgumentsDeltaEvent>().ToDictionary(item => item.Index);
        Assert.Equal(2, calls.Count);
        var continuation = request with
        {
            Messages =
            [
                .. request.Messages,
                new ChatMessage(Role.Assistant, [.. calls.Select(call =>
                    new ToolCallBlock(call.Id, call.Name, arguments[call.Index].Delta))]),
                new ChatMessage(Role.User, [.. calls.Select(call =>
                    new ToolResultBlock(call.Id, call.Name, "local fixture result", false))]),
            ],
        };

        var continued = await RunAsync(new ChatGptWebProvider(new Cookies(), options), continuation);

        Assert.Equal(4, transport.Asks.Count);
        Assert.Equal(ConversationId, transport.Asks[3].ConversationId);
        Assert.Contains("RESULT todo_write: local fixture result", transport.Asks[3].Prompt);
        Assert.Contains("RESULT Agent: local fixture result", transport.Asks[3].Prompt);
        Assert.DoesNotContain(InvalidBatch, transport.Asks[3].Prompt);
        Assert.Equal("The local checks are complete.", Assert.Single(continued.OfType<TextDeltaEvent>()).Delta);
        Assert.Empty(continued.OfType<ToolCallStartedEvent>());
        Assert.False(Assert.Single(continued.OfType<ResponseCompletedEvent>()).WantsToolUse);
        Assert.Equal(4, Assert.Single(options.ChatGptChats).SentMessages);
    }

    [Theory]
    [InlineData(ReplyKind.NoExact)]
    [InlineData(ReplyKind.NoFinal)]
    [InlineData(ReplyKind.ForeignTool)]
    [InlineData(ReplyKind.ChangedConversationId)]
    public async Task CorrectedReplyMustStillPassStoredFinalAndExecutionRouteVerification(ReplyKind kind)
    {
        var transport = new Transport(new Reply(InvalidBatch), new Reply(ValidBatch, kind));
        var options = Register(transport);
        var events = new List<ProviderEvent>();

        await Assert.ThrowsAsync<ProviderException>(() =>
            RunAsync(new ChatGptWebProvider(new Cookies(), options), Request(), events));

        Assert.Equal(2, transport.Asks.Count);
        AssertNotAccepted(events, transport, options);
    }

    [Fact]
    public async Task MalformedBatchAssetsAreNotDownloadedWhileItsActionsAwaitCorrection()
    {
        var transport = new Transport(new Reply(InvalidBatch, ReplyKind.AttachedAsset), new Reply(ValidBatch));
        var options = Register(transport);

        var events = await RunAsync(new ChatGptWebProvider(new Cookies(), options), Request());

        Assert.Equal(2, transport.Asks.Count);
        Assert.Equal(2, events.OfType<ToolCallStartedEvent>().Count());
        Assert.Empty(transport.SavedAssets);
    }

    [Fact]
    public async Task CorrectionDoesNotReattachHistoricalOrCurrentImages()
    {
        var transport = new Transport(new Reply(InvalidBatch), new Reply(ValidBatch));
        var options = Register(transport);
        var request = Request() with
        {
            Messages = [new ChatMessage(Role.User,
                [new TextBlock("inspect the local project"), new ImageBlock("image/png", "FIXTURE-IMAGE")])],
        };

        await RunAsync(new ChatGptWebProvider(new Cookies(), options), request);

        Assert.Equal("FIXTURE-IMAGE", Assert.Single(transport.Asks[0].Images).Base64);
        Assert.Empty(transport.Asks[1].Images);
        Assert.Equal("", transport.Asks[1].ImagesOmittedNote);
    }

    [Theory]
    [InlineData(InvalidBatch)]
    [InlineData(ValidBatch)]
    [InlineData("JARVIS_ACT {broken")]
    public async Task ToollessChatTreatsActionSyntaxAsLiteralTextWithoutCorrections(string text)
    {
        var transport = new Transport(new Reply(text));
        var options = Register(transport);
        var events = new List<ProviderEvent>();

        await RunAsync(new ChatGptWebProvider(new Cookies(), options), Request() with { Tools = [] }, events);

        Assert.Single(transport.Asks);
        Assert.Equal(text, Assert.Single(events.OfType<TextDeltaEvent>()).Delta);
        Assert.Empty(events.OfType<ToolCallStartedEvent>());
        Assert.False(Assert.Single(events.OfType<ResponseCompletedEvent>()).WantsToolUse);
        Assert.Single(options.ChatGptChats);
    }

    [Fact]
    public async Task ProseInsteadOfTheCorrectedBatchCannotBeAcceptedAsSuccessfulCompletion()
    {
        var transport = new Transport(new Reply(InvalidBatch), new Reply("Done. The project is complete."),
            new Reply("Done. The project is complete."));
        var options = Register(transport);
        var events = new List<ProviderEvent>();

        await Assert.ThrowsAsync<ProviderException>(() =>
            RunAsync(new ChatGptWebProvider(new Cookies(), options), Request(), events));

        Assert.InRange(transport.Asks.Count, 2, 3);
        AssertNotAccepted(events, transport, options);
    }

    private static void AssertNoAcceptedOutput(IReadOnlyList<ProviderEvent> events)
    {
        Assert.Empty(events.OfType<TextDeltaEvent>());
        Assert.Empty(events.OfType<TextPreviewEvent>());
        Assert.Empty(events.OfType<ToolCallStartedEvent>());
        Assert.Empty(events.OfType<ToolCallArgumentsDeltaEvent>());
        Assert.Empty(events.OfType<ResponseCompletedEvent>());
    }

    private static void AssertNotAccepted(IReadOnlyList<ProviderEvent> events, Transport transport, Options options)
    {
        AssertNoAcceptedOutput(events);
        Assert.Empty(transport.SavedAssets);
        Assert.Empty(options.ChatGptChats);
    }

    private static Options Register(Transport transport)
    {
        ChatGptTransportRegistry.Register((_, _) => transport);
        return new Options();
    }

    private static async Task<List<ProviderEvent>> RunAsync(ChatGptWebProvider provider, LlmRequest request,
        List<ProviderEvent>? events = null, CancellationToken cancellationToken = default)
    {
        events ??= [];
        await foreach (var item in provider.StreamChatAsync(request, cancellationToken)) events.Add(item);
        return events;
    }

    private static LlmRequest Request() => new()
    {
        ModelId = "auto",
        ConversationScopeId = "action-correction-fixture",
        SystemPrompt = "Use local tools for the requested project.",
        Messages = [new ChatMessage(Role.User, [new TextBlock("inspect the local project")])],
        Tools =
        [
            new ToolDefinition("todo_write", "Update the local task list.", new JsonObject()),
            new ToolDefinition("Agent", "Delegate a local inspection.", new JsonObject()),
        ],
    };

    public enum ReplyKind { Final, NoExact, NoFinal, ForeignTool, ChangedConversationId, AttachedAsset }
    private sealed record Reply(string Text, ReplyKind Kind = ReplyKind.Final);

    private sealed class Cookies : IApiKeySource
    {
        public string? GetKey(string providerId) => $"{ChatGptCookieJar.SessionTokenName}=fake-action-correction";
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

    private sealed class Transport(params Reply[] replies) : IChatGptTransport
    {
        public List<ChatGptAsk> Asks { get; } = [];
        public List<string> SavedAssets { get; } = [];
        public bool CarriesImages => true;
        public Action<int, CancellationToken>? OnAsk { get; init; }
        public Func<int, CancellationToken, Task>? OnAskAsync { get; init; }
        public Action<int>? OnConversationRead { get; init; }
        public Func<int, string, string>? TransformConversation { get; init; }
        public int ConversationReads { get; private set; }

        public Task<ChatGptResponse> SendAsync(HttpMethod method, string path, string? jsonBody,
            string? bearerToken, string accept, IReadOnlyDictionary<string, string>? extraHeaders,
            CancellationToken cancellationToken)
        {
            if (path == "/api/auth/session")
                return Task.FromResult(new ChatGptResponse(200, """{"accessToken":"fake"}"""));
            if (method != HttpMethod.Get || path != $"/backend-api/conversation/{ConversationId}")
                return Task.FromResult(new ChatGptResponse(200, "{}"));
            ConversationReads++;
            var body = Conversation();
            body = TransformConversation?.Invoke(ConversationReads, body) ?? body;
            var response = replies[Asks.Count - 1].Kind == ReplyKind.NoExact
                ? new ChatGptResponse(403, "{}") : new ChatGptResponse(200, body);
            OnConversationRead?.Invoke(Asks.Count);
            return Task.FromResult(response);
        }

        public async Task<ChatGptUiReply> AskAsync(ChatGptAsk ask, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Asks.Add(ask);
            OnAsk?.Invoke(Asks.Count, cancellationToken);
            Assert.True(Asks.Count <= replies.Length, "The provider sent more correction requests than the fixture allowed.");
            if (OnAskAsync is not null) await OnAskAsync(Asks.Count, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var reply = replies[Asks.Count - 1];
            ask.AnswerPreview?.Invoke(reply.Text);
            return new ChatGptUiReply(reply.Text,
                reply.Kind == ReplyKind.ChangedConversationId ? "unexpected-conversation" : ConversationId, null);
        }

        public Task<string?> SaveAssetAsync(string assetId, string bearer, CancellationToken cancellationToken)
        {
            SavedAssets.Add(assetId);
            return Task.FromResult<string?>("C:/fixture/unexpected-download.png");
        }

        private string Conversation()
        {
            var messages = new List<JsonObject>();
            for (var index = 0; index < Asks.Count; index++)
            {
                messages.Add(Message("user", Asks[index].Prompt));
                if (replies[index].Kind == ReplyKind.ForeignTool)
                {
                    var foreign = Message("assistant", "Remote execution", "commentary", "container.exec");
                    foreign["content"]!["parts"]!.AsArray().Add(new JsonObject { ["asset_pointer"] = "sediment://foreign-result" });
                    messages.Add(foreign);
                }
                if (replies[index].Kind == ReplyKind.AttachedAsset)
                {
                    var asset = Message("tool", "Attached image");
                    asset["author"]!["name"] = "image_gen";
                    asset["content"]!["parts"]!.AsArray().Add(new JsonObject { ["asset_pointer"] = "sediment://rejected-batch-result" });
                    messages.Add(asset);
                }
                var final = Message("assistant", replies[index].Text,
                    replies[index].Kind == ReplyKind.NoFinal ? "commentary" : "final");
                messages.Add(final);
            }
            var mapping = new JsonObject();
            for (var index = 0; index < messages.Count; index++)
                mapping[$"n{index}"] = new JsonObject
                {
                    ["parent"] = index == 0 ? null : $"n{index - 1}",
                    ["message"] = messages[index],
                };
            return new JsonObject { ["current_node"] = $"n{messages.Count - 1}", ["mapping"] = mapping }.ToJsonString();
        }

        private static JsonObject Message(string role, string text, string? channel = null, string recipient = "all")
        {
            var message = new JsonObject
            {
                ["author"] = new JsonObject { ["role"] = role },
                ["recipient"] = recipient,
                ["status"] = "finished_successfully",
                ["end_turn"] = role == "assistant" && channel == "final",
                ["content"] = new JsonObject { ["content_type"] = "text", ["parts"] = new JsonArray(text) },
            };
            if (channel is not null) message["channel"] = channel;
            return message;
        }
    }
}
