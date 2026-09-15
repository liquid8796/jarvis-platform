using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using JarvisCode.App.Services;
using JarvisCode.Cli;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Hooks;
using JarvisCode.Core.Models;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.Cli.Tests;

/// <summary>In-memory protocol/model fixtures only: no account, network, process or user hooks.</summary>
public sealed class PrintProtocolTests
{
    private const string AnswerSchema = """
        {"type":"object","required":["answer"],"additionalProperties":false,"properties":{"answer":{"type":"integer"}}}
        """;
    private static LlmRequest Request(string model = "test") => new()
    { ModelId = model, SystemPrompt = "system", Messages = [ChatMessage.FromUserText("question")] };
    private static ProviderEvent[] Answer(string text, Usage? usage = null) =>
        [new TextDeltaEvent(text), new ResponseCompletedEvent(false, usage ?? new Usage(10, 2), "end_turn")];

    [Fact]
    public void Implemented_print_flags_parse_and_are_not_left_in_the_refusal_table()
    {
        var parsed = CommandLine.Parse(["-p", "--input-format", "stream-json", "--output-format", "stream-json",
            "--include-partial-messages", "--include-hook-events", "--replay-user-messages", "--forward-subagent-text",
            "--permission-prompts", "host", "--json-schema", AnswerSchema, "--max-budget-usd", "0.25"], RootOptions.Specs);
        Assert.Null(parsed.Error);
        var options = CliOptions.From(parsed);
        PrintRunner.ValidateOptions(options);
        Assert.True(options.IncludePartialMessages && options.IncludeHookEvents && options.ReplayUserMessages && options.ForwardSubagentText);
        Assert.Equal(.25m, options.MaxBudgetUsd);
        Assert.Equal(AnswerSchema, options.JsonSchema);
        foreach (var flag in new[] { "include-partial-messages", "include-hook-events", "replay-user-messages",
                     "forward-subagent-text", "json-schema", "max-budget-usd" })
            Assert.False(RootOptions.Unsupported.ContainsKey(flag));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("invalid")]
    public void Invalid_dollar_ceiling_fails_before_a_session_can_start(string amount) =>
        Assert.NotNull(CommandLine.Parse(["--max-budget-usd", amount], RootOptions.Specs).Error);

    [Fact]
    public void Stream_input_cannot_silently_run_as_text_output() => Assert.Throws<CliError>(() =>
        PrintRunner.ValidateOptions(new CliOptions { Prompt = "", InputFormat = "stream-json" }));

    [Fact]
    public void Input_preserves_multimodal_order_and_replay_identity()
    {
        var message = CliInputMessage.Parse(JsonNode.Parse("""
            {"type":"user","uuid":"caller-1","message":{"role":"user","content":[
              {"type":"text","text":"before"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"aGk="}},
              {"type":"text","text":"after"}]}}
            """)!.AsObject());
        Assert.Equal("caller-1", message.Uuid);
        Assert.Collection(message.Message.Content, x => Assert.Equal("before", Assert.IsType<TextBlock>(x).Text),
            x => Assert.Equal("aGk=", Assert.IsType<ImageBlock>(x).Base64Data),
            x => Assert.Equal("after", Assert.IsType<TextBlock>(x).Text));
        var writer = new StringWriter();
        new StreamJson(writer, "session").EmitUser(message);
        var replay = JsonNode.Parse(writer.ToString())!;
        Assert.Equal("caller-1", replay["uuid"]!.GetValue<string>());
        Assert.Equal("aGk=", replay["message"]!["content"]![1]!["source"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Input_pump_orders_users_deduplicates_redelivery_and_answers_initialize()
    {
        var lines = """
            {"type":"control_request","request_id":"init","request":{"subtype":"initialize"}}
            {"type":"user","uuid":"a","message":{"role":"user","content":"one"}}
            {"type":"user","uuid":"a","message":{"role":"user","content":"one"}}
            {"type":"user","uuid":"b","message":{"role":"user","content":"two"}}
            """;
        var writer = new StringWriter();
        using var input = new StreamJsonInput(new StringReader(lines), new StreamJson(writer, "s"),
            (_, _) => Task.FromResult(new JsonObject { ["ready"] = true }));
        await input.PumpAsync(default);
        var messages = new List<CliInputMessage>();
        await foreach (var message in input.Messages.ReadAllAsync()) messages.Add(message);
        Assert.Equal(["one", "two"], messages.Select(m => m.Message.GetText()));
        var response = JsonNode.Parse(writer.ToString())!["response"]!;
        Assert.Equal("init", response["request_id"]!.GetValue<string>());
        Assert.True(response["response"]!["ready"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Permission_response_is_processed_while_the_tool_waits_and_eof_denies_pending_calls()
    {
        var reader = new DuplexReader();
        var writer = new RecordingWriter();
        using var input = new StreamJsonInput(reader, new StreamJson(writer, "s"), (_, _) => Task.FromResult(new JsonObject()));
        var pump = input.PumpAsync(default);
        var pending = input.RequestAsync(new JsonObject { ["subtype"] = "can_use_tool" }, default);
        var request = JsonNode.Parse(await writer.NextAsync())!;
        Respond(reader, request, new JsonObject { ["behavior"] = "allow" });
        Assert.Equal("allow", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))!["behavior"]!.GetValue<string>());
        var disconnected = input.RequestAsync(new JsonObject { ["subtype"] = "can_use_tool" }, default);
        await writer.NextAsync();
        reader.Complete();
        await pump;
        Assert.Null(await disconnected.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Sdk_updated_input_is_applied_but_cannot_escape_workspace_rules()
    {
        foreach (var escape in new[] { false, true })
        {
            var reader = new DuplexReader();
            var writer = new RecordingWriter();
            using var input = new StreamJsonInput(reader, new StreamJson(writer, "s"), (_, _) => Task.FromResult(new JsonObject()));
            var pump = input.PumpAsync(default);
            var workspace = Path.Combine(Path.GetTempPath(), "jarvis-sdk-permission-" + Guid.NewGuid().ToString("N"));
            var gate = new UiPermissionGate { Mode = PermissionMode.Manual, WorkingDirectory = workspace,
                IgnoreSettingsFiles = true, RestrictToWorkspace = true };
            var bridge = new SdkPermissionBridge(gate, input);
            gate.PromptAsync = bridge.PromptAsync;
            var args = new JsonObject { ["file_path"] = Path.Combine(workspace, "original.txt"), ["content"] = "old" };
            var request = bridge.RequestAsync(new PermissionRequest(new WriteStub(), args, "write", "call-1"), default).AsTask();
            var wire = JsonNode.Parse(await writer.NextAsync())!;
            var nextPath = Path.Combine(escape ? Path.GetTempPath() : workspace, "updated.txt");
            Respond(reader, wire, new JsonObject { ["behavior"] = "allow", ["updatedInput"] =
                new JsonObject { ["file_path"] = nextPath, ["content"] = "new" } });
            Assert.Equal(escape ? PermissionDecision.Deny : PermissionDecision.Allow,
                await request.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(nextPath, args["file_path"]!.GetValue<string>());
            reader.Complete();
            await pump;
        }
    }

    [Fact]
    public async Task Sdk_deny_interrupt_cancels_the_active_turn_and_keeps_the_reason()
    {
        var interrupted = false;
        var gate = new UiPermissionGate();
        var bridge = new SdkPermissionBridge(gate, (_, _) => Task.FromResult<JsonObject?>(new JsonObject
        { ["behavior"] = "deny", ["interrupt"] = true, ["message"] = "fixture interruption" }))
        { Interrupt = () => interrupted = true };
        var result = await bridge.PromptAsync(new PermissionPrompt("Write", null, null, null, null,
            CallRisk.Standard, CallId: "fixture-call"), CancellationToken.None);
        Assert.Equal(PermissionDecision.Deny, result);
        Assert.True(interrupted);
        Assert.Equal("fixture interruption", bridge.TakeDenialReason("fixture-call"));
    }

    [Fact]
    public async Task Sdk_permission_update_is_rechecked_before_the_current_tool_runs()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "sdk-update-" + Guid.NewGuid().ToString("N"));
        var gate = new UiPermissionGate { Mode = PermissionMode.Manual, WorkingDirectory = workspace, IgnoreSettingsFiles = true };
        var bridge = new SdkPermissionBridge(gate, (_, _) => Task.FromResult<JsonObject?>(new JsonObject
        { ["behavior"] = "allow", ["updatedPermissions"] = new JsonArray(new JsonObject { ["type"] = "fixture" }) }))
        {
            UpdatePermissions = (_, _) => { gate.SdkRuleLines = ["deny Write"]; return Task.CompletedTask; },
        };
        gate.PromptAsync = bridge.PromptAsync;
        var result = await bridge.RequestAsync(new PermissionRequest(new WriteStub(), new JsonObject
        { ["file_path"] = Path.Combine(workspace, "file.txt"), ["content"] = "fixture" }, "write", "call"), CancellationToken.None);
        Assert.Equal(PermissionDecision.Deny, result);
    }

    [Fact]
    public async Task Structured_output_retries_invalid_values_without_repeating_tool_actions()
    {
        var fake = new ScriptedProvider(Answer("not JSON"), Answer("{\"answer\":\"wrong\"}"), Answer("{\"answer\":42}"));
        var policy = new CliStructuredOutput(AnswerSchema);
        var result = await Collect(policy.Wrap(fake).StreamChatAsync(Request(), default));
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal("{\"answer\":42}", string.Concat(result.OfType<TextDeltaEvent>().Select(x => x.Delta)));
        Assert.Equal(42, policy.Value!["answer"]!.GetValue<int>());
        Assert.All(fake.Requests.Skip(1), retry => Assert.Empty(retry.Tools));
        Assert.Contains("failed JSON Schema validation", fake.Requests[1].Messages.Last().GetText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structured_output_does_not_open_a_tool_free_repair_on_an_unsafe_provider(bool decorated)
    {
        var fake = new ScriptedProvider(Answer("not JSON"))
        { Capabilities = new() { SupportsToolFreeInference = false } };
        ILlmProvider provider = decorated
            ? new CliBetaRegistry(new ProviderRegistry([fake]), ["fixture-beta"]).Get(fake.Id)
            : fake;
        var policy = new CliStructuredOutput(AnswerSchema);

        var failure = await Assert.ThrowsAsync<CliError>(() => Collect(policy.Wrap(provider).StreamChatAsync(Request(), default)));

        Assert.Single(fake.Requests);
        Assert.False(policy.HasValue);
        Assert.Contains("No correction request was sent", failure.Message);
        Assert.Equal(failure.Message, policy.FailureDetail);
    }

    [Fact]
    public async Task Structured_output_still_accepts_valid_browser_values_and_forwards_tool_turns()
    {
        var policy = new CliStructuredOutput(AnswerSchema);
        var valid = new ScriptedProvider(Answer("{\"answer\":42}"))
        { Capabilities = new() { SupportsToolFreeInference = false } };
        await Collect(policy.Wrap(valid).StreamChatAsync(Request(), default));
        Assert.Single(valid.Requests);
        Assert.Equal(42, policy.Value!["answer"]!.GetValue<int>());

        policy.BeginTurn();
        var tools = new ScriptedProvider([new ToolCallStartedEvent(0, "read-1", "Read"),
            new ToolCallArgumentsDeltaEvent(0, "{}"), new ResponseCompletedEvent(true, Usage.Zero, "tool_use")])
        { Capabilities = new() { SupportsToolFreeInference = false } };
        var events = await Collect(policy.Wrap(tools).StreamChatAsync(Request(), default));
        Assert.Contains(events, e => e is ToolCallStartedEvent);
        Assert.Single(tools.Requests);
        Assert.False(policy.HasValue);
        Assert.Null(policy.FailureDetail);
    }

    [Fact]
    public async Task Structured_output_has_a_bounded_failure_and_retains_tool_and_refusal_turns()
    {
        var policy = new CliStructuredOutput(AnswerSchema);
        var bad = new ScriptedProvider(Answer("{}"), Answer("{}"), Answer("{}"));
        await Assert.ThrowsAsync<CliError>(() => Collect(policy.Wrap(bad).StreamChatAsync(Request(), default)));
        Assert.Contains("3 attempts", policy.FailureDetail);
        policy.BeginTurn();
        var tool = new ScriptedProvider([new ToolCallStartedEvent(0, "t", "Read"),
            new ToolCallArgumentsDeltaEvent(0, "{}"), new ResponseCompletedEvent(true, Usage.Zero, "tool_use")]);
        Assert.Contains(await Collect(policy.Wrap(tool).StreamChatAsync(Request(), default)), e => e is ToolCallStartedEvent);
        Assert.False(policy.HasValue);
        var refusal = new ScriptedProvider([new TextDeltaEvent("Declined"), new ResponseCompletedEvent(false, Usage.Zero, "refusal")]);
        await Collect(policy.Wrap(refusal).StreamChatAsync(Request(), default));
        Assert.Single(refusal.Requests);
        Assert.False(policy.HasValue);
    }

    [Fact]
    public async Task Schema_and_partial_observers_do_not_change_compaction_requests()
    {
        var policy = new CliStructuredOutput(AnswerSchema);
        var fake = new ScriptedProvider(Answer("ordinary compaction summary"));
        var observed = 0;
        var wrapped = new CliObservedProvider(policy.Wrap(fake, applies: r => r.SystemPrompt == "main"),
            _ => observed++, _ => observed++, r => r.SystemPrompt == "main");
        var output = await Collect(wrapped.StreamChatAsync(Request(), default));
        Assert.Equal(0, observed);
        Assert.Equal("system", fake.Requests.Single().SystemPrompt);
        Assert.Equal("ordinary compaction summary", output.OfType<TextDeltaEvent>().Single().Delta);
    }

    [Fact]
    public void Invalid_schemas_are_rejected_before_any_provider_is_created()
    {
        Assert.Throws<CliError>(() => new CliStructuredOutput("not JSON"));
        Assert.Throws<CliError>(() => new CliStructuredOutput("{\"type\":\"not-a-type\"}"));
    }

    [Fact]
    public async Task Pricing_uses_reported_token_buckets_and_accumulates_retries()
    {
        var model = new ModelInfo("fake", "test", "Test", 200_000, 3, 15);
        var ledger = new CliUsageLedger((_, _) => model, null);
        var usage = new Usage(100, 50, 10, 20);
        var fake = new ScriptedProvider(Answer("one", usage), Answer("two", usage));
        var metered = new CliMeteredProvider(fake, ledger);
        await Collect(metered.StreamChatAsync(Request(), default));
        await Collect(metered.StreamChatAsync(Request() with { CacheTtl = "1h" }, default));
        var actual = ledger.Snapshot();
        Assert.Equal(2, actual.Calls);
        Assert.Equal(usage.Add(usage), actual.Usage);
        Assert.Equal(.002301m, actual.CostUsd); // 0.001128 + 0.001173, including 5m/1h cache writes.
    }

    [Fact]
    public async Task Unknown_pricing_is_null_and_a_dollar_budget_refuses_before_spending()
    {
        var unknown = new ModelInfo("fake", "test", "Test", 100_000);
        var freeRun = new CliUsageLedger((_, _) => unknown, null);
        await Collect(new CliMeteredProvider(new ScriptedProvider(Answer("answer")), freeRun).StreamChatAsync(Request(), default));
        Assert.Null(freeRun.Snapshot().CostUsd);
        var budget = new CliUsageLedger((_, _) => unknown, 1m);
        var fake = new ScriptedProvider(Answer("never"));
        await Assert.ThrowsAsync<CliError>(() => Collect(new CliMeteredProvider(fake, budget).StreamChatAsync(Request(), default)));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Reaching_budget_records_the_call_and_prevents_another_request()
    {
        var model = new ModelInfo("fake", "test", "Test", 100_000, 1, 1);
        var ledger = new CliUsageLedger((_, _) => model, .0009m);
        var fake = new ScriptedProvider(Answer("last", new Usage(1000, 1)), Answer("must not run"));
        var metered = new CliMeteredProvider(fake, ledger);
        await Assert.ThrowsAsync<CliError>(() => Collect(metered.StreamChatAsync(Request(), default)));
        await Assert.ThrowsAsync<CliError>(() => Collect(metered.StreamChatAsync(Request(), default)));
        Assert.True(ledger.BudgetReached);
        Assert.Single(fake.Requests);
        Assert.Equal(.001001m, ledger.Snapshot().CostUsd);
        Assert.Equal(1000, ledger.Snapshot().Usage.InputTokens);
    }

    [Fact]
    public async Task Failed_structured_attempts_are_in_the_shared_cost_ledger()
    {
        var model = new ModelInfo("fake", "test", "Test", 100_000, 1, 1);
        var ledger = new CliUsageLedger((_, _) => model, null);
        var fake = new ScriptedProvider(Answer("bad"), Answer("{\"answer\":1}"));
        await Collect(new CliStructuredOutput(AnswerSchema).Wrap(new CliMeteredProvider(fake, ledger)).StreamChatAsync(Request(), default));
        Assert.Equal(2, ledger.Snapshot().Calls);
        Assert.Equal(20, ledger.Snapshot().Usage.InputTokens);
    }

    [Fact]
    public void Partial_events_preserve_json_fragments_citations_and_close_the_message()
    {
        var writer = new StringWriter();
        var partial = new PartialJsonMessage(new StreamJson(writer, "session"));
        partial.Begin(Request());
        partial.Observe(new ToolCallStartedEvent(0, "call", "Read"));
        partial.Observe(new ToolCallArgumentsDeltaEvent(0, "{\"file_path\":"));
        partial.Observe(new ToolCallArgumentsDeltaEvent(0, "\"file\"}"));
        partial.Observe(new TextDeltaEvent("Claim", 1));
        partial.Observe(new CitationDeltaEvent(1, new TextCitation("{\"type\":\"web_search_result_location\",\"url\":\"https://example.test/\"}", 5)));
        partial.Observe(new ResponseCompletedEvent(true, new Usage(1, 2), "tool_use"));
        var events = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!["event"]!).ToList();
        Assert.Equal("message_start", events[0]["type"]!.GetValue<string>());
        Assert.Equal("message_stop", events[^1]["type"]!.GetValue<string>());
        Assert.Equal(2, events.Count(e => e["type"]!.GetValue<string>() == "content_block_stop"));
        Assert.Contains(events, e => e["delta"]?["partial_json"]?.GetValue<string>() == "{\"file_path\":");
        Assert.Contains(events, e => e["delta"]?["type"]?.GetValue<string>() == "citations_delta");
    }

    [Fact]
    public async Task Hook_lifecycle_is_observational_and_clones_keep_the_observer()
    {
        var observed = new List<HookLifecycleEvent>();
        var hooks = new HookRunner([], Path.GetTempPath(), [new FunctionHook(HookEvent.PostToolUse,
            (_, _) => Task.FromResult(new FunctionHookResult("extra")))]) { ExecutionObserved = observed.Add };
        Assert.Equal("extra", await hooks.WithExtra([]).AfterToolAsync(new WriteStub(), [], ToolResult.Success("ok"), default));
        Assert.Equal(["started", "response"], observed.Select(x => x.Phase));
        Assert.Equal(observed[0].HookId, observed[1].HookId);
        hooks.ExecutionObserved = _ => throw new InvalidOperationException("observer failed");
        Assert.Equal("extra", await hooks.AfterToolAsync(new WriteStub(), [], ToolResult.Success("ok"), default));
    }

    [Fact]
    public async Task Headless_wakeups_outlive_their_turn_and_stop_cancels_them()
    {
        using var pending = new CliPendingWork(default);
        pending.CompleteInput();
        pending.Schedule(TimeSpan.FromMilliseconds(15), "wake", false);
        var message = await pending.NextAsync(() => false, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("wake", message!.Message.GetText());
        Assert.True(message.Message.IsMeta);
        pending.Schedule(TimeSpan.FromSeconds(30), "cancel", false);
        Assert.Equal(1, pending.StopWakeups());
        Assert.Null(await pending.NextAsync(() => false, default).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Sdk_hosted_mcp_initialization_can_exchange_control_responses_without_deadlock()
    {
        var reader = new DuplexReader();
        var writer = new RecordingWriter();
        await using var manager = new McpManager();
        var sdk = new CliSdkSession(manager, "s", Path.GetTempPath(), "transcript.json", ["fixture"]);
        using var input = new StreamJsonInput(reader, new StreamJson(writer, "s"), async (request, token) =>
        { await sdk.InitializeAsync(request, token); return new JsonObject { ["ready"] = true }; });
        sdk.Attach(input);
        using var hostLifetime = new CancellationTokenSource();
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var methods = new ConcurrentQueue<string>();
        var host = FakeHostAsync(writer, reader, wire =>
        {
            if (wire["type"]?.GetValue<string>() == "control_response")
            { initialized.TrySetResult(); return null; }
            var rpc = wire["request"]!["message"]!;
            var method = rpc["method"]!.GetValue<string>();
            methods.Enqueue(method);
            var result = method switch
            {
                "initialize" => JsonNode.Parse("""{"protocolVersion":"2025-06-18","capabilities":{"tools":{},"resources":{},"prompts":{}},"instructions":"Use fixture tools."}""")!,
                "tools/list" => JsonNode.Parse("""{"tools":[{"name":"echo","description":"Echo","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]},"_meta":{"anthropic/searchHint":"fixture search","anthropic/alwaysLoad":true}}]}""")!,
                "resources/list" => JsonNode.Parse("""{"resources":[{"uri":"memory://fixture","name":"Fixture","mimeType":"text/plain"}]}""")!,
                "prompts/list" => JsonNode.Parse("""{"prompts":[{"name":"greeting","arguments":[]}]}""")!,
                "resources/read" => JsonNode.Parse("""{"contents":[{"uri":"memory://fixture","text":"resource text"}]}""")!,
                "prompts/get" => JsonNode.Parse("""{"messages":[{"role":"user","content":{"type":"text","text":"prompt text"}}]}""")!,
                "tools/call" => JsonNode.Parse("""{"content":[{"type":"text","text":"echoed"},{"type":"image","mimeType":"image/png","data":"aGk="}],"structuredContent":{"answer":42}}""")!,
                _ => new JsonObject(),
            };
            return new JsonObject { ["mcp_response"] = new JsonObject
            { ["jsonrpc"] = "2.0", ["id"] = rpc["id"]?.DeepClone(), ["result"] = result } };
        }, hostLifetime.Token);
        var pump = input.PumpAsync(default);
        reader.Send("""{"type":"control_request","request_id":"init","request":{"subtype":"initialize"}}""");
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sdk.Ready;
        Assert.Equal(1, manager.ConnectedToolCounts["fixture"]);
        Assert.Equal("Use fixture tools.", manager.ServerInstructions.Single(x => x.Name == "fixture").Instructions);
        var tool = Assert.Single(manager.Tools);
        Assert.Equal("fixture search", Assert.IsAssignableFrom<ISearchHintTool>(tool).SearchHint);
        var result = await tool.ExecuteAsync(new JsonObject { ["text"] = "hello" },
            new ToolExecutionContext { WorkingDirectory = Path.GetTempPath(), IsNonInteractiveSession = true }, default);
        Assert.Contains("echoed", result.Content);
        Assert.Contains("\"answer\":42", result.Content);
        Assert.Equal("aGk=", Assert.Single(result.Images!).Base64Data);
        Assert.Equal("resource text", await manager.ReadResourceAsync("fixture", "memory://fixture", default));
        Assert.Equal("prompt text", await manager.GetPromptAsync("fixture", "greeting", [], default));
        await manager.RefreshAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), null, default);
        Assert.Single(manager.Tools); // ordinary refresh keeps SDK-owned registrations
        Assert.IsType<JsonArray>(sdk.ServerStatus()["mcpServers"]![0]!["tools"]);
        await sdk.ToggleAsync("fixture", false, default);
        Assert.Equal("disabled", sdk.ServerStatus()["mcpServers"]![0]!["status"]!.GetValue<string>());
        await sdk.ToggleAsync("fixture", true, default);
        Assert.Equal("echo", sdk.ServerStatus()["mcpServers"]![0]!["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Contains("notifications/initialized", methods);
        reader.Complete();
        await pump;
        hostLifetime.Cancel();
        await host;
    }

    [Fact]
    public async Task Sdk_callbacks_receive_real_tool_identity_and_enforce_updates_context_and_stop_verdicts()
    {
        var reader = new DuplexReader();
        var writer = new RecordingWriter();
        await using var manager = new McpManager();
        var sdk = new CliSdkSession(manager, "session", Path.GetTempPath(), "transcript.json", []);
        using var input = new StreamJsonInput(reader, new StreamJson(writer, "session"), (_, _) => Task.FromResult(new JsonObject()));
        sdk.Attach(input);
        await sdk.InitializeAsync(JsonNode.Parse("""
            {"hooks":{"PreToolUse":[{"matcher":"Write","hookCallbackIds":["pre"]}],
                      "PostToolUse":[{"matcher":"Write","hookCallbackIds":["post"]}],
                      "Stop":[{"hookCallbackIds":["stop"]}]}}
            """)!.AsObject(), default);
        using var hostLifetime = new CancellationTokenSource();
        var requests = new ConcurrentQueue<JsonObject>();
        var host = FakeHostAsync(writer, reader, wire =>
        {
            var request = wire["request"]!.AsObject();
            requests.Enqueue((JsonObject)request.DeepClone());
            return request["callback_id"]!.GetValue<string>() switch
            {
                "pre" => JsonNode.Parse("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","updatedInput":{"file_path":"updated.txt","content":"updated"}}}""")!.AsObject(),
                "post" => JsonNode.Parse("""{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"validated by host"}}""")!.AsObject(),
                _ => new JsonObject { ["decision"] = "block", ["reason"] = "finish the requested check" },
            };
        }, hostLifetime.Token);
        var pump = input.PumpAsync(default);
        var hooks = sdk.ApplyHooks(null);
        var args = new JsonObject { ["file_path"] = "original.txt", ["content"] = "old" };
        using (HookRunner.EnterToolCall("tool-actual"))
        {
            var decision = await hooks.BeforeToolAsync(new WriteStub(), args, default);
            Assert.True(decision.Allowed);
            Assert.Equal("allow", decision.PermissionBehavior);
            Assert.Equal("updated.txt", args["file_path"]!.GetValue<string>());
            Assert.Contains("validated by host", await hooks.AfterToolAsync(new WriteStub(), args, ToolResult.Success("ok"), default));
        }
        var stop = await hooks.RunStopHooksAsync(HookEvent.Stop, false, default);
        Assert.False(stop.Decision.Allowed);
        Assert.Contains("finish the requested check", stop.Decision.BlockReason);
        var pre = requests.First();
        Assert.Equal("tool-actual", pre["tool_use_id"]!.GetValue<string>());
        Assert.Equal("Write", pre["input"]!["tool_name"]!.GetValue<string>());
        Assert.Equal("session", pre["input"]!["session_id"]!.GetValue<string>());
        Assert.Equal("original.txt", pre["input"]!["tool_input"]!["file_path"]!.GetValue<string>());
        reader.Complete();
        await pump;
        hostLifetime.Cancel();
        await host;
    }

    [Fact]
    public void Tool_result_stream_retains_images_from_pdf_and_sdk_tools()
    {
        var writer = new StringWriter();
        new StreamJson(writer, "s").EmitToolResult("tool", "page", false, images: [new ImageBlock("image/png", "aGk=")]);
        var result = JsonNode.Parse(writer.ToString())!["message"]!["content"]![0]!["content"]!;
        Assert.Equal("page", result[0]!["text"]!.GetValue<string>());
        Assert.Equal("aGk=", result[1]!["source"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pre_tool_callback_is_applied_before_permission_and_cannot_override_a_deny_rule()
    {
        var path = Path.Combine(Path.GetTempPath(), "sdk-rewritten.txt");
        var hooks = new HookRunner([], Path.GetTempPath()).WithSdkCallbacks(
            [new HookDefinition(HookEvent.PreToolUse, null, "pre", 30, HookKind.Callback, CallbackId: "pre")],
            (_, _, _, _) => Task.FromResult<JsonObject?>(new JsonObject { ["hookSpecificOutput"] = new JsonObject
            { ["permissionDecision"] = "allow", ["updatedInput"] = new JsonObject { ["file_path"] = path, ["content"] = "new" } } }));
        var fake = new ScriptedProvider([new ToolCallStartedEvent(0, "call", "Write"),
            new ToolCallArgumentsDeltaEvent(0, "{\"file_path\":\"original.txt\",\"content\":\"old\"}"),
            new ResponseCompletedEvent(true, new Usage(10, 1), "tool_use")], Answer("done"));
        var gate = new UiPermissionGate { WorkingDirectory = Path.GetTempPath(), Mode = PermissionMode.Manual,
            IgnoreSettingsFiles = true };
        gate.AddSessionRuleLines(["deny Write"]);
        var context = new AgentTurnContext { Provider = fake, ModelId = "test", SystemPrompt = "system",
            Messages = [ChatMessage.FromUserText("task")], Tools = new ToolRegistry([new WriteStub()]),
            PermissionGate = gate, Hooks = hooks, MaxIterations = 3,
            ToolContext = new ToolExecutionContext { WorkingDirectory = Path.GetTempPath() } };
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentOrchestrator().RunTurnAsync(context, default)) events.Add(evt);
        Assert.Contains(events, evt => evt is ToolExecutionDenied { ToolName: "Write" });
        Assert.DoesNotContain(events, evt => evt is ToolExecutionStarted);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Continue_false_halts_instead_of_repeating_a_stop_hook()
    {
        var hooks = new HookRunner([], Path.GetTempPath()).WithSdkCallbacks(
            [new HookDefinition(HookEvent.Stop, null, "stop", 30, HookKind.Callback, CallbackId: "stop")],
            (_, _, _, _) => Task.FromResult<JsonObject?>(new JsonObject { ["continue"] = false, ["stopReason"] = "stop now" }));
        var outcome = await hooks.RunStopHooksAsync(HookEvent.Stop, false, default);
        Assert.True(outcome.Decision.StopImmediately);
        Assert.True(outcome.Decision.Allowed);
        Assert.Equal("stop now", outcome.Decision.BlockReason);
    }

    [Fact]
    public async Task Missing_usage_does_not_become_a_zero_charge_or_allow_budget_retry()
    {
        var model = new ModelInfo("fake", "test", "Test", 100000, 1, 1);
        var ledger = new CliUsageLedger((_, _) => model, 1m);
        var fake = new ScriptedProvider([new TextDeltaEvent("lost stream")], Answer("must not run"));
        var metered = new CliMeteredProvider(fake, ledger);
        await Collect(metered.StreamChatAsync(Request(), default));
        Assert.Null(ledger.Snapshot().CostUsd);
        Assert.Equal(1, ledger.Snapshot().UnreportedCalls);
        await Assert.ThrowsAsync<CliError>(() => Collect(metered.StreamChatAsync(Request(), default)));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public void Durable_cron_does_not_dispatch_into_an_unrelated_workspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-cron-scope-" + Guid.NewGuid().ToString("N"));
        var store = new JarvisCode.Core.Routines.RoutineStore(Path.Combine(root, "routines.json"));
        var when = new DateTime(2026, 9, 8, 12, 0, 0);
        store.Save([new JarvisCode.Core.Routines.Routine { Id = "abc12345", Name = "scope", Instruction = "task",
            WorkingDirectory = Path.Combine(root, "project-a"), CronSessionId = "session-a", CronExpression = "* * * * *",
            CreatedAt = when.AddMinutes(-5), OneShot = true }]);
        try
        {
            var messages = new List<string>();
            using var wrong = new CliSessionCrons(store, "session-b", () => true, messages.Add, automaticTick: false,
                workingDirectory: Path.Combine(root, "project-b"));
            wrong.Tick(when);
            Assert.Empty(messages);
            Assert.False(wrong.HasPendingWork);
            using var right = new CliSessionCrons(store, "resumed-session", () => true, messages.Add, automaticTick: false,
                workingDirectory: Path.Combine(root, "project-a"));
            right.Tick(when);
            Assert.Equal(["task"], messages);
        }
        finally
        {
            var target = Path.GetFullPath(root);
            var allowedPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "jarvis-cron-scope-");
            Assert.StartsWith(allowedPrefix, target, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(target, recursive: true);
        }
    }

    private static async Task FakeHostAsync(RecordingWriter output, DuplexReader input,
        Func<JsonObject, JsonObject?> answer, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var message = JsonNode.Parse(await output.NextAsync(cancellationToken))!.AsObject();
                if (answer(message) is { } response) Respond(input, message, response);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task<List<ProviderEvent>> Collect(IAsyncEnumerable<ProviderEvent> events)
    {
        var result = new List<ProviderEvent>();
        await foreach (var evt in events) result.Add(evt);
        return result;
    }

    private static void Respond(DuplexReader input, JsonNode request, JsonObject answer) => input.Send(new JsonObject
    { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success",
        ["request_id"] = request["request_id"]!.GetValue<string>(), ["response"] = answer } }.ToJsonString());

    private sealed class ScriptedProvider(params ProviderEvent[][] responses) : ILlmProvider, IProviderCapabilities
    {
        public string Id => "fake";
        public string DisplayName => "Fixture";
        public ProviderCapabilities Capabilities { get; init; } = new();
        public List<LlmRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var response = responses[Requests.Count];
            Requests.Add(request);
            foreach (var evt in response) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); yield return evt; }
        }
    }

    private sealed class DuplexReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public void Send(string line) => _lines.Writer.TryWrite(line);
        public void Complete() => _lines.Writer.TryComplete();
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            return await _lines.Reader.WaitToReadAsync(cancellationToken) ? await _lines.Reader.ReadAsync(cancellationToken) : null;
        }
    }

    private sealed class RecordingWriter : TextWriter
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => _lines.Writer.TryWrite(value ?? "");
        public async Task<string> NextAsync() => await _lines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async Task<string> NextAsync(CancellationToken cancellationToken) => await _lines.Reader.ReadAsync(cancellationToken);
    }

    private sealed class WriteStub : ITool
    {
        public string Name => "Write";
        public string Description => "Test write; never executed.";
        public bool IsReadOnly => false;
        public JsonObject InputSchema => JsonNode.Parse("""
            {"type":"object","properties":{"file_path":{"type":"string"},"content":{"type":"string"}},"required":["file_path","content"],"additionalProperties":false}
            """)!.AsObject();
        public string DescribeCall(JsonObject arguments) => "Write " + arguments["file_path"];
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Permission fixtures must never execute a tool.");
    }
}
