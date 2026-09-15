using System.Net;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Anthropic;

namespace JarvisCode.Providers.Tests;

public sealed class AnthropicProviderTests
{
    [Fact]
    public void RequestBody_MapsMessagesToolsAndSystem()
    {
        var body = AnthropicProvider.BuildRequestBody(ProviderTestHelpers.SampleRequest());

        Assert.Equal("test-model", body["model"]!.GetValue<string>());
        var systemBlock = body["system"]!.AsArray().Single()!;
        Assert.Equal("system prompt", systemBlock["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", systemBlock["cache_control"]!["type"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());

        var messages = Assert.IsType<JsonArray>(body["messages"]);
        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("assistant", messages[1]!["role"]!.GetValue<string>());

        var toolUse = messages[1]!["content"]![1]!;
        Assert.Equal("tool_use", toolUse["type"]!.GetValue<string>());
        Assert.Equal("call-1", toolUse["id"]!.GetValue<string>());
        // Arguments must be sent as a JSON object, not an escaped string.
        Assert.Equal("a.txt", toolUse["input"]!["file_path"]!.GetValue<string>());

        var toolResult = messages[2]!["content"]![0]!;
        Assert.Equal("tool_result", toolResult["type"]!.GetValue<string>());
        Assert.Equal("call-1", toolResult["tool_use_id"]!.GetValue<string>());

        var tool = body["tools"]![0]!;
        Assert.Equal("Read", tool["name"]!.GetValue<string>());
        Assert.NotNull(tool["input_schema"]!["properties"]);
    }

    [Fact]
    public void RequestBody_ToolResultWithImages_UsesBlockArrayContent()
    {
        var request = ProviderTestHelpers.SampleRequest() with
        {
            Messages =
            [
                ChatMessage.FromUserText("look"),
                new ChatMessage(Role.Assistant, [new ToolCallBlock("call-9", "peek", "{}")]),
                ChatMessage.FromToolResults(
                [
                    new ToolResultBlock("call-9", "peek", "captured", IsError: false,
                        Images: [new ImageBlock("image/png", "aGVsbG8=")]),
                ]),
            ],
        };

        var body = AnthropicProvider.BuildRequestBody(request);
        var toolResult = body["messages"]!.AsArray()[2]!["content"]![0]!;
        Assert.Equal("tool_result", toolResult["type"]!.GetValue<string>());

        var parts = Assert.IsType<JsonArray>(toolResult["content"]);
        Assert.Equal("text", parts[0]!["type"]!.GetValue<string>());
        Assert.Equal("captured", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("image", parts[1]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", parts[1]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal("aGVsbG8=", parts[1]!["source"]!["data"]!.GetValue<string>());

        // Plain results keep the string form.
        var plain = AnthropicProvider.BuildRequestBody(ProviderTestHelpers.SampleRequest());
        Assert.IsAssignableFrom<JsonValue>(plain["messages"]![2]!["content"]![0]!["content"]);
    }

    [Fact]
    public void RequestBody_PlacesCacheBreakpoints_OnLastToolAndLastMessageBlock()
    {
        var body = AnthropicProvider.BuildRequestBody(ProviderTestHelpers.SampleRequest());

        var lastTool = body["tools"]!.AsArray()[^1]!;
        Assert.Equal("ephemeral", lastTool["cache_control"]!["type"]!.GetValue<string>());

        var messages = body["messages"]!.AsArray();
        var lastBlock = messages[^1]!["content"]!.AsArray()[^1]!;
        Assert.Equal("ephemeral", lastBlock["cache_control"]!["type"]!.GetValue<string>());
        // Only the final message carries the moving breakpoint.
        var firstBlock = messages[0]!["content"]!.AsArray()[0]!;
        Assert.Null(firstBlock["cache_control"]);
    }

    [Fact]
    public async Task Stream_ParsesCacheUsageIntoTotals()
    {
        const string sse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":900,\"cache_creation_input_tokens\":50}}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":7}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var provider = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out _), new FakeKeySource());
        var events = await ProviderTestHelpers.CollectAsync(provider, ProviderTestHelpers.SampleRequest());

        var usage = Assert.IsType<ResponseCompletedEvent>(events.Last()).Usage;
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(900, usage.CacheReadInputTokens);
        Assert.Equal(50, usage.CacheCreationInputTokens);
        Assert.Equal(960, usage.TotalInputTokens);
    }

    [Fact]
    public async Task Stream_TakesTheInputAndCacheCountsMessageDeltaReports()
    {
        // The reference SDK overwrites every usage field message_delta carries:
        // its message_delta arm sets output_tokens, then input_tokens,
        // cache_creation_input_tokens and cache_read_input_tokens whenever each
        // is non-null. An endpoint that reports the authoritative totals only
        // there — a relay in front of Anthropic does — must still be accounted
        // for, or a cache hit reads as a context that shrank.
        const string sse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":0,\"output_tokens\":1}}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"input_tokens\":12,\"cache_read_input_tokens\":19000,\"cache_creation_input_tokens\":40,\"output_tokens\":7}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var provider = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out _), new FakeKeySource());
        var events = await ProviderTestHelpers.CollectAsync(provider, ProviderTestHelpers.SampleRequest());

        var usage = Assert.IsType<ResponseCompletedEvent>(events.Last()).Usage;
        Assert.Equal(12, usage.InputTokens);
        Assert.Equal(19000, usage.CacheReadInputTokens);
        Assert.Equal(40, usage.CacheCreationInputTokens);
        Assert.Equal(19052, usage.TotalInputTokens);
        Assert.Equal(7, usage.OutputTokens);
    }

    [Fact]
    public async Task Stream_CountsTheCacheReadARelayReportsOnlyAtTheEnd()
    {
        // A capture of a real llmapi.pro response (claude-fable-5-1, 2026-09-03),
        // trimmed to its usage events. message_start states the input but calls
        // the cache read zero; the true 43,452 arrives only in message_delta.
        // Reading the start alone therefore threw away 43,452 of a 248,097-token
        // context — and since the uncached half shrinks as more of the prefix is
        // cached, the context read as though it had shrunk, which is what froze
        // the token budget, the ring, /context and the compaction threshold.
        const string sse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-fable-5-1\",\"usage\":" +
            "{\"input_tokens\":204645,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":0}}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":" +
            "{\"input_tokens\":204645,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":43452,\"output_tokens\":39," +
            "\"output_tokens_details\":{\"thinking_tokens\":14}}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var provider = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out _), new FakeKeySource());
        var events = await ProviderTestHelpers.CollectAsync(provider, ProviderTestHelpers.SampleRequest());

        var usage = Assert.IsType<ResponseCompletedEvent>(events.Last()).Usage;
        Assert.Equal(43_452, usage.CacheReadInputTokens);
        Assert.Equal(248_097, usage.TotalInputTokens);
        Assert.Equal(39, usage.OutputTokens);
    }

    [Fact]
    public async Task Stream_KeepsTheStartCountsMessageDeltaLeavesOut()
    {
        // Each overwrite is guarded on the field being present, so a delta
        // carrying only output_tokens leaves the start's counts standing.
        const string sse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":900,\"cache_creation_input_tokens\":50}}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":7}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var provider = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out _), new FakeKeySource());
        var events = await ProviderTestHelpers.CollectAsync(provider, ProviderTestHelpers.SampleRequest());

        var usage = Assert.IsType<ResponseCompletedEvent>(events.Last()).Usage;
        Assert.Equal(960, usage.TotalInputTokens);
        Assert.Equal(7, usage.OutputTokens);
    }

    [Fact]
    public async Task Stream_ParsesTextToolUseAndUsage()
    {
        const string sse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":42}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"I'll read \"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"the file\"}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Read\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"file_path\\\":\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"a.txt\\\"}\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":17}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var provider = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out var handler), new FakeKeySource());
        var events = await ProviderTestHelpers.CollectAsync(provider, ProviderTestHelpers.SampleRequest());

        Assert.Equal("test-key", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("I'll read the file",
            string.Concat(events.OfType<TextDeltaEvent>().Select(e => e.Delta)));
        var started = Assert.Single(events.OfType<ToolCallStartedEvent>());
        Assert.Equal("toolu_1", started.Id);
        Assert.Equal("Read", started.Name);
        Assert.Equal("""{"file_path":"a.txt"}""",
            string.Concat(events.OfType<ToolCallArgumentsDeltaEvent>().Select(e => e.Delta)));
        var completed = Assert.IsType<ResponseCompletedEvent>(events.Last());
        Assert.True(completed.WantsToolUse);
        Assert.Equal(42, completed.Usage.InputTokens);
        Assert.Equal(17, completed.Usage.OutputTokens);
    }

    [Fact]
    public async Task Stream_ParsesThinkingWithSignature_AndRedactedAsRaw()
    {
        const string sse =
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"let me \"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"reason\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig-abc\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"redacted_thinking\",\"data\":\"opaque-bytes\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"text_delta\",\"text\":\"answer\"}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var provider = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out _), new FakeKeySource());
        var events = await ProviderTestHelpers.CollectAsync(provider, ProviderTestHelpers.SampleRequest());

        Assert.Equal("let me reason",
            string.Concat(events.OfType<ThinkingDeltaEvent>().Select(e => e.Delta)));
        var completed = Assert.Single(events.OfType<ThinkingCompletedEvent>());
        Assert.Equal("let me reason", completed.Thinking);
        Assert.Equal("sig-abc", completed.Signature);
        var raw = Assert.Single(events.OfType<RawBlockEvent>());
        Assert.Contains("redacted_thinking", raw.RawJson);
        Assert.Contains("opaque-bytes", raw.RawJson);
        Assert.Equal("answer", string.Concat(events.OfType<TextDeltaEvent>().Select(e => e.Delta)));
    }

    [Fact]
    public void RequestBody_ReplaysSignedThinkingAndOwnRawBlocks_SkipsForeign()
    {
        var request = ProviderTestHelpers.SampleRequest() with
        {
            Messages =
            [
                ChatMessage.FromUserText("go"),
                new ChatMessage(Role.Assistant,
                [
                    new ThinkingBlock("reasoning here", "sig-1"),
                    new ThinkingBlock("unsigned - display only", null),
                    // Signed, but the body never arrived — a relay has been seen
                    // to open, sign and close a thinking block with no delta in
                    // between. Replaying it asks the API to verify a signature
                    // against nothing.
                    new ThinkingBlock("", "sig-over-nothing"),
                    new ThinkingBlock("   ", "sig-over-whitespace"),
                    new RawProviderBlock("anthropic", """{"type":"redacted_thinking","data":"x"}"""),
                    new RawProviderBlock("openai", """{"foreign":true}"""),
                    new TextBlock("the answer"),
                ]),
            ],
        };

        var body = AnthropicProvider.BuildRequestBody(request);

        var content = body["messages"]!.AsArray()[1]!["content"]!.AsArray();
        Assert.Equal(3, content.Count); // signed non-empty thinking + own raw + text
        Assert.Equal("thinking", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("sig-1", content[0]!["signature"]!.GetValue<string>());
        Assert.DoesNotContain("sig-over-nothing", body.ToJsonString());
        Assert.DoesNotContain("sig-over-whitespace", body.ToJsonString());
        Assert.Equal("redacted_thinking", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("text", content[2]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void RequestBody_AddsWebSearchServerTool_OnlyWhenEnabled()
    {
        var enabled = AnthropicProvider.BuildRequestBody(
            ProviderTestHelpers.SampleRequest() with { EnableWebSearch = true });
        var tools = enabled["tools"]!.AsArray();
        var webSearch = tools.Single(t => t!["name"]?.GetValue<string>() == "web_search")!;
        Assert.Equal("web_search_20250305", webSearch["type"]!.GetValue<string>());
        Assert.Equal(5, webSearch["max_uses"]!.GetValue<int>());
        // The cache breakpoint stays on the final tool entry.
        Assert.NotNull(tools[^1]!["cache_control"]);

        var disabled = AnthropicProvider.BuildRequestBody(ProviderTestHelpers.SampleRequest());
        Assert.DoesNotContain(disabled["tools"]!.AsArray(),
            t => t!["name"]?.GetValue<string>() == "web_search");
    }

    [Fact]
    public async Task Stream_ServerToolUse_EmitsNoticeAndKeepsRawBlocksWithInput()
    {
        const string sse =
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"server_tool_use\",\"id\":\"srvtoolu_1\",\"name\":\"web_search\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"query\\\":\\\"dotnet 10\\\"}\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"web_search_tool_result\",\"tool_use_id\":\"srvtoolu_1\",\"content\":[]}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"text_delta\",\"text\":\"found it\"}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var provider = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out _), new FakeKeySource());
        var events = await ProviderTestHelpers.CollectAsync(provider, ProviderTestHelpers.SampleRequest());

        Assert.Equal("web_search", Assert.Single(events.OfType<ServerToolNoticeEvent>()).ToolName);
        var rawEvents = events.OfType<RawBlockEvent>().ToList();
        Assert.Equal(2, rawEvents.Count);
        Assert.Contains("dotnet 10", rawEvents[0].RawJson); // streamed input merged into the block
        Assert.Contains("web_search_tool_result", rawEvents[1].RawJson);
        // No client-side tool call must be synthesized from server tool blocks.
        Assert.Empty(events.OfType<ToolCallStartedEvent>());
    }

    [Fact]
    public async Task HttpError_ThrowsProviderExceptionWithVendorMessage()
    {
        var body = """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""";
        var provider = new AnthropicProvider(
            ProviderTestHelpers.ClientFor(body, out _, HttpStatusCode.Unauthorized), new FakeKeySource());

        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => ProviderTestHelpers.CollectAsync(provider, ProviderTestHelpers.SampleRequest()));
        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid x-api-key", ex.Message);
    }

    [Fact]
    public async Task MissingApiKey_ThrowsActionableError()
    {
        var provider = new AnthropicProvider(new HttpClient(), new FakeKeySource(key: null));
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => ProviderTestHelpers.CollectAsync(provider, ProviderTestHelpers.SampleRequest()));
        Assert.Contains("No API key", ex.Message);
    }

    [Fact]
    public async Task TheCliBetaList_RidesThinkingRequestsOnly()
    {
        const string sse = "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var thinking = ProviderTestHelpers.SampleRequest() with { ThinkingEffort = ThinkingEffort.High };
        var provider = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out var handler), new FakeKeySource());
        await ProviderTestHelpers.CollectAsync(provider, thinking);
        // One comma-joined header carrying the reference CLI's list for this
        // model class (SampleRequest's "test-model" reads as an older model).
        Assert.Equal(AnthropicEffort.EnabledBetas,
            handler.LastRequest!.Headers.GetValues("anthropic-beta").Single());
        Assert.Contains(AnthropicProvider.InterleavedThinkingBeta, AnthropicEffort.EnabledBetas);

        var plain = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out var plainHandler), new FakeKeySource());
        await ProviderTestHelpers.CollectAsync(plain, ProviderTestHelpers.SampleRequest());
        Assert.False(plainHandler.LastRequest!.Headers.Contains("anthropic-beta"));
    }

    [Fact]
    public async Task TheBetaNamespaceUrl_RidesThinkingRequestsToAnthropicItself()
    {
        // The vendor's own API is where ?beta=true belongs, and the captured CLI
        // fixtures pin it. A relay speaking this wire switches it off instead —
        // this is the default that switch flips.
        const string sse = "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var provider = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out var handler), new FakeKeySource());
        await ProviderTestHelpers.CollectAsync(
            provider, ProviderTestHelpers.SampleRequest() with { ThinkingEffort = ThinkingEffort.High });
        Assert.Equal(AnthropicProvider.DefaultEndpoint + "?beta=true", handler.LastRequest!.RequestUri!.ToString());

        var plain = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out var plainHandler), new FakeKeySource());
        await ProviderTestHelpers.CollectAsync(plain, ProviderTestHelpers.SampleRequest());
        Assert.Equal(AnthropicProvider.DefaultEndpoint, plainHandler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ErrorEvent_MidStream_Throws()
    {
        const string sse =
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n" +
            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";

        var provider = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out _), new FakeKeySource());
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => ProviderTestHelpers.CollectAsync(provider, ProviderTestHelpers.SampleRequest()));
        Assert.Contains("Overloaded", ex.Message);
    }

    /// <summary>
    /// The same side query on claude-fable-5, held against the CLI's captured
    /// request for that model. The reference sends no thinking config for a
    /// model that rejects having thinking turned off, and its request builder
    /// then demotes the forced tool_choice to auto because thinking is live.
    /// </summary>
    [Fact]
    public void RequestBody_WebSearchSideQueryOverride_FableDropsThinkingAndForcedChoice()
    {
        var request = new LlmRequest
        {
            ModelId = "claude-fable-5",
            SystemPrompt = "You are an assistant for performing a web search tool use",
            Messages =
                [ChatMessage.FromUserText("Perform a web search for the query: anthropic claude release notes")],
            Tools = [],
            EnableWebSearch = false,
            ThinkingEffort = ThinkingEffort.High,
            BodyOverride = new JsonObject
            {
                ["system"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "You are an assistant for performing a web search tool use",
                }),
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "Perform a web search for the query: anthropic claude release notes",
                    }),
                }),
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["type"] = "web_search_20250305",
                    ["name"] = "web_search",
                    ["max_uses"] = 8,
                }),
                ["tool_choice"] = new JsonObject { ["type"] = "auto" },
                ["thinking"] = null,
                ["context_management"] = null,
            },
        };

        var body = RequestBodyOverride.Apply(
            AnthropicProvider.BuildRequestBody(request), request.BodyOverride);

        // Captured: {"model":"claude-fable-5",…,"tools":[{"type":
        // "web_search_20250305","name":"web_search","max_uses":8}],"tool_choice":
        // {"type":"auto"},"max_tokens":64000,"output_config":{"effort":"high"},
        // "stream":true} — with no thinking key at all.
        Assert.Equal("claude-fable-5", body["model"]!.GetValue<string>());
        Assert.Equal(64000, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("""{"type":"auto"}""", body["tool_choice"]!.ToJsonString());
        Assert.Equal("""{"effort":"high"}""", body["output_config"]!.ToJsonString());
        Assert.False(body.ContainsKey("thinking"));
        Assert.False(body.ContainsKey("context_management"));
        Assert.DoesNotContain("cache_control", body.ToJsonString());
    }

    /// <summary>
    /// The vendor web-search fallback's side query (the App's
    /// VendorWebSearchTool.BuildSideQuery, replicated here — Providers.Tests
    /// cannot reference the App), held against the request the installed CLI
    /// 2.1.251 was captured sending for a WebSearch call on claude-opus-5.
    /// Deliberate deltas, as everywhere in this adapter: no metadata.user_id and
    /// no billing/SDK system preamble.
    /// </summary>
    [Fact]
    public void RequestBody_WebSearchSideQueryOverride_MatchesCapturedBody()
    {
        var request = new LlmRequest
        {
            ModelId = "claude-opus-5",
            SystemPrompt = "You are an assistant for performing a web search tool use",
            Messages = [ChatMessage.FromUserText("Perform a web search for the query: jarvis parity probe")],
            Tools = [],
            EnableWebSearch = false,
            ThinkingEffort = ThinkingEffort.High,
            BodyOverride = new JsonObject
            {
                ["system"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "You are an assistant for performing a web search tool use",
                }),
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "Perform a web search for the query: jarvis parity probe",
                    }),
                }),
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["type"] = "web_search_20250305",
                    ["name"] = "web_search",
                    ["max_uses"] = 8,
                }),
                ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "web_search" },
                ["thinking"] = new JsonObject { ["type"] = "disabled", ["budget_tokens"] = null },
                ["context_management"] = null,
            },
        };

        var body = RequestBodyOverride.Apply(
            AnthropicProvider.BuildRequestBody(request), request.BodyOverride);

        // Captured: {"model":"claude-opus-5","messages":[{"role":"user","content":
        // [{"type":"text","text":"Perform a web search for the query: …"}]}],
        // "system":[…,{"type":"text","text":"You are an assistant for performing a
        // web search tool use"}],"tools":[{"type":"web_search_20250305","name":
        // "web_search","max_uses":8}],"tool_choice":{"type":"tool","name":
        // "web_search"},"metadata":{…},"max_tokens":64000,"thinking":{"type":
        // "disabled"},"output_config":{"effort":"high"},"stream":true}
        Assert.Equal("claude-opus-5", body["model"]!.GetValue<string>());
        Assert.Equal(64000, body["max_tokens"]!.GetValue<int>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(
            """[{"type":"text","text":"You are an assistant for performing a web search tool use"}]""",
            body["system"]!.ToJsonString());
        Assert.Equal(
            """[{"role":"user","content":[{"type":"text","text":"Perform a web search for the query: jarvis parity probe"}]}]""",
            body["messages"]!.ToJsonString());
        Assert.Equal(
            """[{"type":"web_search_20250305","name":"web_search","max_uses":8}]""",
            body["tools"]!.ToJsonString());
        Assert.Equal("""{"type":"tool","name":"web_search"}""", body["tool_choice"]!.ToJsonString());
        Assert.Equal("""{"type":"disabled"}""", body["thinking"]!.ToJsonString());
        Assert.Equal("""{"effort":"high"}""", body["output_config"]!.ToJsonString());
        Assert.False(body.ContainsKey("context_management"));
        // enablePromptCaching:false in the reference — no breakpoint anywhere.
        Assert.DoesNotContain("cache_control", body.ToJsonString());
    }
}
