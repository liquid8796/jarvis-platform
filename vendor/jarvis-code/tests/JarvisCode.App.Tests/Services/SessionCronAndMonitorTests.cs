using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Routines;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.App.Tests.Services;

public sealed class SessionCronAndMonitorTests
{
    [Fact]
    public void Runtime_jitter_matches_the_installed_260_defaults_including_cache_boundary()
    {
        var job = new Routine { Id = "80000000", CronExpression = "0 * * * *", CronSessionId = "test" };
        Assert.Equal(new DateTime(2026, 9, 8, 13, 15, 0), SessionCronTiming.NextDispatch(job, new DateTime(2026, 9, 8, 12, 1, 0)));
        job.OneShot = true;
        Assert.Equal(new DateTime(2026, 9, 8, 12, 59, 15), SessionCronTiming.NextDispatch(job, new DateTime(2026, 9, 8, 12, 59, 0)));
        job.OneShot = false; job.CronExpression = "*/5 * * * *";
        Assert.Equal(new DateTime(2026, 9, 8, 12, 4, 45), SessionCronTiming.NextDispatch(job, new DateTime(2026, 9, 8, 12, 0, 0)));
        Assert.Null(RoutineRunner.AutoDisableReason(job)); // 5-minute CronCreate jobs must not be disabled by Scheduled's hourly restriction
    }

    [Fact]
    public void An_idle_conversation_is_reserved_before_a_cron_prompt_is_queued()
    {
        var id = Guid.NewGuid().ToString();
        var busy = true;
        var enqueued = 0;
        SessionCronDispatch.Register(id, () => !busy, _ => enqueued++);
        try
        {
            Assert.False(SessionCronDispatch.TryEnqueue(id, "go"));
            busy = false;
            Assert.True(SessionCronDispatch.TryEnqueue(id, "go"));
            Assert.False(SessionCronDispatch.TryEnqueue(id, "duplicate"));
            Assert.Equal(1, enqueued);
        }
        finally { SessionCronDispatch.Unregister(id); }
    }

    [Fact]
    public async Task Actual_tool_wrapping_and_search_registry_advertise_the_armed_monitor_schema()
    {
        var wrapped = ReferenceToolDocs.Apply([new MonitorTool()]);
        var registry = new DeferredToolRegistry(wrapped, _ => true);
        Assert.DoesNotContain(registry.All, t => t.Name == "Monitor");
        var search = registry.Find("ToolSearch")!;
        var result = await search.ExecuteAsync(new JsonObject { ["query"] = "select:Monitor" },
            new ToolExecutionContext { WorkingDirectory = Path.GetTempPath() }, default);
        Assert.False(result.IsError, result.Content);
        var monitor = Assert.Single(registry.All, t => t.Name == "Monitor").ToDefinition();
        var properties = monitor.InputSchema["properties"]!.AsObject();
        Assert.Contains("command", properties.Select(p => p.Key));
        Assert.Contains("ws", properties.Select(p => p.Key));
        Assert.Contains("persistent", properties.Select(p => p.Key));
        Assert.DoesNotContain("task_id", properties.Select(p => p.Key));
        Assert.Contains("returns its taskId immediately", monitor.Description);
    }
}
