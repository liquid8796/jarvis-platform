using Jarvis.McpServer.Application;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Server.Tests;

public sealed partial class AgentTaskMcpTests
{
    [Theory]
    [InlineData("1.0.79", true)]
    [InlineData("1.0.79.0", true)]
    [InlineData("1.0.79+abc123", true)]
    [InlineData("1.0.79.0+abc123", true)]
    [InlineData("1.0.80", true)]
    [InlineData("2.0.0", true)]
    [InlineData("1.0.78.99", false)]
    [InlineData("1.0.79-preview", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Coding_capability_version_fence_parses_release_and_assembly_versions(string? version, bool expected) =>
        Assert.Equal(expected, AgentTaskService.SupportsCodingVersion(version));

    [Fact]
    public async Task Older_agent_does_not_advertise_new_inspection_ops_and_cannot_silently_ignore_a_coding_spec()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.SingleAsync(item => item.Id == peer.DeviceId);
            device.AgentVersion = "1.0.78.0";
            await db.SaveChangesAsync();
        }
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        var names = listed.GetProperty("result").GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.DoesNotContain("agent_task_events", names);
        Assert.DoesNotContain("agent_task_report", names);
        Assert.DoesNotContain("agent_task_context", names);
        Assert.Contains("agent_task_get", names);
        Assert.Contains("agent_task_verify", names);
        Assert.Contains("agent_task_capture", names);
        var id = Guid.NewGuid().ToString("N");
        var coding = new CodingVerificationSpec { Profiles = ["cli"], Checks = [new()
        {
            Id = "do-not-run", Kind = "command", ToolId = "process.start", Requirement = "The explicit acceptance check requires a capable agent.",
            Arguments = WireJson.Element(new { command = "echo MUST_NOT_RUN" }), ExpectedText = "MUST_NOT_RUN"
        }] };
        var denied = await CallAsync(client, "agent_task_create", new { taskId = id, goal = "Validate CLI behavior", codingVerification = coding });
        Assert.True(denied.GetProperty("isError").GetBoolean());
        Assert.Equal("upgrade_required", denied.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        Assert.Contains("1.0.79", denied.GetProperty("structuredContent").GetProperty("error").GetString());
        Assert.Null(peer.StartedJobId);
        var absent = await CallAsync(client, "agent_task_get", new { taskId = id });
        Assert.Equal("not_found", absent.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        var legacy = await CallAsync(client, "agent_task_create", new { goal = "Inspect existing task metadata" });
        Assert.Equal("NEEDS_PLAN", legacy.GetProperty("structuredContent").GetProperty("task").GetProperty("status").GetString());
        var forcedNewOperation = await CallAsync(client, "agent_task_events", new { taskId = id });
        Assert.Equal("upgrade_required", forcedNewOperation.GetProperty("structuredContent").GetProperty("errorCode").GetString());
    }
}
