using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class SessionProcessOwnershipTests
{
    [Fact]
    public async Task Another_session_cannot_read_cancel_or_send_input_to_a_job()
    {
        using var tools = new ProcessToolSet();
        var a = Context(); var b = Context();
        var id = await Start(tools, a);
        foreach (var operation in new[] { "process.read", "process.cancel", "process.write_stdin", "process.resize_pty" })
        {
            var arguments = operation switch
            {
                "process.write_stdin" => WireJson.Element(new { jobId = id, text = "foreign input\n" }),
                "process.resize_pty" => WireJson.Element(new { jobId = id, columns = 120, rows = 40 }),
                _ => WireJson.Element(new { jobId = id })
            };
            var reply = await Tool(tools, operation).ExecuteAsync(arguments, b, CancellationToken.None);
            Assert.True(reply.IsError, operation + " accepted another session's job.");
            Assert.Contains("owned", reply.Text, StringComparison.OrdinalIgnoreCase);
        }
        var own = await Tool(tools, "process.read").ExecuteAsync(WireJson.Element(new { jobId = id }), a, CancellationToken.None);
        Assert.False(own.IsError, own.Text);
        Assert.False(JsonDocument.Parse(own.Text).RootElement.GetProperty("done").GetBoolean());
        var forged = a with { OwnerId = "different-owner" };
        Assert.True((await Tool(tools, "process.read").ExecuteAsync(WireJson.Element(new { jobId = id }), forged, CancellationToken.None)).IsError);
    }

    [Fact]
    public async Task Default_limit_allows_five_owned_jobs_and_absolute_workdir_with_empty_workspace()
    {
        using var tools = new ProcessToolSet();
        for (var i = 0; i < 5; i++)
        {
            var context = Context() with { Workspace = "" };
            var id = await Start(tools, context, Path.GetTempPath());
            Assert.NotEmpty(id);
        }
    }

    [Fact]
    public async Task Session_cancellation_stops_only_that_sessions_job()
    {
        using var tools = new ProcessToolSet();
        using var stopA = new CancellationTokenSource();
        var a = Context() with { SessionCancellation = stopA.Token };
        var b = Context();
        var idA = await Start(tools, a);
        var idB = await Start(tools, b);
        stopA.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            using var snapshot = JsonDocument.Parse((await Tool(tools, "process.read").ExecuteAsync(WireJson.Element(new { jobId = idA }), a, timeout.Token)).Text);
            if (snapshot.RootElement.GetProperty("done").GetBoolean()) break;
            await Task.Delay(25, timeout.Token);
        }
        using var other = JsonDocument.Parse((await Tool(tools, "process.read").ExecuteAsync(WireJson.Element(new { jobId = idB }), b, timeout.Token)).Text);
        Assert.False(other.RootElement.GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task Background_process_keeps_its_resource_lease_until_owned_processes_finish()
    {
        using var resources = new Jarvis.Agent.Core.Execution.ExecutionResourceCoordinator(10);
        using var tools = new ProcessToolSet();
        var callLease = (Jarvis.Agent.Core.Execution.IExecutionResourceLease)await resources.AcquireAsync("a", new[] { "*" }, true, CancellationToken.None);
        using var disposeCall = callLease;
        var context = Context();
        var property = typeof(AgentExecutionContext).GetProperty("RetainResources");
        Assert.NotNull(property);
        property.SetValue(context, new Func<IDisposable>(callLease.Retain));
        var id = await Start(tools, context);
        callLease.Dispose();
        var other = resources.AcquireAsync("b", new[] { "fs|some-file" }, false, CancellationToken.None);
        Assert.False(other.IsCompleted);
        await Tool(tools, "process.cancel").ExecuteAsync(WireJson.Element(new { jobId = id }), context, CancellationToken.None);
        (await other.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    private static AgentExecutionContext Context() => new(Path.GetTempPath(), Guid.NewGuid().ToString("N"), AgentSessionRules.NewSessionId())
    { OwnerId = "owner", AgentDeviceId = "agent" };
    private static IAgentTool Tool(ProcessToolSet tools, string id) => tools.Tools.Single(t => t.Descriptor.Id == id);
    private static async Task<string> Start(ProcessToolSet tools, AgentExecutionContext context, string? directory = null)
    {
        var argv = OperatingSystem.IsWindows()
            ? new[] { "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "[Console]::In.ReadLine() | Out-Null" }
            : new[] { "/bin/sh", "-c", "read line" };
        var reply = await Tool(tools, "process.spawn").ExecuteAsync(WireJson.Element(new { argv, workingDirectory = directory, timeoutSeconds = 15 }), context, CancellationToken.None);
        Assert.False(reply.IsError, reply.Text);
        using var initial = JsonDocument.Parse(reply.Text);
        return initial.RootElement.GetProperty("jobId").GetString()!;
    }
}
