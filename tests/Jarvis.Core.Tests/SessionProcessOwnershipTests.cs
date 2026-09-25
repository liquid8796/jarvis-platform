using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class SessionProcessOwnershipTests
{
    [Fact]
    public async Task Another_session_cannot_poll_write_or_cancel_an_exec_session()
    {
        using var tools = new ProcessToolSet();
        var owner = Context();
        var foreign = Context();
        var sessionId = await Start(tools, owner);

        foreach (var chars in new[] { "", "foreign input\n", "\u0003" })
        {
            var reply = await Stdin(tools, foreign, sessionId, chars);
            Assert.True(reply.IsError);
            Assert.Contains("owned", reply.Text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False((await Stdin(tools, owner, sessionId, "", 0)).IsError);
        Assert.True((await Stdin(tools, owner with { OwnerId = "different-owner" }, sessionId, "", 0)).IsError);
        await Stdin(tools, owner, sessionId, "\u0003", 0);
    }

    [Fact]
    public async Task Sessionless_exec_survives_ephemeral_call_ids_but_remains_owner_device_scoped()
    {
        using var tools = new ProcessToolSet();
        var first = SessionlessContext();
        var second = SessionlessContext();
        var sessionId = await Start(tools, first);
        Assert.False((await Stdin(tools, second, sessionId, "", 0)).IsError);
        Assert.True((await Stdin(tools, Context(), sessionId, "", 0)).IsError);
        Assert.True((await Stdin(tools, second with { OwnerId = "different-owner" }, sessionId, "", 0)).IsError);
        Assert.True((await Stdin(tools, second with { AgentDeviceId = "different-device" }, sessionId, "", 0)).IsError);
        await Stdin(tools, second, sessionId, "\u0003", 0);
    }

    [Fact]
    public async Task Default_limit_allows_five_owned_exec_sessions_and_rejects_the_sixth()
    {
        using var tools = new ProcessToolSet();
        var sessions = new List<(AgentExecutionContext Context, long Id)>();
        for (var i = 0; i < 5; i++)
        {
            var context = Context() with { Workspace = "" };
            sessions.Add((context, await Start(tools, context, Path.GetTempPath())));
        }
        var sixth = await Exec(tools, Context(), BlockingCommand(), Path.GetTempPath());
        Assert.True(sixth.IsError);
        Assert.Contains("PROCESS_LIMIT", sixth.Text);
        foreach (var item in sessions) await Stdin(tools, item.Context, item.Id, "\u0003", 0);
    }

    [Fact]
    public async Task Session_cancellation_stops_only_that_sessions_exec()
    {
        using var tools = new ProcessToolSet();
        using var stop = new CancellationTokenSource();
        var a = Context() with { SessionCancellation = stop.Token };
        var b = Context();
        _ = await Start(tools, a);
        var idB = await Start(tools, b);
        stop.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (tools.RunningForSession(new(a.OwnerId, a.AgentDeviceId, a.SessionId)) > 0)
            await Task.Delay(25, timeout.Token);
        Assert.Equal(1, tools.RunningForSession(new(b.OwnerId, b.AgentDeviceId, b.SessionId)));
        await Stdin(tools, b, idB, "\u0003", 0);
    }

    private static AgentExecutionContext Context() =>
        new(Path.GetTempPath(), Guid.NewGuid().ToString("N"), AgentSessionRules.NewSessionId())
        { OwnerId = "owner", AgentDeviceId = "device" };

    private static AgentExecutionContext SessionlessContext() =>
        new(Path.GetTempPath(), Guid.NewGuid().ToString("N"), AgentSessionRules.NewEphemeralExecutionId())
        { OwnerId = "owner", AgentDeviceId = "device" };

    private static IAgentTool Tool(ProcessToolSet tools, string id) =>
        tools.Tools.Single(tool => tool.Descriptor.Id == id);

    private static async Task<long> Start(ProcessToolSet tools, AgentExecutionContext context, string? workdir = null)
    {
        var reply = await Exec(tools, context, BlockingCommand(), workdir);
        Assert.False(reply.IsError, reply.Text);
        using var json = JsonDocument.Parse(reply.Text);
        return json.RootElement.GetProperty("session_id").GetInt64();
    }

    private static Task<ToolReply> Exec(ProcessToolSet tools, AgentExecutionContext context, string cmd, string? workdir = null) =>
        Tool(tools, "unified_exec.exec_command").ExecuteAsync(
            WireJson.Element(new { cmd, workdir, tty = false, yield_time_ms = 20 }), context, CancellationToken.None);

    private static Task<ToolReply> Stdin(ProcessToolSet tools, AgentExecutionContext context, long sessionId, string chars, int yieldMs = 50) =>
        Tool(tools, "unified_exec.write_stdin").ExecuteAsync(
            WireJson.Element(new { session_id = sessionId, chars, yield_time_ms = yieldMs }), context, CancellationToken.None);

    private static string BlockingCommand() => OperatingSystem.IsWindows()
        ? "[Console]::In.ReadLine() | Out-Null"
        : "read line";
}
