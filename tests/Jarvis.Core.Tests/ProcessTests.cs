using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ProcessTests
{
    [Fact]
    public void Codex_process_surface_exposes_only_exec_command_and_write_stdin()
    {
        using var tools = new ProcessToolSet();
        var ids = tools.Tools.Select(tool => tool.Descriptor.Id).ToArray();
        Assert.Equal(["unified_exec.exec_command", "unified_exec.write_stdin"], ids);
        Assert.Equal("exec_command", tools.Tools.First().Descriptor.Name);
        Assert.Equal("write_stdin", tools.Tools.Last().Descriptor.Name);
    }

    [Fact]
    public async Task Exec_command_returns_output_and_exit_code()
    {
        using var tools = new ProcessToolSet();
        var context = Context("quick");
        var reply = await Exec(tools, context, Echo("codex-exec-output"), yieldMs: 10_000);
        Assert.False(reply.IsError, reply.Text);
        using var result = JsonDocument.Parse(reply.Text);
        Assert.Contains("codex-exec-output", result.RootElement.GetProperty("output").GetString());
        Assert.Equal(0, result.RootElement.GetProperty("exit_code").GetInt32());
        Assert.False(result.RootElement.TryGetProperty("session_id", out _));
    }

    [Fact]
    public async Task Exec_command_streams_partial_output_before_completion()
    {
        using var tools = new ProcessToolSet();
        var context = Context("partial");
        var command = OperatingSystem.IsWindows()
            ? "[Console]::Out.WriteLine('PARTIAL_OUTPUT_MARKER'); [Console]::Out.Flush(); Start-Sleep -Seconds 5; [Console]::Out.WriteLine('DONE')"
            : "echo PARTIAL_OUTPUT_MARKER; sleep 5; echo DONE";
        var started = await Exec(tools, context, command, yieldMs: 2_000, tty: false);
        Assert.False(started.IsError, started.Text);
        using var json = JsonDocument.Parse(started.Text);
        Assert.Contains("PARTIAL_OUTPUT_MARKER", json.RootElement.GetProperty("output").GetString());
        var sessionId = json.RootElement.GetProperty("session_id").GetInt64();
        await Write(tools, context, sessionId, "\u0003", 0);
    }

    [Fact]
    public async Task Write_stdin_polls_and_writes_to_owned_process()
    {
        using var tools = new ProcessToolSet();
        var context = Context("stdin");
        var command = OperatingSystem.IsWindows()
            ? "$line=[Console]::In.ReadLine(); Write-Output ('stdin:'+$line)"
            : "read line; echo stdin:$line";
        var started = await Exec(tools, context, command, yieldMs: 20, tty: false);
        var sessionId = SessionId(started);

        var written = await Write(tools, context, sessionId, "hello-from-stdin\n", 100);
        Assert.False(written.IsError, written.Text);
        var completed = await PollUntilDone(tools, context, sessionId, written.Text);
        Assert.Contains("stdin:hello-from-stdin", completed.Output);
        Assert.Equal(0, completed.ExitCode);
    }

    [Fact]
    public async Task Write_stdin_rejects_an_unowned_session()
    {
        using var tools = new ProcessToolSet();
        var owner = Context("owner");
        var foreign = Context("foreign");
        var started = await Exec(tools, owner, BlockingCommand(), yieldMs: 20, tty: false);
        var sessionId = SessionId(started);
        var reply = await Write(tools, foreign, sessionId, "", 0);
        Assert.True(reply.IsError);
        Assert.Contains("owned", reply.Text, StringComparison.OrdinalIgnoreCase);
        await Write(tools, owner, sessionId, "\u0003", 0);
    }

    [Fact]
    public async Task Ctrl_c_cancels_an_owned_exec_session()
    {
        using var tools = new ProcessToolSet();
        var context = Context("cancel");
        var started = await Exec(tools, context, BlockingCommand(), yieldMs: 20, tty: false);
        var sessionId = SessionId(started);
        var cancelled = await Write(tools, context, sessionId, "\u0003", 100);
        Assert.False(cancelled.IsError, cancelled.Text);
        await PollUntilDone(tools, context, sessionId, cancelled.Text);
        Assert.Equal(0, tools.RunningCount);
    }

    [Fact]
    public async Task Background_exec_keeps_its_resource_lease_until_process_finishes()
    {
        using var resources = new ExecutionResourceCoordinator(4);
        using var tools = new ProcessToolSet();
        using var callLease = (IExecutionResourceLease)await resources.AcquireAsync(
            "owner", ["*"], true, CancellationToken.None);
        var context = Context("lease") with { RetainResources = callLease.Retain };
        var started = await Exec(tools, context, BlockingCommand(), yieldMs: 20, tty: false);
        var sessionId = SessionId(started);
        callLease.Dispose();

        var waiting = resources.AcquireAsync("other", ["fs|other"], false, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        var cancelled = await Write(tools, context, sessionId, "\u0003", 100);
        await PollUntilDone(tools, context, sessionId, cancelled.Text);
        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Exec_command_honors_an_explicit_workdir()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-exec-workdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var tools = new ProcessToolSet();
            var reply = await Tool(tools, "unified_exec.exec_command").ExecuteAsync(
                WireJson.Element(new { cmd = CurrentDirectoryCommand(), workdir = root, tty = false, yield_time_ms = 10_000 }),
                Context("workdir"), CancellationToken.None);
            Assert.False(reply.IsError, reply.Text);
            using var result = JsonDocument.Parse(reply.Text);
            Assert.Contains(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                result.RootElement.GetProperty("output").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static AgentExecutionContext Context(string suffix) =>
        new(Path.GetTempPath(), "call-" + suffix, "session-" + suffix)
        { OwnerId = "owner", AgentDeviceId = "device" };

    private static IAgentTool Tool(ProcessToolSet tools, string id) =>
        tools.Tools.Single(tool => tool.Descriptor.Id == id);

    private static Task<ToolReply> Exec(ProcessToolSet tools, AgentExecutionContext context, string cmd,
        int yieldMs, bool tty = false) =>
        Tool(tools, "unified_exec.exec_command").ExecuteAsync(
            WireJson.Element(new { cmd, tty, login = false, yield_time_ms = yieldMs, max_output_tokens = 10_000 }),
            context, CancellationToken.None);

    private static Task<ToolReply> Write(ProcessToolSet tools, AgentExecutionContext context, long sessionId,
        string chars, int yieldMs) =>
        Tool(tools, "unified_exec.write_stdin").ExecuteAsync(
            WireJson.Element(new { session_id = sessionId, chars, yield_time_ms = yieldMs, max_output_tokens = 10_000 }),
            context, CancellationToken.None);

    private static long SessionId(ToolReply reply)
    {
        Assert.False(reply.IsError, reply.Text);
        using var json = JsonDocument.Parse(reply.Text);
        return json.RootElement.GetProperty("session_id").GetInt64();
    }

    private static async Task<(string Output, int? ExitCode)> PollUntilDone(
        ProcessToolSet tools, AgentExecutionContext context, long sessionId, string? initial = null)
    {
        var output = new System.Text.StringBuilder();
        int? exitCode = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var next = initial;
        while (true)
        {
            if (next is null)
            {
                var reply = await Tool(tools, "unified_exec.write_stdin").ExecuteAsync(
                    WireJson.Element(new { session_id = sessionId, chars = "", yield_time_ms = 100 }), context, timeout.Token);
                Assert.False(reply.IsError, reply.Text);
                next = reply.Text;
            }
            using var json = JsonDocument.Parse(next);
            output.Append(json.RootElement.GetProperty("output").GetString());
            if (json.RootElement.TryGetProperty("exit_code", out var exit)) exitCode = exit.GetInt32();
            if (!json.RootElement.TryGetProperty("session_id", out _)) return (output.ToString(), exitCode);
            next = null;
        }
    }

    private static string Echo(string value) => OperatingSystem.IsWindows()
        ? $"Write-Output '{value}'"
        : $"printf '%s\\n' '{value}'";

    private static string BlockingCommand() => OperatingSystem.IsWindows()
        ? "[Console]::In.ReadLine() | Out-Null"
        : "read line";

    private static string CurrentDirectoryCommand() => OperatingSystem.IsWindows()
        ? "[Environment]::CurrentDirectory"
        : "pwd";
}
