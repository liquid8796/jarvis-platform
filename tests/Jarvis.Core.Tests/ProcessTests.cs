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
}
