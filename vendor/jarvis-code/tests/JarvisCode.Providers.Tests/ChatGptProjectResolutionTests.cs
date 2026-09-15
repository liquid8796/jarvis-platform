using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Utilities;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.Providers.Tests;

/// <summary>
/// Which project a chat lands in, across restarts. The resolved id is durably pinned and verified
/// directly on a process's first use; getting either that verification or a replacement lookup
/// wrong means the user's chats can split across two projects with the same name.
///
/// One class so the shared transport registry is never swapped mid-test: xUnit serialises the
/// tests inside a class, and nothing else in this assembly registers a transport.
/// </summary>
public sealed class ChatGptProjectResolutionTests : IDisposable
{
    private const string ProjectSidebarPath =
        "/backend-api/gizmos/snorlax/sidebar?owned_only=true&conversations_per_gizmo=0&limit=50";

    private const string Sidebar = """
        {"items":[{"gizmo":{"gizmo":{"id":"g-p-existing","display":{"name":"Jarvis"}}}}],"cursor":null}
        """;

    public void Dispose() => ChatGptTransportRegistry.Reset();

    private sealed class StubTransport : IChatGptTransport
    {
        public List<string> Calls { get; } = [];

        public Dictionary<string, ChatGptResponse> Responses { get; } = [];

        /// <summary>
        /// Dynamic backend answers for project pagination and repeated reads. Null falls through to
        /// the ordinary canned-response/store behaviour below.
        /// </summary>
        public Func<HttpMethod, string, string?, ChatGptResponse?>? ResponseHandler { get; set; }

        // Ordinary project/routing fixtures have a matching stored answer. Explicit
        // Responses/ConversationReads above always win, including deliberately bad reads.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _storedReplies = new();

        public string? AskedGizmo { get; private set; }

        public string? AskedConversation { get; private set; }

        public string? AskedModel { get; private set; }

        public string? AskedPrompt { get; private set; }

        /// <summary>Replies to hand back in order; the last one repeats once they run out.</summary>
        public Queue<string> Replies { get; } = [];

        /// <summary>Where a saved file is said to have landed, or null for one that would not come.</summary>
        public string? AssetPath { get; set; }

        public List<string> SavedAssets { get; } = [];

        public Task<string?> SaveAssetAsync(string assetId, string bearer, CancellationToken cancellationToken)
        {
            SavedAssets.Add(assetId);
            return Task.FromResult(AssetPath);
        }

        /// <summary>Conversation reads in order, for a store that catches up a moment late.</summary>
        public Queue<ChatGptResponse> ConversationReads { get; } = [];

        public Task<ChatGptResponse> SendAsync(
            HttpMethod method,
            string path,
            string? jsonBody,
            string? bearerToken,
            string accept,
            IReadOnlyDictionary<string, string>? extraHeaders,
            CancellationToken cancellationToken)
        {
            Calls.Add($"{method} {path}");
            if (ResponseHandler?.Invoke(method, path, jsonBody) is { } handled)
            {
                return Task.FromResult(handled);
            }

            if (ConversationReads.Count > 0 && method == HttpMethod.Get &&
                path.StartsWith("/backend-api/conversation/", StringComparison.Ordinal))
            {
                return Task.FromResult(ConversationReads.Dequeue());
            }

            if (Responses.TryGetValue(path, out var canned))
            {
                return Task.FromResult(canned);
            }

            // Older fixtures predate the sidebar's required query. Keeping their one-page answer
            // under the path alone lets the focused pagination tests own the exact query contract.
            if (path.StartsWith("/backend-api/gizmos/snorlax/sidebar?", StringComparison.Ordinal)
                && Responses.TryGetValue("/backend-api/gizmos/snorlax/sidebar", out canned))
            {
                return Task.FromResult(canned);
            }

            if (method == HttpMethod.Get && _storedReplies.TryGetValue(path, out var stored))
                return Task.FromResult(new ChatGptResponse(200, stored));

            return Task.FromResult(path == "/api/auth/session"
                ? new ChatGptResponse(200, """{"accessToken":"token"}""")
                : new ChatGptResponse(200, "{}"));
        }

        /// <summary>Whether this stand-in claims it can attach pictures to a message.</summary>
        public bool CarriesImages { get; set; }

        public IReadOnlyList<ChatGptImage> AskedImages { get; private set; } = [];

        public System.Collections.Concurrent.ConcurrentQueue<ChatGptAsk> Asks { get; } = new();

        public Func<ChatGptAsk, CancellationToken, Task<ChatGptUiReply>>? AskHandler { get; set; }

        public async Task<ChatGptUiReply> AskAsync(ChatGptAsk ask, CancellationToken cancellationToken)
        {
            Calls.Add("ASK");
            AskedModel = ask.ModelLabel;
            AskedGizmo = ask.GizmoId;
            AskedConversation = ask.ConversationId;
            AskedPrompt = ask.Prompt;
            AskedImages = ask.Images;
            Asks.Enqueue(ask);
            var reply = AskHandler is { } handler
                ? await handler(ask, cancellationToken)
                : new ChatGptUiReply(Replies.Count > 0 ? Replies.Dequeue() : "an answer", "conv-1", null);
            if (reply.ConversationId is { Length: > 0 } conversationId && reply.Error is null)
            {
                var previous = Enumerable.Range(1, Math.Max(0, ask.ExpectedUserMessageCount - 1))
                    .Select(index => ($"Earlier fixture prompt {index}", "an answer"));
                _storedReplies[$"/backend-api/conversation/{conversationId}"] =
                    StoredConversation([.. previous, (ask.Prompt, reply.Text)]);
            }
            return reply;
        }
    }

    private sealed class Cookies(string session = "fake") : IApiKeySource
    {
        public string? GetKey(string providerId) => $"{ChatGptCookieJar.SessionTokenName}={session}";
    }

    private sealed class Options(string name, string projectId, int rotateAfter) : IChatGptWebOptions
    {
        public string ChatGptProjectName { get; set; } = name;

        public string ChatGptProjectId { get; private set; } = projectId;

        public int ChatGptRotateAfterMessages => rotateAfter;

        public string? Saved { get; private set; }

        public List<string> SavedProjectIds { get; } = [];

        /// <summary>Stands in for the settings file, so a "restart" is a second provider over this.</summary>
        public IReadOnlyList<JarvisCode.Core.Settings.ChatGptChatEntry> ChatGptChats { get; private set; } = [];

        public bool FailSaving { get; set; }

        public Func<string, CancellationToken, ValueTask<IAsyncDisposable?>>? ScopeLeaseFactory { get; set; }

        public ValueTask<IAsyncDisposable?> AcquireChatGptScopeAsync(string scopeKey, CancellationToken cancellationToken) =>
            ScopeLeaseFactory?.Invoke(scopeKey, cancellationToken) ?? ValueTask.FromResult<IAsyncDisposable?>(null);

        public void SaveChatGptChats(IReadOnlyList<JarvisCode.Core.Settings.ChatGptChatEntry> chats)
        {
            if (FailSaving) throw new IOException("settings are read only");
            ChatGptChats = chats;
        }

        public void SaveChatGptProjectId(string id)
        {
            Saved = id;
            ChatGptProjectId = id;
            SavedProjectIds.Add(id);
        }
    }

    private static LlmRequest Ask(params string[] turns) => new()
    {
        ModelId = "auto",
        ConversationScopeId = "test-session",
        SystemPrompt = "system",
        Messages =
        [
            .. turns.Select((text, i) =>
                new ChatMessage(i % 2 == 0 ? Role.User : Role.Assistant, [new TextBlock(text)])),
        ],
    };

    private static async Task<string> RunAsync(ChatGptWebProvider provider, LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var text = "";
        await foreach (var e in provider.StreamChatAsync(request, cancellationToken))
        {
            if (e is TextDeltaEvent delta)
            {
                text += delta.Delta;
            }
        }

        return text;
    }

    /// <summary>
    /// The thread of a chat used to live in the provider and die with it, so reopening a session
    /// opened a second ChatGPT chat and pasted the whole conversation into it.
    /// </summary>
    [Fact]
    public async Task AChatIsPickedUpAgainAfterARestart()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("", "", 100);

        await RunAsync(new ChatGptWebProvider(new Cookies(), options), Ask("hello"));
        Assert.Null(stub.AskedConversation);

        // A second provider over the same stored settings is what a restart looks like.
        await RunAsync(
            new ChatGptWebProvider(new Cookies(), options), Ask("hello", "an answer", "again"));

        Assert.Equal("conv-1", stub.AskedConversation);
        Assert.Equal("again\n\n" + ChatGptToolProtocol.TextOnlyBoundary, stub.AskedPrompt);
    }

    /// <summary>
    /// A chat is filed under the conversation up to this message, so every message files it under
    /// a new key. Only the newest can match again — an older one means the session was rewound —
    /// so the store holds chats rather than a row per message.
    /// </summary>
    [Fact]
    public async Task EachMessageReplacesItsChatsOwnRowRatherThanAddingOne()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("", "", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);

        await RunAsync(provider, Ask("hello"));
        await RunAsync(provider, Ask("hello", "an answer", "again"));
        await RunAsync(provider, Ask("hello", "an answer", "again", "an answer", "and again"));

        Assert.Equal("conv-1", stub.AskedConversation);
        Assert.Single(options.ChatGptChats);
        Assert.Equal(3, options.ChatGptChats[0].SentMessages);
    }

    /// <summary>
    /// A turn can leave more than one user-side message behind — a task notification arriving while
    /// the session is idle, then the prompt the user types. The chat is filed under what the next
    /// turn will recompute, so those have to be hashed one per message rather than as the one run
    /// they are sent in; folding them cost the chat on the very next message.
    /// </summary>
    [Fact]
    public async Task AChatSurvivesATurnThatCarriedTwoUserMessages()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("", "", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);

        ChatMessage User(string text) => new(Role.User, [new TextBlock(text)]);
        ChatMessage Assistant(string text) => new(Role.Assistant, [new TextBlock(text)]);

        await RunAsync(provider, new LlmRequest
        {
            ConversationScopeId = "test-session",
            ModelId = "auto",
            SystemPrompt = "system",
            Messages = [User("a notification"), User("and the prompt")],
        });
        Assert.Null(stub.AskedConversation);

        await RunAsync(provider, new LlmRequest
        {
            ConversationScopeId = "test-session",
            ModelId = "auto",
            SystemPrompt = "system",
            Messages = [User("a notification"), User("and the prompt"), Assistant("an answer"), User("carry on")],
        });

        Assert.Equal("conv-1", stub.AskedConversation);
    }

    /// <summary>
    /// Rotation, end to end through the provider: once a chat has taken the configured number of
    /// messages it is deleted and the next message opens a fresh one. The count is what this app
    /// sent, so it takes that many turns to reach — which is the point, and why it is driven here
    /// rather than asserted about a field.
    /// </summary>
    [Fact]
    public async Task AChatIsDeletedAndReopenedOnceItHasTakenItsShare()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var provider = new ChatGptWebProvider(new Cookies(), new Options("", "", 20));

        // Turn N sends the Nth message into the same chat, so the conversation grows by a
        // user/assistant pair each time — which is what makes turn N+1 resume rather than restart.
        var messages = new List<ChatMessage>();
        var resumed = new List<string?>();
        for (var turn = 1; turn <= 21; turn++)
        {
            messages.Add(new ChatMessage(Role.User, [new TextBlock($"message {turn}")]));
            await RunAsync(provider, new LlmRequest
            {
                ConversationScopeId = "test-session",
                ModelId = "auto",
                SystemPrompt = "system",
                Messages = [.. messages],
            });
            resumed.Add(stub.AskedConversation);
            messages.Add(new ChatMessage(Role.Assistant, [new TextBlock("an answer")]));
        }

        // The first message opens a chat; the next nineteen continue it.
        Assert.Null(resumed[0]);
        Assert.All(resumed.Skip(1).Take(19), conversation => Assert.Equal("conv-1", conversation));

        // The twenty-first finds a chat that has taken its twenty and starts over: the old one is
        // deleted, and the message goes into a new chat rather than the one just abandoned.
        Assert.Contains("DELETE /backend-api/conversation/id/conv-1", stub.Calls);
        Assert.Null(resumed[20]);
    }

    /// <summary>Nothing is deleted while the chat is still inside its share.</summary>
    [Fact]
    public async Task AChatUnderItsShareIsLeftAlone()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var provider = new ChatGptWebProvider(new Cookies(), new Options("", "", 20));

        await RunAsync(provider, Ask("hello"));
        await RunAsync(provider, Ask("hello", "an answer", "again"));

        Assert.DoesNotContain(stub.Calls, call => call.StartsWith("DELETE", StringComparison.Ordinal));
    }

    /// <summary>A chat belongs to the account that holds it; other cookies cannot see it.</summary>
    [Fact]
    public async Task AnotherAccountDoesNotResumeThisOnesChat()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("", "", 100);

        await RunAsync(new ChatGptWebProvider(new Cookies("first"), options), Ask("hello"));
        await RunAsync(
            new ChatGptWebProvider(new Cookies("second"), options), Ask("hello", "an answer", "again"));

        Assert.Null(stub.AskedConversation);
    }

    [Fact]
    public async Task ARememberedProjectIdIsVerifiedOnceThenReusedFromMemory()
    {
        var stub = new StubTransport();
        stub.Responses["/backend-api/gizmos/g-p-remembered"] = new ChatGptResponse(200,
            """{"gizmo":{"id":"g-p-remembered","display":{"name":"Jarvis"}}}""");
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("Jarvis", "g-p-remembered", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);

        await RunAsync(provider, Ask("hello"));
        await RunAsync(provider, Ask("hello", "an answer", "again"));

        Assert.Equal("g-p-remembered", stub.AskedGizmo);
        Assert.Equal(1, stub.Calls.Count(c => c == "GET /backend-api/gizmos/g-p-remembered"));
        Assert.DoesNotContain(stub.Calls, c => c.Contains("sidebar"));
        Assert.DoesNotContain(stub.Calls, c => c == "POST /backend-api/projects");
    }

    [Fact]
    public async Task TheMemoryFastPathObservesAnotherProcessReplacingTheDurablePin()
    {
        var stub = new StubTransport();
        stub.Responses["/backend-api/gizmos/g-p-first"] = new ChatGptResponse(200,
            """{"gizmo":{"id":"g-p-first","display":{"name":"Jarvis"}}}""");
        stub.Responses["/backend-api/gizmos/g-p-second"] = new ChatGptResponse(200,
            """{"gizmo":{"id":"g-p-second","display":{"name":"Jarvis"}}}""");
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("Jarvis", "g-p-first", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);

        await RunAsync(provider, Ask("hello"));
        options.SaveChatGptProjectId("g-p-second");
        await RunAsync(provider, Ask("hello", "an answer", "again"));

        Assert.Equal("g-p-second", stub.AskedGizmo);
        Assert.Equal(1, stub.Calls.Count(c => c == "GET /backend-api/gizmos/g-p-first"));
        Assert.Equal(1, stub.Calls.Count(c => c == "GET /backend-api/gizmos/g-p-second"));
        Assert.DoesNotContain(stub.Calls, c => c.Contains("sidebar"));
    }

    [Fact]
    public async Task TheMemoryFastPathObservesAnotherProcessTombstoningTheDurablePin()
    {
        var stub = new StubTransport();
        stub.Responses["/backend-api/gizmos/g-p-first"] = new ChatGptResponse(200,
            """{"gizmo":{"id":"g-p-first","display":{"name":"Jarvis"}}}""");
        stub.Responses[ProjectSidebarPath] = new ChatGptResponse(200,
            """{"items":[{"gizmo":{"id":"g-p-replacement","display":{"name":"Jarvis"}}}],"cursor":null}""");
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("Jarvis", "g-p-first", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);

        await RunAsync(provider, Ask("hello"));
        options.SaveChatGptProjectId("");
        await RunAsync(provider, Ask("hello", "an answer", "again"));

        Assert.Equal("g-p-replacement", stub.AskedGizmo);
        Assert.Contains($"GET {ProjectSidebarPath}", stub.Calls);
        Assert.DoesNotContain(stub.Calls, c => c == "POST /backend-api/projects");
    }

    [Fact]
    public async Task AProjectRouteLostBeforeSendEvictsTheVerifiedMemoryEntry()
    {
        var asks = 0;
        var stub = new StubTransport
        {
            AskHandler = (_, _) => Task.FromResult(Interlocked.Increment(ref asks) == 1
                ? ChatGptUiReply.Failed("project route lost") with { ProjectUnavailable = true }
                : new ChatGptUiReply("an answer", "conv-1", null)),
        };
        stub.Responses["/backend-api/gizmos/g-p-remembered"] = new ChatGptResponse(200,
            """{"gizmo":{"id":"g-p-remembered","display":{"name":"Jarvis"}}}""");
        ChatGptTransportRegistry.Register((_, _) => stub);
        var provider = new ChatGptWebProvider(new Cookies(), new Options("Jarvis", "g-p-remembered", 100));

        await Assert.ThrowsAsync<ProviderException>(() => RunAsync(provider, Ask("hello")));
        await RunAsync(provider, Ask("hello"));

        Assert.Equal(2, stub.Calls.Count(c => c == "GET /backend-api/gizmos/g-p-remembered"));
        Assert.Equal(2, asks);
    }

    [Fact]
    public async Task ARememberedProjectWithNoReadableNameFailsClosed()
    {
        var stub = new StubTransport();
        stub.Responses["/backend-api/gizmos/g-p-remembered"] = new ChatGptResponse(200,
            """{"gizmo":{"id":"g-p-remembered"}}""");
        ChatGptTransportRegistry.Register((_, _) => stub);

        var failure = await Assert.ThrowsAsync<ProviderException>(() => RunAsync(
            new ChatGptWebProvider(new Cookies(), new Options("Jarvis", "g-p-remembered", 100)), Ask("hello")));

        Assert.Contains("readable name", failure.Message);
        Assert.DoesNotContain(stub.Calls, c => c.Contains("sidebar") || c == "POST /backend-api/projects");
        Assert.Empty(stub.Asks);
    }

    [Fact]
    public async Task AReadableDuplicateAfterANamelessIdStillValidatesTheRememberedProject()
    {
        var stub = new StubTransport();
        stub.Responses["/backend-api/gizmos/g-p-remembered"] = new ChatGptResponse(200,
            """{"shadow":{"id":"g-p-remembered"},"gizmo":{"id":"g-p-remembered","display":{"name":"Jarvis"}}}""");
        ChatGptTransportRegistry.Register((_, _) => stub);

        await RunAsync(new ChatGptWebProvider(
            new Cookies(), new Options("Jarvis", "g-p-remembered", 100)), Ask("hello"));

        Assert.Equal("g-p-remembered", stub.AskedGizmo);
        Assert.DoesNotContain(stub.Calls, c => c.Contains("sidebar") || c == "POST /backend-api/projects");
    }

    [Fact]
    public async Task CancellationReturnedAsAProjectFailureStillCancelsTheTurn()
    {
        using var cancellation = new CancellationTokenSource();
        var stub = new StubTransport
        {
            ResponseHandler = (method, path, _) =>
            {
                if (method == HttpMethod.Get && path == "/backend-api/gizmos/g-p-remembered")
                {
                    cancellation.Cancel();
                    return new ChatGptResponse(ChatGptResponse.TransportFailure, "cancelled");
                }

                return null;
            },
        };
        ChatGptTransportRegistry.Register((_, _) => stub);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(
            new ChatGptWebProvider(new Cookies(), new Options("Jarvis", "g-p-remembered", 100)),
            Ask("hello"), cancellation.Token));

        Assert.Empty(stub.Asks);
    }

    [Fact]
    public async Task CancellationReturnedAsAnAuthFailureStillCancelsConfiguredProjectSetup()
    {
        using var cancellation = new CancellationTokenSource();
        var stub = new StubTransport
        {
            ResponseHandler = (method, path, _) =>
            {
                if (method == HttpMethod.Get && path == "/api/auth/session")
                {
                    cancellation.Cancel();
                    return new ChatGptResponse(ChatGptResponse.TransportFailure, "cancelled");
                }

                return null;
            },
        };
        ChatGptTransportRegistry.Register((_, _) => stub);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(
            new ChatGptWebProvider(new Cookies(), new Options("Jarvis", "", 100)),
            Ask("hello"), cancellation.Token));

        Assert.DoesNotContain(stub.Calls, c => c.Contains("sidebar") || c == "POST /backend-api/projects");
        Assert.Empty(stub.Asks);
    }

    [Fact]
    public async Task ARecentUnknownCreateIsLookupOnlyUntilItCanBeReconciled()
    {
        var stub = new StubTransport();
        stub.Responses[ProjectSidebarPath] = new ChatGptResponse(200, """{"items":[],"cursor":null}""");
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("Jarvis", "pending:" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 100);

        var failure = await Assert.ThrowsAsync<ProviderException>(() => RunAsync(
            new ChatGptWebProvider(new Cookies(), options), Ask("hello")));

        Assert.Contains("may still be completing", failure.Message);
        Assert.DoesNotContain(stub.Calls, c => c == "POST /backend-api/projects");
        Assert.Empty(stub.Asks);
    }

    [Fact]
    public async Task AFuturePendingTimestampIsRepairedInsteadOfBlockingForever()
    {
        var stub = new StubTransport();
        stub.Responses[ProjectSidebarPath] = new ChatGptResponse(200, """{"items":[],"cursor":null}""");
        ChatGptTransportRegistry.Register((_, _) => stub);
        var future = DateTimeOffset.UtcNow.AddYears(10).ToUnixTimeSeconds();
        var options = new Options("Jarvis", "pending:" + future, 100);

        var failure = await Assert.ThrowsAsync<ProviderException>(() => RunAsync(
            new ChatGptWebProvider(new Cookies(), options), Ask("hello")));

        Assert.Contains("repaired", failure.Message);
        Assert.StartsWith("pending:", options.ChatGptProjectId, StringComparison.Ordinal);
        Assert.NotEqual("pending:" + future, options.ChatGptProjectId);
        Assert.DoesNotContain(stub.Calls, c => c == "POST /backend-api/projects");
        Assert.Empty(stub.Asks);
    }

    [Fact]
    public async Task AnExpiredUnknownCreateCanPostAfterAnExhaustiveLookupStillFindsNothing()
    {
        var stub = new StubTransport();
        stub.Responses[ProjectSidebarPath] = new ChatGptResponse(200, """{"items":[],"cursor":null}""");
        stub.Responses["/backend-api/projects"] = new ChatGptResponse(200,
            """{"gizmo":{"id":"g-p-created","display":{"name":"Jarvis"}}}""");
        ChatGptTransportRegistry.Register((_, _) => stub);
        var old = DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeSeconds();
        var options = new Options("Jarvis", "pending:" + old, 100);

        await RunAsync(new ChatGptWebProvider(new Cookies(), options), Ask("hello"));

        Assert.Equal(1, stub.Calls.Count(c => c == "POST /backend-api/projects"));
        Assert.Equal("g-p-created", stub.AskedGizmo);
        Assert.Equal("g-p-created", options.ChatGptProjectId);
    }

    [Fact]
    public async Task AnUnknownCancelledCreateIsReconciledWithoutASecondPost()
    {
        using var cancellation = new CancellationTokenSource();
        var created = false;
        var posts = 0;
        var stub = new StubTransport
        {
            ResponseHandler = (method, path, _) =>
            {
                if (method == HttpMethod.Get && path == ProjectSidebarPath)
                {
                    return created
                        ? new ChatGptResponse(200,
                            """{"items":[{"gizmo":{"id":"g-p-created","display":{"name":"Jarvis"}}}],"cursor":null}""")
                        : new ChatGptResponse(200, """{"items":[],"cursor":null}""");
                }

                if (method == HttpMethod.Post && path == "/backend-api/projects")
                {
                    posts++;
                    created = true;
                    cancellation.Cancel();
                    return new ChatGptResponse(ChatGptResponse.TransportFailure, "cancelled after commit");
                }

                return null;
            },
        };
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("Jarvis", "", 100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(
            new ChatGptWebProvider(new Cookies(), options), Ask("hello"), cancellation.Token));
        Assert.StartsWith("pending:", options.ChatGptProjectId, StringComparison.Ordinal);

        await RunAsync(new ChatGptWebProvider(new Cookies(), options), Ask("hello"));

        Assert.Equal(1, posts);
        Assert.Equal("g-p-created", stub.AskedGizmo);
        Assert.Equal("g-p-created", options.ChatGptProjectId);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task AStaleRememberedProjectIsClearedAndTheLastSidebarPageRepinsIt(int staleStatus)
    {
        const string cursor = "next/page+token=";
        var continuation = ProjectSidebarPath + "&cursor=" + Uri.EscapeDataString(cursor);
        var stub = new StubTransport
        {
            ResponseHandler = (method, path, _) => (method.Method, path) switch
            {
                ("GET", "/backend-api/gizmos/g-p-stale") => new ChatGptResponse(staleStatus, "stale"),
                ("GET", ProjectSidebarPath) => new ChatGptResponse(200,
                    """{"items":[{"gizmo":{"id":"g-p-other","display":{"name":"Other"}}}],"cursor":"""
                    + JsonValue.Create(cursor)!.ToJsonString() + "}"),
                ("GET", var page) when page == continuation => new ChatGptResponse(200,
                    """{"items":[{"gizmo":{"id":"g-p-late","display":{"name":"Jarvis"}}}],"cursor":null}"""),
                _ => null,
            },
        };
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("Jarvis", "g-p-stale", 100);

        await RunAsync(new ChatGptWebProvider(new Cookies(), options), Ask("hello"));

        Assert.Equal("g-p-late", stub.AskedGizmo);
        Assert.Equal(new[] { "", "g-p-late" }, options.SavedProjectIds);
        Assert.Contains($"GET {ProjectSidebarPath}", stub.Calls);
        Assert.Contains($"GET {continuation}", stub.Calls);
        Assert.DoesNotContain(stub.Calls, c => c == "POST /backend-api/projects");
    }

    [Fact]
    public async Task WithNothingRememberedTheNameIsMatchedAndThenPinned()
    {
        var stub = new StubTransport();
        stub.Responses["/backend-api/gizmos/snorlax/sidebar"] = new ChatGptResponse(200, Sidebar);
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("Jarvis", "", 100);

        await RunAsync(new ChatGptWebProvider(new Cookies(), options), Ask("hello"));

        Assert.Equal("g-p-existing", stub.AskedGizmo);
        // Pinned, so the next run does not depend on the lookup working again.
        Assert.Equal("g-p-existing", options.Saved);
        Assert.DoesNotContain(stub.Calls, c => c.Contains("POST /backend-api/projects"));
    }

    [Fact]
    public async Task AFailedLookupRefusesRatherThanCreatingATwin()
    {
        var stub = new StubTransport();
        stub.Responses["/backend-api/gizmos/snorlax/sidebar"] = new ChatGptResponse(500, "server error");
        ChatGptTransportRegistry.Register((_, _) => stub);

        var provider = new ChatGptWebProvider(new Cookies(), new Options("Jarvis", "", 100));
        var failure = await Assert.ThrowsAsync<ProviderException>(() => RunAsync(provider, Ask("hello")));

        Assert.Contains("Nothing was created", failure.Message);
        Assert.DoesNotContain(stub.Calls, c => c.Contains("POST /backend-api/projects"));
    }

    /// <summary>
    /// A body that opens like JSON but stops half way — a truncated response, a proxy cutting in —
    /// gets past the cheap shape check. Each of these reads has an answer for "unreadable"; none of
    /// them is served by an exception thrown out of the middle of a turn.
    /// </summary>
    private const string Truncated = """{"items":[{"gizmo":""";

    [Fact]
    public async Task AnUnreadableListingIsAFailedLookupRatherThanACrash()
    {
        var stub = new StubTransport();
        stub.Responses["/backend-api/gizmos/snorlax/sidebar"] = new ChatGptResponse(200, Truncated);
        ChatGptTransportRegistry.Register((_, _) => stub);

        var provider = new ChatGptWebProvider(new Cookies(), new Options("Jarvis", "", 100));
        var failure = await Assert.ThrowsAsync<ProviderException>(() => RunAsync(provider, Ask("hello")));

        Assert.Contains("could not read", failure.Message);
        Assert.Contains("Nothing was created", failure.Message);
        Assert.DoesNotContain(stub.Calls, c => c.Contains("POST /backend-api/projects"));
    }

    [Fact]
    public async Task ANamelessProjectInACompleteListingCannotProveTheConfiguredNameIsAbsent()
    {
        var stub = new StubTransport();
        stub.Responses[ProjectSidebarPath] = new ChatGptResponse(200,
            """{"items":[{"gizmo":{"id":"g-p-unreadable"}}],"cursor":null}""");
        ChatGptTransportRegistry.Register((_, _) => stub);

        var failure = await Assert.ThrowsAsync<ProviderException>(() => RunAsync(
            new ChatGptWebProvider(new Cookies(), new Options("Jarvis", "", 100)), Ask("hello")));

        Assert.Contains("without a readable name", failure.Message);
        Assert.DoesNotContain(stub.Calls, c => c == "POST /backend-api/projects");
        Assert.Empty(stub.Asks);
    }

    [Fact]
    public async Task AnUnreadableCreationIsRecoveredByOneImmediateNameLookup()
    {
        var listingReads = 0;
        var stub = new StubTransport();
        stub.ResponseHandler = (method, path, _) =>
        {
            if (method == HttpMethod.Get
                && path.StartsWith("/backend-api/gizmos/snorlax/sidebar?", StringComparison.Ordinal))
            {
                return Interlocked.Increment(ref listingReads) == 1
                    ? new ChatGptResponse(200, """{"items":[],"cursor":null}""")
                    : new ChatGptResponse(200,
                        """{"items":[{"gizmo":{"id":"g-p-recovered","display":{"name":"Jarvis"}}}],"cursor":null}""");
            }

            return null;
        };
        stub.Responses["/backend-api/projects"] = new ChatGptResponse(200, Truncated);
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("Jarvis", "", 100);

        await RunAsync(new ChatGptWebProvider(new Cookies(), options), Ask("hello"));

        Assert.Equal(2, listingReads);
        Assert.Equal("g-p-recovered", stub.AskedGizmo);
        Assert.Equal("g-p-recovered", options.Saved);
    }

    [Fact]
    public async Task AnUnreadableCreationCanNeverSendAChatOutsideTheConfiguredProject()
    {
        var listingReads = 0;
        var stub = new StubTransport
        {
            ResponseHandler = (method, path, _) =>
            {
                if (method == HttpMethod.Get
                    && path.StartsWith("/backend-api/gizmos/snorlax/sidebar?", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref listingReads);
                    return new ChatGptResponse(200, """{"items":[],"cursor":null}""");
                }

                return null;
            },
        };
        stub.Responses["/backend-api/projects"] = new ChatGptResponse(200, Truncated);
        ChatGptTransportRegistry.Register((_, _) => stub);

        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            RunAsync(new ChatGptWebProvider(new Cookies(), new Options("Jarvis", "", 100)), Ask("hello")));

        Assert.Equal(2, listingReads);
        Assert.Contains("project", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stub.Asks);
    }

    [Fact]
    public async Task AnUnreadableSessionPointsAtTheCookies()
    {
        var stub = new StubTransport();
        stub.Responses["/api/auth/session"] = new ChatGptResponse(200, Truncated);
        ChatGptTransportRegistry.Register((_, _) => stub);

        var provider = new ChatGptWebProvider(new Cookies(), new Options("", "", 100));
        var failure = await Assert.ThrowsAsync<ProviderException>(() => RunAsync(provider, Ask("hello")));

        Assert.Contains("no access token", failure.Message);
    }

    [Fact]
    public async Task AStalePinCreatesTheProjectOnlyAfterEverySidebarPageProvesItAbsent()
    {
        const string cursor = "final page/+=";
        var continuation = ProjectSidebarPath + "&cursor=" + Uri.EscapeDataString(cursor);
        var stub = new StubTransport
        {
            ResponseHandler = (method, path, _) => (method.Method, path) switch
            {
                ("GET", "/backend-api/gizmos/g-p-stale") => new ChatGptResponse(404, "missing"),
                ("GET", ProjectSidebarPath) => new ChatGptResponse(200,
                    """{"items":[{"gizmo":{"id":"g-p-other","display":{"name":"Other"}}}],"cursor":"""
                    + JsonValue.Create(cursor)!.ToJsonString() + "}"),
                ("GET", var page) when page == continuation =>
                    new ChatGptResponse(200, """{"items":[],"cursor":null}"""),
                _ => null,
            },
        };
        stub.Responses["/backend-api/projects"] = new ChatGptResponse(200, """{"gizmo":{"id":"g-p-made","name":"Jarvis"}}""");
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("Jarvis", "g-p-stale", 100);

        await RunAsync(new ChatGptWebProvider(new Cookies(), options), Ask("hello"));

        var projectCalls = stub.Calls.Where(c =>
            c is "GET /backend-api/gizmos/g-p-stale" or "POST /backend-api/projects"
            || c.StartsWith("GET /backend-api/gizmos/snorlax/sidebar?", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[]
        {
            "GET /backend-api/gizmos/g-p-stale",
            $"GET {ProjectSidebarPath}",
            $"GET {continuation}",
            "POST /backend-api/projects",
        }, projectCalls);
        Assert.Equal("g-p-made", stub.AskedGizmo);
        Assert.Equal("g-p-made", options.Saved);
        Assert.Collection(options.SavedProjectIds,
            id => Assert.Equal("", id),
            id => Assert.StartsWith("pending:", id, StringComparison.Ordinal),
            id => Assert.Equal("g-p-made", id));
    }

    [Fact]
    public async Task TheNewChatIsGivenAName()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);

        await RunAsync(new ChatGptWebProvider(new Cookies(), new Options("", "", 100)), Ask("hello"));

        Assert.Contains(stub.Calls, c => c == "POST /backend-api/conversation/id/conv-1/rename");
    }

    [Fact]
    public async Task TheChatIsDeletedOnceItHasTakenItsShareOfMessages()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var provider = new ChatGptWebProvider(new Cookies(), new Options("", "", 20));

        // Each turn carries the whole conversation, which is how the provider recognises the chat
        // it is continuing; the twenty-first send is the one that has to rotate.
        var turns = new List<string>();
        for (var i = 0; i < 21; i++)
        {
            turns.Add($"question {i}");
            await RunAsync(provider, Ask([.. turns]));
            turns.Add("an answer");
        }

        Assert.Contains(stub.Calls, c => c == "DELETE /backend-api/conversation/id/conv-1");
        Assert.Equal(1, stub.Calls.Count(c => c.StartsWith("DELETE ")));
    }

    /// <summary>
    /// The page's picker has no slug to click, only the label ChatGPT prints, so what the provider
    /// hands the transport is that label — and "auto" must hand it nothing at all, or every turn
    /// would drag the account off whatever model it had chosen for itself.
    /// </summary>
    [Theory]
    [InlineData("auto", null)]
    [InlineData("AUTO", null)]
    [InlineData(null, null)]
    [InlineData("gpt-6-astra", "Latest")]
    [InlineData("gpt-5-6-sol", "GPT-5.6 Sol")]
    [InlineData("gpt-5-5", "GPT-5.5")]
    [InlineData("chatgpt-web:GPT-9 Quasar", "GPT-9 Quasar")]
    [InlineData("GPT-5.6 Pro", "GPT-5.6 Pro")]
    public void ThePickerLabelIsWhatTheModelIdMeansOnThePage(string? modelId, string? expected)
        => Assert.Equal(expected, ChatGptWebProvider.PickerLabel(modelId));

    [Fact]
    public async Task TheChosenModelReachesTheTransportAsItsPickerLabel()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var request = Ask("hello") with { ModelId = "gpt-5-6-sol" };

        await RunAsync(new ChatGptWebProvider(new Cookies(), new Options("", "", 100)), request);

        Assert.Equal("GPT-5.6 Sol", stub.AskedModel);
    }

    [Fact]
    public async Task WebNativeComposerOptionsReachTheTransportUnchanged()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var request = Ask("hello") with
        {
            ModelId = "gpt-6-astra",
            ProviderOptions = new Dictionary<string, string>(StringComparer.Ordinal) { ["power"] = "4" },
        };

        await RunAsync(new ChatGptWebProvider(new Cookies(), new Options("", "", 100)), request);

        Assert.Equal("4", Assert.Single(stub.Asks).ComposerOptions["power"]);
    }

    [Fact]
    public async Task AccountAutoNeverAppliesAModelSpecificPowerKey()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var request = Ask("hello") with
        {
            ProviderOptions = new Dictionary<string, string>(StringComparer.Ordinal) { ["power"] = "4" },
        };

        await RunAsync(new ChatGptWebProvider(new Cookies(), new Options("", "", 100)), request);

        Assert.Empty(Assert.Single(stub.Asks).ComposerOptions);
    }

    [Fact]
    public async Task TheAccountDefaultLeavesThePickerAlone()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);

        await RunAsync(new ChatGptWebProvider(new Cookies(), new Options("", "", 100)), Ask("hello"));

        Assert.Null(stub.AskedModel);
    }

    /// <summary>
    /// The whole point of the text protocol: a marked line in the reply has to come back out as a
    /// real tool call, or the agent loop never runs anything on the user's machine.
    /// </summary>
    [Fact]
    public async Task AnActionLineBecomesAToolCallTheLoopCanRun()
    {
        const string action = """JARVIS_ACT {"name":"Read","arguments":{"path":"a.txt"}}""";
        var request = Ask("what is in a.txt?") with
        {
            Tools = [new ToolDefinition("Read", "Read a local file.", new JsonObject())],
        };
        var stub = new StubTransport();
        stub.Replies.Enqueue(action);
        stub.Responses["/backend-api/conversation/conv-1"] =
            new ChatGptResponse(200, StoredTurn(ChatGptWebProvider.FlattenHistory(request), action));
        ChatGptTransportRegistry.Register((_, _) => stub);

        var events = new List<ProviderEvent>();
        await foreach (var e in new ChatGptWebProvider(new Cookies(), new Options("", "", 100))
            .StreamChatAsync(request, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Empty(events.OfType<TextDeltaEvent>());
        var started = Assert.Single(events.OfType<ToolCallStartedEvent>());
        Assert.Equal("Read", started.Name);
        Assert.NotEmpty(started.Id);
        Assert.Equal("""{"path":"a.txt"}""", Assert.Single(events.OfType<ToolCallArgumentsDeltaEvent>()).Delta);
        Assert.True(Assert.Single(events.OfType<ResponseCompletedEvent>()).WantsToolUse);
    }

    /// <summary>
    /// The page hands back rendered markdown: the escapes inside a file's contents are gone and
    /// the JSON around them no longer parses. The turn has to be read from the conversation, or
    /// every Write the model asks for dies as unparseable text.
    /// </summary>
    [Fact]
    public async Task TheStoredTurnIsUsedRatherThanTheMangledPage()
    {
        var request = Ask("write package.json") with
        {
            Tools = [new ToolDefinition("Write", "Write a local file.", new JsonObject())],
        };
        var prompt = ChatGptWebProvider.FlattenHistory(request);
        var written = "{\n  \"name\": \"app\"\n}";
        var action = new JsonObject
        {
            ["name"] = "Write",
            ["arguments"] = new JsonObject { ["path"] = "package.json", ["content"] = written },
        };
        var stub = new StubTransport();
        // What the page shows once markdown has eaten the backslashes: no longer valid JSON.
        stub.Replies.Enqueue("""JARVIS_ACT {"name":"Write","arguments":{"path":"package.json","content":"{ "name": "app" }"}}""");
        stub.Responses["/backend-api/conversation/conv-1"] = new ChatGptResponse(200, new JsonObject
        {
            ["current_node"] = "n2",
            ["mapping"] = new JsonObject
            {
                ["n1"] = new JsonObject
                {
                    ["parent"] = null,
                    ["message"] = new JsonObject
                    {
                        ["author"] = new JsonObject { ["role"] = "user" },
                        ["content"] = new JsonObject
                        {
                            ["content_type"] = "text",
                            ["parts"] = new JsonArray(prompt),
                        },
                    },
                },
                ["n2"] = new JsonObject
                {
                    ["parent"] = "n1",
                    ["message"] = new JsonObject
                    {
                        ["author"] = new JsonObject { ["role"] = "assistant" },
                        ["content"] = new JsonObject
                        {
                            ["content_type"] = "text",
                            ["parts"] = new JsonArray($"JARVIS_ACT {action.ToJsonString()}"),
                        },
                    },
                },
            },
        }.ToJsonString());
        ChatGptTransportRegistry.Register((_, _) => stub);

        var events = new List<ProviderEvent>();
        await foreach (var e in new ChatGptWebProvider(new Cookies(), new Options("", "", 100))
            .StreamChatAsync(request, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Equal("Write", Assert.Single(events.OfType<ToolCallStartedEvent>()).Name);
        var arguments = JsonNode.Parse(Assert.Single(events.OfType<ToolCallArgumentsDeltaEvent>()).Delta)!;
        Assert.Equal(written, arguments["content"]!.GetValue<string>());
    }

    /// <summary>
    /// A turn can end on a card only a person can answer — a connector asking to be let in — and
    /// the page then holds no message of its own. The conversation still has what was written, so
    /// the turn is an answer, not a failure.
    /// </summary>
    [Fact]
    public async Task AnAnswerLeftOnlyInTheConversationIsStillAnAnswer()
    {
        const string written = "To delete it I need Vercel connected to this chat.";
        var request = Ask("delete the vercel project");
        var stub = new StubTransport();
        stub.Replies.Enqueue("");
        stub.Responses["/backend-api/conversation/conv-1"] =
            new ChatGptResponse(200, StoredTurn(ChatGptWebProvider.FlattenHistory(request), written));
        ChatGptTransportRegistry.Register((_, _) => stub);

        var answer = await RunAsync(new ChatGptWebProvider(new Cookies(), new Options("", "", 100)), request);

        Assert.Equal(written, answer);
    }

    /// <summary>
    /// The page finishes before the store does. Caught in the wild: the conversation held the
    /// message that prompted the turn — so the count of them was right — while the answer to it was
    /// still an empty shell. Reading that once and giving up handed back the page's copy, where
    /// markdown had already eaten the escapes out of a PowerShell command.
    /// </summary>
    [Fact]
    public async Task AStoreThatLagsThePageIsAskedAgain()
    {
        const string written = """JARVIS_ACT {"name":"PowerShell","arguments":{"command":"echo \"hi\""}}""";
        var request = Ask("check the browsers") with
        {
            Tools = [new ToolDefinition("PowerShell", "Run a command on the user's machine.", new JsonObject())],
        };
        var prompt = ChatGptWebProvider.FlattenHistory(request);
        var stub = new StubTransport();
        stub.Replies.Enqueue("""JARVIS_ACT {"name":"PowerShell","arguments":{"command":"echo "hi""}}""");
        stub.ConversationReads.Enqueue(new ChatGptResponse(200, StoredTurn(prompt, "")));
        stub.ConversationReads.Enqueue(new ChatGptResponse(200, StoredTurn(prompt, written)));
        ChatGptTransportRegistry.Register((_, _) => stub);

        var events = new List<ProviderEvent>();
        await foreach (var e in new ChatGptWebProvider(new Cookies(), new Options("", "", 100))
            .StreamChatAsync(request, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Equal(2, stub.Calls.Count(c => c == "GET /backend-api/conversation/conv-1"));
        // The page's copy would have lost the escapes and never parsed; the stored one keeps them.
        var arguments = JsonNode.Parse(Assert.Single(events.OfType<ToolCallArgumentsDeltaEvent>()).Delta)!;
        Assert.Equal("echo \"hi\"", arguments["command"]!.GetValue<string>());
    }

    /// <summary>
    /// Reading the page instead of the store is quiet and lossy, and three times running it took
    /// digging through a session file and the account's own conversation to find out which of the
    /// several ways it can happen had happened. The reason belongs in the log — as metadata, which
    /// is all this log ever carries.
    ///
    /// Both live here rather than beside the walk they test: the sink is process-wide, and xUnit
    /// runs classes in parallel, so two of them attaching at once would take each other's lines.
    /// </summary>
    [Fact]
    public async Task AnUnverifiedStoredAnswerReportsWhyItWasRejected()
    {
        var request = Ask("say hello");
        var prompt = ChatGptWebProvider.FlattenHistory(request);
        var stub = new StubTransport();
        stub.Replies.Enqueue("what the page showed");
        for (var i = 0; i < 6; i++)
        {
            stub.ConversationReads.Enqueue(new ChatGptResponse(200, StoredTurn(prompt, "")));
        }

        ChatGptTransportRegistry.Register((_, _) => stub);

        var lines = new List<string>();
        DiagnosticLog.Attach(lines.Add);
        try
        {
            var failure = await Assert.ThrowsAsync<ProviderException>(() =>
                RunAsync(new ChatGptWebProvider(new Cookies(), new Options("", "", 100)), request));
            Assert.Contains("stored response could not be verified", failure.Message);
        }
        finally
        {
            DiagnosticLog.Attach(null);
        }

        Assert.Contains(lines, l => l.StartsWith("chatgpt: stored response unavailable —", StringComparison.Ordinal)
            && l.Contains("no answer after 4 tries", StringComparison.Ordinal));
    }

    [Fact]
    public void AConversationShortOfMessagesSaysBothNumbers()
    {
        var json = StoredTurn("the prompt", "an answer");

        var lines = new List<string>();
        DiagnosticLog.Attach(lines.Add);
        try
        {
            // Three turns have gone into the chat; the conversation holds one of them.
            Assert.Null(ChatGptWebProvider.ExactAnswer(json, 3));
        }
        finally
        {
            DiagnosticLog.Attach(null);
        }

        Assert.Contains(lines, l => l.Contains("holds 1 of the 3 messages sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStoreThatNeverCatchesUpNeverFallsBackToThePage()
    {
        var request = Ask("say hello");
        var prompt = ChatGptWebProvider.FlattenHistory(request);
        var stub = new StubTransport();
        stub.Replies.Enqueue("what the page showed");
        for (var i = 0; i < 6; i++)
        {
            stub.ConversationReads.Enqueue(new ChatGptResponse(200, StoredTurn(prompt, "")));
        }

        ChatGptTransportRegistry.Register((_, _) => stub);

        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            RunAsync(new ChatGptWebProvider(new Cookies(), new Options("", "", 100)), request));
        Assert.Contains("stored response could not be verified", failure.Message);
    }

    /// <summary>
    /// A turn that drew a picture writes no words: the answer is the file. It comes down to disk
    /// and the path goes into the answer, which is the only shape this channel can carry — and is
    /// enough for the shell to move it wherever the user wants it.
    /// </summary>
    [Fact]
    public async Task APictureComesBackAsAFileOnDisk()
    {
        var request = Ask("draw me a wallpaper");
        var stub = new StubTransport { AssetPath = @"C:\JarvisCode\downloads\chatgpt-20260829.png" };
        stub.Replies.Enqueue("");
        stub.Responses["/backend-api/conversation/conv-1"] = new ChatGptResponse(
            200, DrawnTurn(ChatGptWebProvider.FlattenHistory(request), "file_abc123"));
        ChatGptTransportRegistry.Register((_, _) => stub);

        var answer = await RunAsync(new ChatGptWebProvider(new Cookies(), new Options("", "", 100)), request);

        Assert.Equal("file_abc123", Assert.Single(stub.SavedAssets));
        Assert.Equal(@"[file saved to C:\JarvisCode\downloads\chatgpt-20260829.png]", answer);
    }

    [Fact]
    public async Task APictureThatWillNotComeDownIsSaidSoRatherThanLost()
    {
        var request = Ask("draw me a wallpaper");
        var stub = new StubTransport { AssetPath = null };
        stub.Replies.Enqueue("");
        stub.Responses["/backend-api/conversation/conv-1"] = new ChatGptResponse(
            200, DrawnTurn(ChatGptWebProvider.FlattenHistory(request), "file_abc123"));
        ChatGptTransportRegistry.Register((_, _) => stub);

        var answer = await RunAsync(new ChatGptWebProvider(new Cookies(), new Options("", "", 100)), request);

        Assert.Contains("could not download", answer);
    }

    /// <summary>The shape image_gen leaves: the picture on a tool message, no words of its own.</summary>
    private static string DrawnTurn(string prompt, string assetId) => new JsonObject
    {
        ["current_node"] = "n3",
        ["mapping"] = new JsonObject
        {
            ["n1"] = Node(null, "user", prompt),
            ["n2"] = Drawn("n1", assetId),
            ["n3"] = Node("n2", "assistant", ""),
        },
    }.ToJsonString();

    private static JsonObject Drawn(string parent, string assetId) => new()
    {
        ["parent"] = parent,
        ["message"] = new JsonObject
        {
            ["author"] = new JsonObject { ["role"] = "tool", ["name"] = "image_gen" },
            ["content"] = new JsonObject
            {
                ["content_type"] = "multimodal_text",
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["content_type"] = "image_asset_pointer",
                    ["asset_pointer"] = "sediment://" + assetId,
                }),
            },
        },
    };

    [Fact]
    public async Task NothingOnThePageAndNothingStoredIsReportedAsNothing()
    {
        var stub = new StubTransport();
        stub.Replies.Enqueue("");
        ChatGptTransportRegistry.Register((_, _) => stub);

        var provider = new ChatGptWebProvider(new Cookies(), new Options("", "", 100));
        var failure = await Assert.ThrowsAsync<ProviderException>(() => RunAsync(provider, Ask("hello")));

        Assert.Contains("stored response could not be verified", failure.Message);
    }

    private static string StoredTurn(string prompt, string answer) => new JsonObject
    {
        ["current_node"] = "n2",
        ["mapping"] = new JsonObject
        {
            ["n1"] = Node(null, "user", prompt),
            ["n2"] = Node("n1", "assistant", answer),
        },
    }.ToJsonString();

    private static string StoredConversation(params (string Prompt, string Answer)[] turns)
    {
        var mapping = new JsonObject();
        string? parent = null;
        var index = 0;
        foreach (var (prompt, answer) in turns)
        {
            var user = $"n{++index}";
            mapping[user] = Node(parent, "user", prompt);
            var assistant = $"n{++index}";
            mapping[assistant] = Node(user, "assistant", answer);
            parent = assistant;
        }
        return new JsonObject { ["current_node"] = parent, ["mapping"] = mapping }.ToJsonString();
    }

    private static JsonObject Node(string? parent, string role, string text) => new()
    {
        ["parent"] = parent,
        ["message"] = new JsonObject
        {
            ["author"] = new JsonObject { ["role"] = role },
            ["content"] = new JsonObject
            {
                ["content_type"] = "text",
                ["parts"] = new JsonArray(text),
            },
        },
    };

    [Fact]
    public async Task APlainAnswerAsksForNoTool()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);

        var events = new List<ProviderEvent>();
        await foreach (var e in new ChatGptWebProvider(new Cookies(), new Options("", "", 100))
            .StreamChatAsync(Ask("hello"), CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Empty(events.OfType<ToolCallStartedEvent>());
        Assert.False(Assert.Single(events.OfType<ResponseCompletedEvent>()).WantsToolUse);
    }

    /// <summary>
    /// A tool round trip must land in the chat that is already open. Starting a new one would
    /// re-send the whole conversation for every tool the model asks for.
    /// </summary>
    [Fact]
    public async Task TheToolResultContinuesTheSameChat()
    {
        const string action = """JARVIS_ACT {"name":"Read","arguments":{"path":"a.txt"}}""";
        var stub = new StubTransport();
        stub.Replies.Enqueue(action);
        stub.Replies.Enqueue("It says hello world.");
        ChatGptTransportRegistry.Register((_, _) => stub);
        var provider = new ChatGptWebProvider(new Cookies(), new Options("", "", 100));

        var first = Ask("what is in a.txt?") with
        {
            Tools = [new ToolDefinition("Read", "Read a local file.", new JsonObject())],
        };
        var firstPrompt = ChatGptWebProvider.FlattenHistory(first);
        stub.ConversationReads.Enqueue(new ChatGptResponse(200, StoredTurn(firstPrompt, action)));
        await RunAsync(provider, first);

        var withResult = first with
        {
            Messages =
            [
                .. first.Messages,
                new ChatMessage(Role.Assistant, [new ToolCallBlock("c1", "Read", """{"path":"a.txt"}""")]),
                new ChatMessage(Role.User, [new ToolResultBlock("c1", "Read", "hello world", false)]),
            ],
        };
        var continuedPrompt = "RESULT Read: hello world\n\n" + ChatGptToolProtocol.Closing();
        stub.ConversationReads.Enqueue(new ChatGptResponse(200, StoredConversation(
            (firstPrompt, action), (continuedPrompt, "It says hello world."))));
        var answer = await RunAsync(provider, withResult);

        Assert.Equal("conv-1", stub.AskedConversation);
        Assert.Equal(continuedPrompt, stub.AskedPrompt);
        Assert.Equal("It says hello world.", answer);
    }

    [Fact]
    public async Task AFailedLocalReadCanContinueWithAVerifiedLocalWrite()
    {
        const string readAction = """JARVIS_ACT {"name":"Read","arguments":{"path":"index.html"}}""";
        const string writeAction = """JARVIS_ACT {"name":"Write","arguments":{"path":"index.html","content":"<!doctype html><title>Local page</title>"}}""";
        var request = Ask("Create index.html in this workspace if it does not exist.") with
        {
            Tools =
            [
                new ToolDefinition("Read", "Read a local file.", new JsonObject()),
                new ToolDefinition("Write", "Write a local file.", new JsonObject()),
            ],
        };
        var firstPrompt = ChatGptWebProvider.FlattenHistory(request);
        var stub = new StubTransport();
        stub.Replies.Enqueue(readAction);
        stub.Replies.Enqueue(writeAction);
        stub.ConversationReads.Enqueue(new ChatGptResponse(200, StoredTurn(firstPrompt, readAction)));
        ChatGptTransportRegistry.Register((_, _) => stub);
        var provider = new ChatGptWebProvider(new Cookies(), new Options("", "", 100));
        var firstEvents = new List<ProviderEvent>();
        await foreach (var item in provider.StreamChatAsync(request, CancellationToken.None)) firstEvents.Add(item);
        var read = Assert.Single(firstEvents.OfType<ToolCallStartedEvent>());
        Assert.Equal("Read", read.Name);

        var failedRead = new ToolResultBlock(read.Id, "Read", "File not found: index.html", true);
        var continued = request with
        {
            Messages =
            [
                .. request.Messages,
                new ChatMessage(Role.Assistant,
                    [new ToolCallBlock(read.Id, "Read", """{"path":"index.html"}""")]),
                new ChatMessage(Role.User, [failedRead]),
            ],
        };
        var continuedPrompt = ChatGptToolProtocol.RenderResult(failedRead) + "\n\n" + ChatGptToolProtocol.Closing();
        stub.ConversationReads.Enqueue(new ChatGptResponse(200, StoredConversation(
            (firstPrompt, readAction), (continuedPrompt, writeAction))));

        var events = new List<ProviderEvent>();
        await foreach (var item in provider.StreamChatAsync(continued, CancellationToken.None)) events.Add(item);

        Assert.Equal("conv-1", stub.AskedConversation);
        Assert.Equal(continuedPrompt, stub.AskedPrompt);
        Assert.Contains("RESULT Read FAILED: File not found: index.html", stub.AskedPrompt);
        Assert.Equal("Write", Assert.Single(events.OfType<ToolCallStartedEvent>()).Name);
        Assert.Equal("<!doctype html><title>Local page</title>",
            JsonNode.Parse(Assert.Single(events.OfType<ToolCallArgumentsDeltaEvent>()).Delta)!["content"]!.GetValue<string>());
        Assert.Empty(events.OfType<TextDeltaEvent>());
        Assert.Empty(events.OfType<TextPreviewEvent>());
        Assert.True(Assert.Single(events.OfType<ResponseCompletedEvent>()).WantsToolUse);
        Assert.Empty(stub.SavedAssets);
    }

    [Fact]
    public async Task AChatUnderTheThresholdIsKept()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var provider = new ChatGptWebProvider(new Cookies(), new Options("", "", 20));

        var turns = new List<string>();
        for (var i = 0; i < 19; i++)
        {
            turns.Add($"question {i}");
            await RunAsync(provider, Ask([.. turns]));
            turns.Add("an answer");
        }

        Assert.DoesNotContain(stub.Calls, c => c.StartsWith("DELETE "));
    }

    [Fact]
    public async Task ForkedSessionsKeepIndependentChatsWithIdenticalHistory()
    {
        var stub = new StubTransport();
        var scopes = new List<string>();
        ChatGptTransportRegistry.Register((cookies, _) => { scopes.Add(cookies.ScopeId); return stub; });
        var options = new Options("", "", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);
        await RunAsync(provider, Ask("hello") with { ConversationScopeId = "parent" });

        await RunAsync(provider, Ask("hello", "an answer", "fork question") with
        {
            ConversationScopeId = "fork",
        });
        Assert.Null(stub.AskedConversation);

        await RunAsync(provider, Ask("hello", "an answer", "parent question") with
        {
            ConversationScopeId = "parent",
        });
        Assert.Equal("conv-1", stub.AskedConversation);
        Assert.Equal(new[] { "parent", "fork", "parent" }, scopes);
        Assert.Equal(2, options.ChatGptChats.Count);
    }

    [Fact]
    public async Task AnotherProviderInstancesAdvanceIsReloadedBeforeContinuation()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("", "", 100);
        var firstProcess = new ChatGptWebProvider(new Cookies(), options);
        var secondProcess = new ChatGptWebProvider(new Cookies(), options);
        await RunAsync(firstProcess, Ask("hello"));
        await RunAsync(secondProcess, Ask("hello", "an answer", "second process"));

        await RunAsync(firstProcess, Ask("hello", "an answer", "second process", "an answer", "back to first"));

        Assert.Equal("conv-1", stub.AskedConversation);
        Assert.Equal("back to first\n\n" + ChatGptToolProtocol.TextOnlyBoundary, stub.AskedPrompt);
        Assert.Equal(3, Assert.Single(options.ChatGptChats).SentMessages);
    }

    [Fact]
    public async Task RequestsWithoutScopeCannotResumeAnotherRequestsChat()
    {
        var stub = new StubTransport();
        var scopes = new List<string>();
        ChatGptTransportRegistry.Register((cookies, _) => { scopes.Add(cookies.ScopeId); return stub; });
        var provider = new ChatGptWebProvider(new Cookies(), new Options("", "", 100));
        await RunAsync(provider, Ask("hello") with { ConversationScopeId = null });
        await RunAsync(provider, Ask("hello", "an answer", "again") with { ConversationScopeId = null });

        Assert.Null(stub.AskedConversation);
        Assert.NotEqual(scopes[0], scopes[1]);
    }

    [Fact]
    public async Task SwitchingProjectsOpensTheNextChatInTheNewProject()
    {
        var stub = new StubTransport();
        stub.Responses["/backend-api/gizmos/g-p-first"] = new ChatGptResponse(200,
            """{"gizmo":{"id":"g-p-first","display":{"name":"first"}}}""");
        stub.Responses["/backend-api/gizmos/g-p-second"] = new ChatGptResponse(200,
            """{"gizmo":{"id":"g-p-second","display":{"name":"second"}}}""");
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("first", "g-p-first", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);
        await RunAsync(provider, Ask("hello"));
        options.ChatGptProjectName = "second";
        options.SaveChatGptProjectId("g-p-second");

        await RunAsync(provider, Ask("hello", "an answer", "again"));

        Assert.Null(stub.AskedConversation);
        Assert.Equal("g-p-second", stub.AskedGizmo);
        Assert.Single(options.ChatGptChats);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("schema")]
    [InlineData("removed")]
    [InlineData("added")]
    public async Task AChangedToolContractIsReplacedInAFreshChat(string change)
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("", "", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);
        var original = new ToolDefinition("Read", "read original", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["file_path"] = new JsonObject { ["type"] = "string" } },
        });
        var first = Ask("hello") with { Tools = [original] };
        stub.ConversationReads.Enqueue(
            new ChatGptResponse(200, StoredTurn(ChatGptWebProvider.FlattenHistory(first), "an answer")));
        await RunAsync(provider, first);
        var oldContract = Assert.Single(options.ChatGptChats).ToolContractHash;
        ToolDefinition[] replacement = change switch
        {
            "description" => new[] { original with { Description = "read revised description" } },
            "schema" => [original with { InputSchema = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("new_required") } }],
            "removed" => [],
            _ => [original, new ToolDefinition("Write", "new action", new JsonObject())],
        };

        var changed = Ask("hello", "an answer", "again") with { Tools = replacement };
        // A changed contract starts a fresh remote conversation, even though the local prompt
        // replays older turns. Its stored branch therefore contains exactly one user message.
        stub.ConversationReads.Enqueue(
            new ChatGptResponse(200, StoredTurn(ChatGptWebProvider.FlattenHistory(changed), "an answer")));
        await RunAsync(provider, changed);

        Assert.Null(stub.AskedConversation);
        Assert.NotEqual(oldContract, Assert.Single(options.ChatGptChats).ToolContractHash);
        Assert.Contains("User: hello", stub.AskedPrompt);
        if (change == "description") Assert.Contains("read revised description", stub.AskedPrompt);
        if (change == "schema") Assert.Contains("new_required", stub.AskedPrompt);
        if (change == "removed") Assert.DoesNotContain("Actions:", stub.AskedPrompt);
    }

    [Fact]
    public async Task LeadingBlockChangesCannotContinueWithOldInstructions()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var provider = new ChatGptWebProvider(new Cookies(), new Options("", "", 100));
        await RunAsync(provider, Ask("hello") with { LeadingSystemBlocks = [new("old identity")] });

        await RunAsync(provider, Ask("hello", "an answer", "again") with
        {
            LeadingSystemBlocks = [new("new identity")],
        });

        Assert.Null(stub.AskedConversation);
        Assert.StartsWith("new identity\n\nsystem", stub.AskedPrompt);
    }

    [Fact]
    public async Task CancellationAfterSendRemovesDurableResumptionState()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("", "", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);
        await RunAsync(provider, Ask("hello"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stub.AskHandler = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new ChatGptUiReply("unreachable", "conv-1", null);
        };
        using var cancellation = new CancellationTokenSource();
        var next = Ask("hello", "an answer", "again");
        var running = RunAsync(provider, next, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(options.ChatGptChats);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        stub.AskHandler = null;
        await RunAsync(new ChatGptWebProvider(new Cookies(), options), next);

        Assert.Null(stub.AskedConversation);
        Assert.Contains("User: hello", stub.AskedPrompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopeOwnershipLastsUntilCancelledBrowserCleanupFinishes(bool cancelToken)
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        using var leaseGate = new SemaphoreSlim(1, 1);
        var options = new Options("", "", 100)
        {
            ScopeLeaseFactory = async (_, token) =>
            {
                await leaseGate.WaitAsync(token);
                return new ScopeLease(leaseGate);
            },
        };
        var firstProvider = new ChatGptWebProvider(new Cookies(), options);
        var secondProvider = new ChatGptWebProvider(new Cookies(), options);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        stub.AskHandler = async (ask, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                ask.Progress?.Invoke("working");
                try { await Task.Delay(Timeout.Infinite, token); }
                finally
                {
                    cleanupStarted.TrySetResult();
                    await releaseCleanup.Task;
                }
            }
            return new ChatGptUiReply("an answer", "conv-1", null);
        };
        using var cancellation = new CancellationTokenSource();
        await using var iterator = firstProvider.StreamChatAsync(Ask("first"), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Task ending;
        if (cancelToken)
        {
            cancellation.Cancel();
            ending = iterator.MoveNextAsync().AsTask();
        }
        else ending = iterator.DisposeAsync().AsTask();

        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = RunAsync(secondProvider, Ask("second"));
        try
        {
            Assert.False(ending.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { releaseCleanup.TrySetResult(); }

        if (cancelToken) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ending);
        else await ending.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    private sealed class ScopeLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrMalformedRepliesCannotBeResumedAfterRestart(bool malformedAction)
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("", "", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);
        var first = Ask("hello") with
        {
            Tools = malformedAction ? [new ToolDefinition("Read", "Read local files", new JsonObject())] : [],
        };
        await RunAsync(provider, first);
        stub.AskHandler = (_, _) => Task.FromResult(malformedAction
            ? new ChatGptUiReply("JARVIS_ACT {broken", "conv-1", null)
            : ChatGptUiReply.Failed("browser failure after send"));
        var next = Ask("hello", "an answer", "again") with { Tools = first.Tools };

        await Assert.ThrowsAsync<ProviderException>(() => RunAsync(provider, next));
        Assert.Empty(options.ChatGptChats);
        stub.AskHandler = null;
        await RunAsync(new ChatGptWebProvider(new Cookies(), options), next);

        Assert.Null(stub.AskedConversation);
    }

    [Fact]
    public async Task RecoveryStateMustBeSavedBeforeSending()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("", "", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);
        await RunAsync(provider, Ask("hello"));
        options.FailSaving = true;

        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            RunAsync(provider, Ask("hello", "an answer", "again")));

        Assert.Contains("message was not sent", error.Message);
        Assert.Single(stub.Asks);
    }

    [Fact]
    public async Task ConcurrentTurnsInOneScopeSerializeBeforeResuming()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var provider = new ChatGptWebProvider(new Cookies(), new Options("", "", 100));
        await RunAsync(provider, Ask("hello"));
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        stub.AskHandler = async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.SetResult();
                await release.Task;
            }
            return new ChatGptUiReply("an answer", "conv-1", null);
        };
        var first = RunAsync(provider, Ask("hello", "an answer", "first continuation"));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = RunAsync(provider, Ask("hello", "an answer", "concurrent branch"));
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        var asks = stub.Asks.ToArray();
        Assert.Equal("conv-1", asks[1].ConversationId);
        Assert.Null(asks[2].ConversationId);
    }

    [Fact]
    public async Task IndependentScopesDoNotWaitForEachOthersAnswer()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var options = new Options("", "", 100);
        var provider = new ChatGptWebProvider(new Cookies(), options);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        stub.AskHandler = async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2) bothEntered.SetResult();
            await release.Task;
            return new ChatGptUiReply("an answer", "conv-1", null);
        };
        var first = RunAsync(provider, Ask("first") with { ConversationScopeId = "first" });
        var second = RunAsync(provider, Ask("second") with { ConversationScopeId = "second" });
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
        }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, options.ChatGptChats.Count);
    }

    [Fact]
    public async Task ReplayedHistoryCarriesOldPicturesIntoAFreshChat()
    {
        var stub = new StubTransport { CarriesImages = true };
        ChatGptTransportRegistry.Register((_, _) => stub);
        var request = Ask("hello") with
        {
            Messages =
            [
                new ChatMessage(Role.User, [new TextBlock("look at this"), new ImageBlock("image/png", "OLD-IMAGE")]),
                new ChatMessage(Role.Assistant, [new TextBlock("an answer")]),
                ChatMessage.FromUserText("what changed in that picture?"),
            ],
        };

        await RunAsync(new ChatGptWebProvider(new Cookies(), new Options("", "", 100)), request);

        Assert.Equal("OLD-IMAGE", Assert.Single(stub.AskedImages).Base64);
    }

    [Fact]
    public async Task AnswerPreviewsStayDisabledUntilStoredOutputIsVerified()
    {
        var stub = new StubTransport();
        ChatGptTransportRegistry.Register((_, _) => stub);
        var request = Ask("hello");
        stub.Responses["/backend-api/conversation/conv-1"] =
            new ChatGptResponse(200, StoredTurn(ChatGptWebProvider.FlattenHistory(request), "Exact answer"));
        stub.AskHandler = (ask, _) =>
        {
            ask.AnswerPreview?.Invoke("JARVIS");
            ask.AnswerPreview?.Invoke("JARVIS_ACT {");
            ask.AnswerPreview?.Invoke("Rendered preview");
            return Task.FromResult(new ChatGptUiReply("Page text", "conv-1", null));
        };
        var events = new List<ProviderEvent>();
        await foreach (var item in new ChatGptWebProvider(new Cookies(), new Options("", "", 100))
            .StreamChatAsync(request, CancellationToken.None)) events.Add(item);

        Assert.Empty(events.OfType<TextPreviewEvent>());
        Assert.Equal("Exact answer", Assert.Single(events.OfType<TextDeltaEvent>()).Delta);
        Assert.True(Assert.Single(events.OfType<ResponseCompletedEvent>()).Usage.IsEstimated);
    }
}
