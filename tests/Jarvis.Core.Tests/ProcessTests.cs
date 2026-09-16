using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;
namespace Jarvis.Core.Tests;
public sealed class ProcessTests
{
    [Fact] public async Task Owned_process_returns_output_and_status()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-job-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            using var tools = new ProcessToolSet(); var context = new AgentExecutionContext(root, "call-1", "test");
            var start = tools.Tools.Single(t => t.Descriptor.Id == "process.start");
            var reply = await start.ExecuteAsync(WireJson.Element(new { command = "echo jarvis-test-output", timeoutSeconds = 10 }), context, CancellationToken.None);
            using var initial = JsonDocument.Parse(reply.Text); var id = initial.RootElement.GetProperty("jobId").GetString();
            var read = tools.Tools.Single(t => t.Descriptor.Id == "process.read");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                using var result = JsonDocument.Parse((await read.ExecuteAsync(WireJson.Element(new { jobId = id, cursor = 0 }), context, timeout.Token)).Text);
                if (result.RootElement.GetProperty("done").GetBoolean())
                { Assert.Contains("jarvis-test-output", result.RootElement.GetProperty("output").GetString()); Assert.Equal(0, result.RootElement.GetProperty("exitCode").GetInt32()); break; }
                await Task.Delay(50, timeout.Token);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public async Task Read_cannot_attach_to_an_unowned_job()
    {
        using var tools = new ProcessToolSet();
        var result = await tools.Tools.Single(t => t.Descriptor.Id == "process.read").ExecuteAsync(WireJson.Element(new { jobId = "other-process" }), new(Path.GetTempPath(),"c","s"),CancellationToken.None);
        Assert.True(result.IsError);
    }

    [Fact] public void Process_v2_exposes_argv_and_interactive_tools()
    {
        using var tools = new ProcessToolSet();
        var ids = tools.Tools.Select(t => t.Descriptor.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("process.spawn", ids);
        Assert.Contains("process.write_stdin", ids);
        Assert.Contains("process.resize_pty", ids);
    }

    [Fact] public async Task Spawn_uses_argv_environment_and_structured_stream_events()
    {
        using var tools = new ProcessToolSet();
        var context = new AgentExecutionContext(Path.GetTempPath(), "spawn-env", "test");
        var spawn = tools.Tools.Single(t => t.Descriptor.Id == "process.spawn");
        var argv = OperatingSystem.IsWindows()
            ? new[] { "cmd.exe", "/d", "/c", "echo %JARVIS_PROCESS_TEST%" }
            : new[] { "/bin/sh", "-lc", "echo \"$JARVIS_PROCESS_TEST\"" };
        var reply = await spawn.ExecuteAsync(WireJson.Element(new
        {
            argv,
            environment = new Dictionary<string, string> { ["JARVIS_PROCESS_TEST"] = "argv-env-ok" },
            timeoutSeconds = 10
        }), context, CancellationToken.None);
        using var initial = JsonDocument.Parse(reply.Text);
        var id = initial.RootElement.GetProperty("jobId").GetString();
        using var result = await ReadUntilDone(tools, context, id!);
        Assert.Equal(0, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("argv-env-ok", result.RootElement.GetProperty("output").GetString());
        Assert.Contains(result.RootElement.GetProperty("events").EnumerateArray(), e =>
            e.GetProperty("stream").GetString() == "stdout" && e.GetProperty("text").GetString()!.Contains("argv-env-ok"));
    }

    [Fact] public async Task Spawn_accepts_stdin_for_owned_processes()
    {
        using var tools = new ProcessToolSet();
        var context = new AgentExecutionContext(Path.GetTempPath(), "spawn-stdin", "test");
        var spawn = tools.Tools.Single(t => t.Descriptor.Id == "process.spawn");
        var argv = OperatingSystem.IsWindows()
            ? new[] { "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "$line=[Console]::In.ReadLine(); Write-Output ('stdin:'+$line)" }
            : new[] { "/bin/sh", "-c", "read line; echo stdin:$line" };
        using var initial = JsonDocument.Parse((await spawn.ExecuteAsync(WireJson.Element(new { argv, timeoutSeconds = 10 }), context, CancellationToken.None)).Text);
        var id = initial.RootElement.GetProperty("jobId").GetString();
        var write = tools.Tools.Single(t => t.Descriptor.Id == "process.write_stdin");
        var writeReply = await write.ExecuteAsync(WireJson.Element(new { jobId = id, text = "hello-from-stdin\n" }), context, CancellationToken.None);
        Assert.False(writeReply.IsError);
        using var result = await ReadUntilDone(tools, context, id!);
        Assert.Contains("stdin:hello-from-stdin", result.RootElement.GetProperty("output").GetString());
    }

    [Fact] public async Task Windows_pty_process_can_be_resized_and_cancelled()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var tools = new ProcessToolSet();
        var context = new AgentExecutionContext(Path.GetTempPath(), "spawn-pty", "test");
        var spawn = tools.Tools.Single(t => t.Descriptor.Id == "process.spawn");
        using var initial = JsonDocument.Parse((await spawn.ExecuteAsync(WireJson.Element(new
        {
            argv = new[] { "powershell.exe", "-NoProfile", "-NoExit", "-Command", "Write-Output 'pty-ready'" },
            pty = true,
            columns = 80,
            rows = 24,
            timeoutSeconds = 20
        }), context, CancellationToken.None)).Text);
        var id = initial.RootElement.GetProperty("jobId").GetString();
        var resize = tools.Tools.Single(t => t.Descriptor.Id == "process.resize_pty");
        var resized = await resize.ExecuteAsync(WireJson.Element(new { jobId = id, columns = 120, rows = 40 }), context, CancellationToken.None);
        Assert.False(resized.IsError);
        var cancel = tools.Tools.Single(t => t.Descriptor.Id == "process.cancel");
        Assert.False((await cancel.ExecuteAsync(WireJson.Element(new { jobId = id }), context, CancellationToken.None)).IsError);
    }

    private static async Task<JsonDocument> ReadUntilDone(ProcessToolSet tools, AgentExecutionContext context, string jobId)
    {
        var read = tools.Tools.Single(t => t.Descriptor.Id == "process.read");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var result = JsonDocument.Parse((await read.ExecuteAsync(WireJson.Element(new { jobId, cursor = 0 }), context, timeout.Token)).Text);
            if (result.RootElement.GetProperty("done").GetBoolean()) return result;
            result.Dispose();
            await Task.Delay(50, timeout.Token);
        }
    }
}
