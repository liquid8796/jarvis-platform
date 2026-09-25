using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.ToolPrograms;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class SessionToolReplTests
{
    private static readonly AgentSessionIdentity Identity = new("owner", "device", AgentSessionRules.NewSessionId());

    [Fact]
    public void Core_host_surface_contains_session_repl_tools_with_codex_compatible_names()
    {
        var descriptors = AgentCoreHostTools.Descriptors.ToDictionary(descriptor => descriptor.Id, StringComparer.Ordinal);
        Assert.Equal("exec", descriptors[SessionToolReplHost.ExecId].Name);
        Assert.Equal("wait", descriptors[SessionToolReplHost.WaitId].Name);
        Assert.Equal("sleep", descriptors[SessionToolReplHost.SleepId].Name);
        Assert.Equal("repl_reset", descriptors[SessionToolReplHost.ResetId].Name);
    }

    [Fact]
    public async Task Repl_persists_global_state_and_isolates_sessions()
    {
        var registry = new DynamicToolRegistry([]);
        await using var host = new SessionToolReplHost((_, _, _, _) => throw new InvalidOperationException(),
            () => registry.Snapshot);
        var exec = host.CreateTools().Single(tool => tool.Descriptor.Id == SessionToolReplHost.ExecId);

        var first = await exec.ExecuteAsync(Args("globalThis.count = (globalThis.count ?? 0) + 1; return globalThis.count;"),
            Context(Identity), default);
        var second = await exec.ExecuteAsync(Args("globalThis.count = (globalThis.count ?? 0) + 1; return globalThis.count;"),
            Context(Identity), default);
        var other = await exec.ExecuteAsync(Args("return globalThis.count ?? null;"),
            Context(new("owner", "device", AgentSessionRules.NewSessionId())), default);

        Assert.Equal("1", Result(first));
        Assert.Equal("2", Result(second));
        Assert.Null(Result(other));
    }

    [Fact]
    public async Task Repl_builds_dynamic_tools_proxy_and_parallel_nested_calls_reenter_invoker()
    {
        var echo = new EchoTool();
        var registry = new DynamicToolRegistry([echo]);
        var running = 0;
        var maxRunning = 0;
        await using var host = new SessionToolReplHost(async (toolId, arguments, _, cancellationToken) =>
        {
            Assert.Equal("test.echo", toolId);
            var current = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, current);
            try
            {
                await Task.Delay(50, cancellationToken);
                return new ToolReply(WireJson.Element(new { value = arguments.GetProperty("value").GetInt32() }).GetRawText());
            }
            finally { Interlocked.Decrement(ref running); }
        }, () => registry.Snapshot);
        var exec = host.CreateTools().Single(tool => tool.Descriptor.Id == SessionToolReplHost.ExecId);

        var reply = await exec.ExecuteAsync(Args("""
            const values = await Promise.all([tools.echo({ value: 2 }), tools.echo({ value: 5 })]);
            text(values[0].value + values[1].value);
            return { count: values.length };
            """), Context(Identity), default);

        using var body = JsonDocument.Parse(reply.Text);
        Assert.Equal("SUCCEEDED", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("7", body.RootElement.GetProperty("output").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("tool_calls").GetInt32());
        Assert.Equal(2, maxRunning);
    }

    [Fact]
    public async Task Repl_cannot_swallow_denied_nested_tool_and_forwards_images()
    {
        var echo = new EchoTool();
        var registry = new DynamicToolRegistry([echo]);
        var denied = true;
        await using var host = new SessionToolReplHost((_, _, _, _) =>
        {
            if (denied) throw new UnauthorizedAccessException("denied locally");
            return Task.FromResult(new ToolReply("ok", Images: [new WireImage("image/png", "aGVsbG8=")]));
        }, () => registry.Snapshot);
        var exec = host.CreateTools().Single(tool => tool.Descriptor.Id == SessionToolReplHost.ExecId);

        var failed = await exec.ExecuteAsync(Args("try { await tools.echo({ value: 1 }); } catch (_) { } return 'ignored';"),
            Context(Identity), default);
        using (var body = JsonDocument.Parse(failed.Text))
        {
            Assert.Equal("FAILED", body.RootElement.GetProperty("status").GetString());
            Assert.Contains("denied locally", body.RootElement.GetProperty("error").GetString());
        }

        denied = false;
        var imageReply = await exec.ExecuteAsync(Args("return await tools.echo({ value: 1 });"), Context(Identity), default);
        Assert.Single(imageReply.Images!);
        Assert.Equal("image/png", imageReply.Images![0].MimeType);
    }

    [Fact]
    public async Task Wait_streams_long_cell_and_reset_discards_state()
    {
        var registry = new DynamicToolRegistry([]);
        await using var host = new SessionToolReplHost((_, _, _, _) => throw new InvalidOperationException(),
            () => registry.Snapshot);
        var tools = host.CreateTools().ToDictionary(tool => tool.Descriptor.Id, StringComparer.Ordinal);
        var context = Context(Identity);

        var started = await tools[SessionToolReplHost.ExecId].ExecuteAsync(
            WireJson.Element(new { code = "globalThis.saved = 9; await sleep(80); text('done'); return globalThis.saved;", yield_time_ms = 0 }),
            context, default);
        string cellId;
        using (var body = JsonDocument.Parse(started.Text))
        {
            Assert.Contains(body.RootElement.GetProperty("status").GetString(), new[] { "QUEUED", "RUNNING" });
            cellId = body.RootElement.GetProperty("cell_id").GetString()!;
        }

        var completed = await tools[SessionToolReplHost.WaitId].ExecuteAsync(
            WireJson.Element(new { cell_id = cellId, yield_time_ms = 2_000 }), context, default);
        using (var body = JsonDocument.Parse(completed.Text))
        {
            Assert.Equal("SUCCEEDED", body.RootElement.GetProperty("status").GetString());
            Assert.Equal("done", body.RootElement.GetProperty("output").GetString());
            Assert.Equal("9", body.RootElement.GetProperty("result").GetString());
        }

        await tools[SessionToolReplHost.ResetId].ExecuteAsync(WireJson.Element(new { }), context, default);
        var afterReset = await tools[SessionToolReplHost.ExecId].ExecuteAsync(Args("return globalThis.saved ?? null;"), context, default);
        Assert.Null(Result(afterReset));
    }

    [Fact]
    public async Task Repl_requires_an_explicit_application_session()
    {
        var registry = new DynamicToolRegistry([]);
        await using var host = new SessionToolReplHost((_, _, _, _) => throw new InvalidOperationException(),
            () => registry.Snapshot);
        var exec = host.CreateTools().Single(tool => tool.Descriptor.Id == SessionToolReplHost.ExecId);

        var error = await Assert.ThrowsAsync<AgentRequestException>(() =>
            exec.ExecuteAsync(Args("return 1;"), new(Path.GetTempPath(), "call", AgentSessionRules.NewEphemeralExecutionId()), default));
        Assert.Equal("SESSION_REQUIRED", error.Code);
    }

    private static JsonElement Args(string code) => WireJson.Element(new { code, yield_time_ms = 2_000, max_output_tokens = 10_000 });

    private static AgentExecutionContext Context(AgentSessionIdentity identity) =>
        new(Path.GetTempPath(), "test-call", identity.SessionId)
        {
            OwnerId = identity.OwnerId,
            AgentDeviceId = identity.DeviceId,
            SessionCancellation = CancellationToken.None
        };

    private static string? Result(ToolReply reply)
    {
        using var body = JsonDocument.Parse(reply.Text);
        if (!body.RootElement.TryGetProperty("result", out var result)) return null;
        return result.ValueKind == JsonValueKind.Null ? null : result.GetString();
    }

    private sealed class EchoTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("test.echo", "echo", "test", "Echo a value.",
            WireJson.Element(new
            {
                type = "object",
                properties = new { value = new { type = "integer" } },
                required = new[] { "value" },
                additionalProperties = false
            }), ReadOnly: true);

        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply(arguments.GetRawText()));
    }
}
