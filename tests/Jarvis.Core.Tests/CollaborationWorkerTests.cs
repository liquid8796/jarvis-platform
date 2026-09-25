using System.Diagnostics;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.ToolPrograms;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class CollaborationWorkerTests
{
    private static readonly AgentSessionIdentity Identity = new("owner", "device", AgentSessionRules.NewSessionId());

    [Fact]
    public void Core_host_surface_contains_collaboration_worker_tools()
    {
        var descriptors = AgentCoreHostTools.Descriptors.ToDictionary(item => item.Id, StringComparer.Ordinal);
        Assert.Equal("worker_spawn", descriptors[CollaborationWorkerHost.SpawnId].Name);
        Assert.Equal("worker_send", descriptors[CollaborationWorkerHost.SendId].Name);
        Assert.Equal("worker_wait", descriptors[CollaborationWorkerHost.WaitId].Name);
        Assert.Equal("worker_list", descriptors[CollaborationWorkerHost.ListId].Name);
        Assert.Equal("worker_stop", descriptors[CollaborationWorkerHost.StopId].Name);
    }

    [Fact]
    public async Task Worker_receives_messages_and_streams_terminal_result()
    {
        var registry = new DynamicToolRegistry([]);
        await using var host = new CollaborationWorkerHost((_, _, _, _) => throw new InvalidOperationException(),
            () => registry.Snapshot);
        var tools = host.CreateTools().ToDictionary(item => item.Descriptor.Id, StringComparer.Ordinal);
        var context = Context(Identity);

        var started = await tools[CollaborationWorkerHost.SpawnId].ExecuteAsync(WireJson.Element(new
        {
            label = "mailbox",
            code = "const message = await receive(5000); text(message.message); return { sequence: message.sequence };",
            yield_time_ms = 0
        }), context, default);
        var workerId = Property(started, "worker_id").GetString()!;

        await tools[CollaborationWorkerHost.SendId].ExecuteAsync(WireJson.Element(new
        {
            worker_id = workerId,
            message = "hello worker"
        }), context, default);
        var completed = await WaitForTerminal(tools, context, workerId);

        using var body = JsonDocument.Parse(completed.Text);
        Assert.Equal("SUCCEEDED", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("hello worker", body.RootElement.GetProperty("output").GetString());
        Assert.Contains("sequence", body.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Workers_execute_nested_tools_concurrently_and_forward_images()
    {
        var echo = new EchoTool();
        var registry = new DynamicToolRegistry([echo]);
        var running = 0;
        var maxRunning = 0;
        await using var host = new CollaborationWorkerHost(async (_, args, _, ct) =>
        {
            var current = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, current);
            try
            {
                await Task.Delay(100, ct);
                return new ToolReply(WireJson.Element(new { value = args.GetProperty("value").GetInt32() }).GetRawText(),
                    Images: [new WireImage("image/png", "aGVsbG8=")]);
            }
            finally { Interlocked.Decrement(ref running); }
        }, () => registry.Snapshot);
        var tools = host.CreateTools().ToDictionary(item => item.Descriptor.Id, StringComparer.Ordinal);
        var context = Context(Identity);

        var first = await Spawn(tools, context, "first", "return await tools.echo({ value: 2 });");
        var second = await Spawn(tools, context, "second", "return await tools.echo({ value: 5 });");
        var one = await WaitForTerminal(tools, context, first);
        var two = await WaitForTerminal(tools, context, second);

        Assert.Equal(2, maxRunning);
        Assert.Single(one.Images!);
        Assert.Single(two.Images!);
        Assert.Contains("2", Property(one, "result").GetString());
        Assert.Contains("5", Property(two, "result").GetString());
    }

    [Fact]
    public async Task Worker_cannot_swallow_a_denied_nested_tool()
    {
        var registry = new DynamicToolRegistry([new EchoTool()]);
        await using var host = new CollaborationWorkerHost((_, _, _, _) =>
            throw new UnauthorizedAccessException("denied locally"), () => registry.Snapshot);
        var tools = host.CreateTools().ToDictionary(item => item.Descriptor.Id, StringComparer.Ordinal);
        var context = Context(Identity);

        var workerId = await Spawn(tools, context, "denied",
            "try { await tools.echo({ value: 1 }); } catch (_) { } return 'ignored';");
        var failed = await WaitForTerminal(tools, context, workerId);

        Assert.True(failed.IsError);
        Assert.Equal("FAILED", Property(failed, "status").GetString());
        Assert.Contains("denied locally", Property(failed, "error").GetString());
    }

    [Fact]
    public async Task Workers_are_session_isolated_and_stop_is_targeted()
    {
        var registry = new DynamicToolRegistry([]);
        await using var host = new CollaborationWorkerHost((_, _, _, _) => throw new InvalidOperationException(),
            () => registry.Snapshot);
        var tools = host.CreateTools().ToDictionary(item => item.Descriptor.Id, StringComparer.Ordinal);
        var firstContext = Context(Identity);
        var otherContext = Context(new("owner", "device", AgentSessionRules.NewSessionId()));
        var workerId = await Spawn(tools, firstContext, "waiting", "await receive(300000); return 'done';");

        var otherList = await tools[CollaborationWorkerHost.ListId].ExecuteAsync(WireJson.Element(new { }), otherContext, default);
        Assert.Equal(0, Property(otherList, "running").GetInt32());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => tools[CollaborationWorkerHost.StopId].ExecuteAsync(
            WireJson.Element(new { worker_id = workerId }), otherContext, default));

        await tools[CollaborationWorkerHost.StopId].ExecuteAsync(WireJson.Element(new { worker_id = workerId }), firstContext, default);
        var stopped = await WaitForTerminal(tools, firstContext, workerId);
        Assert.Equal("CANCELLED", Property(stopped, "status").GetString());
    }

    [Fact]
    public async Task Worker_requires_explicit_session_and_enforces_active_limit()
    {
        var registry = new DynamicToolRegistry([]);
        var limits = new CollaborationWorkerLimits(MaxWorkersPerSession: 1, MaxRetainedWorkersPerSession: 2);
        await using var host = new CollaborationWorkerHost((_, _, _, _) => throw new InvalidOperationException(),
            () => registry.Snapshot, limits);
        var spawn = host.CreateTools().Single(item => item.Descriptor.Id == CollaborationWorkerHost.SpawnId);
        await Assert.ThrowsAsync<AgentRequestException>(() => spawn.ExecuteAsync(WireJson.Element(new
        {
            label = "no session", code = "return 1;"
        }), new(Path.GetTempPath(), "call", AgentSessionRules.NewEphemeralExecutionId()), default));

        var context = Context(Identity);
        await spawn.ExecuteAsync(WireJson.Element(new
        {
            label = "one", code = "await receive(300000);", yield_time_ms = 0
        }), context, default);
        var error = await Assert.ThrowsAsync<AgentRequestException>(() => spawn.ExecuteAsync(WireJson.Element(new
        {
            label = "two", code = "return 2;", yield_time_ms = 0
        }), context, default));
        Assert.Equal("WORKER_LIMIT", error.Code);
    }

    private static async Task<string> Spawn(IReadOnlyDictionary<string, IAgentTool> tools,
        AgentExecutionContext context, string label, string code)
    {
        var reply = await tools[CollaborationWorkerHost.SpawnId].ExecuteAsync(WireJson.Element(new
        {
            label, code, yield_time_ms = 0
        }), context, default);
        return Property(reply, "worker_id").GetString()!;
    }

    private static async Task<ToolReply> WaitForTerminal(IReadOnlyDictionary<string, IAgentTool> tools,
        AgentExecutionContext context, string workerId)
    {
        var stopwatch = Stopwatch.StartNew();
        var output = new System.Text.StringBuilder();
        var images = new List<WireImage>();
        WidgetArtifact? widget = null;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var reply = await tools[CollaborationWorkerHost.WaitId].ExecuteAsync(WireJson.Element(new
            {
                worker_id = workerId, yield_time_ms = 1_000, max_output_tokens = 10_000
            }), context, default);
            using var body = JsonDocument.Parse(reply.Text);
            var delta = body.RootElement.GetProperty("output").GetString();
            if (!string.IsNullOrEmpty(delta))
            {
                if (output.Length > 0) output.AppendLine();
                output.Append(delta);
            }
            if (reply.Images is not null) images.AddRange(reply.Images);
            widget ??= reply.Widget;
            var status = body.RootElement.GetProperty("status").GetString();
            if (status is "SUCCEEDED" or "FAILED" or "CANCELLED")
            {
                var merged = WireJson.Element(new
                {
                    worker_id = body.RootElement.GetProperty("worker_id").GetString(),
                    label = body.RootElement.GetProperty("label").GetString(),
                    status,
                    output = output.ToString(),
                    result = body.RootElement.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null
                        ? result.GetString() : null,
                    error = body.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
                        ? error.GetString() : null
                });
                return new ToolReply(merged.GetRawText(), reply.IsError, images, widget);
            }
        }
        throw new TimeoutException("Worker did not reach a terminal state.");
    }

    private static JsonElement Property(ToolReply reply, string name)
    {
        using var body = JsonDocument.Parse(reply.Text);
        return body.RootElement.GetProperty(name).Clone();
    }

    private static AgentExecutionContext Context(AgentSessionIdentity identity) =>
        new(Path.GetTempPath(), "worker-test", identity.SessionId)
        {
            OwnerId = identity.OwnerId,
            AgentDeviceId = identity.DeviceId,
            SessionCancellation = CancellationToken.None
        };

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

        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(new ToolReply(arguments.GetRawText()));
    }
}
