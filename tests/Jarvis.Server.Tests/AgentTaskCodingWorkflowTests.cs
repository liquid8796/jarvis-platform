using System.Text.Json;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Server.Tests;

public sealed partial class AgentTaskMcpTests
{
    [Fact]
    public async Task Retained_check_spec_cannot_bypass_a_fresh_disabled_server_tool_catalog()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var spec = new CodingVerificationSpec { Profiles = ["cli"], Checks = [new()
        {
            Id = "marker", Kind = "command", ToolId = "process.start", Requirement = "The CLI writes the required acceptance marker.",
            Arguments = WireJson.Element(new { command = "echo ACTUAL_MARKER", timeoutSeconds = 20 }), ExpectedText = "EXPECTED_MARKER"
        }] };
        var created = await CallAsync(client, "agent_task_create", new { goal = "Validate CLI marker", codingVerification = spec });
        var id = created.GetProperty("structuredContent").GetProperty("task").GetProperty("taskId").GetString()!;
        Assert.Equal("NEEDS_REPAIR", (await AwaitPending()).Status);
        var firstJob = peer.StartedJobId;
        Assert.NotNull(firstJob);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entry = await db.Tools.SingleAsync(tool => tool.AgentToolId == "process.start");
            entry.Enabled = false; await db.SaveChangesAsync();
        }
        var forgedAllowlist = await CallAsync(client, "agent_task_verify", new { taskId = id, attemptId = Guid.NewGuid().ToString("N"), enabledToolIds = new[] { "process.start" } });
        Assert.True(forgedAllowlist.GetProperty("isError").GetBoolean());
        var verified = await CallAsync(client, "agent_task_verify", new { taskId = id, attemptId = Guid.NewGuid().ToString("N") });
        Assert.False(verified.TryGetProperty("isError", out var error) && error.GetBoolean(), verified.GetRawText());
        var blocked = await AwaitPending();
        Assert.False(blocked.CodingVerification!.Passed);
        Assert.Equal("blocked", Assert.Single(blocked.CodingVerification.Checks).State);
        Assert.Equal(firstJob, peer.StartedJobId); // No second process was dispatched.

        async Task<RemoteTaskSnapshot> AwaitPending()
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var result = await CallAsync(client, "agent_task_get", new { taskId = id });
                var snapshot = result.GetProperty("structuredContent").GetProperty("task").Deserialize<RemoteTaskSnapshot>(WireJson.Options)!;
                if (snapshot.Status.StartsWith("NEEDS_", StringComparison.Ordinal) || RemoteTaskRules.IsTerminal(snapshot.Status)) return snapshot;
                await Task.Delay(25);
            }
            throw new TimeoutException("Verification did not reach an observable pending state.");
        }
    }

    [Fact]
    public async Task OAuth_coding_spec_runs_real_cli_acceptance_and_exposes_live_events_and_owned_report()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        var argv = OperatingSystem.IsWindows()
            ? new[] { "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "Write-Output CODING_WORKFLOW_OK" }
            : new[] { "/bin/sh", "-c", "printf 'CODING_WORKFLOW_OK\\n'" };
        var spec = new CodingVerificationSpec
        {
            Profiles = ["cli"], Checks = [new()
            {
                Id = "stdout-contract", Kind = "command", ToolId = "process.spawn",
                Requirement = "The fixture CLI exits successfully and writes its required stdout marker.",
                Arguments = WireJson.Element(new { argv, timeoutSeconds = 20 }), ExpectedText = "CODING_WORKFLOW_OK"
            }]
        };
        var created = await CallAsync(client, "agent_task_create", new { goal = "Validate CLI stdout behavior", codingVerification = spec });
        var createData = AssertOutputMatches(listed, "agent_task_create", created);
        var id = createData.GetProperty("task").GetProperty("taskId").GetString()!;
        RemoteTaskSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var reply = await CallAsync(client, "agent_task_get", new { taskId = id });
            snapshot = AssertOutputMatches(listed, "agent_task_get", reply).GetProperty("task").Deserialize<RemoteTaskSnapshot>(WireJson.Options);
            if (RemoteTaskRules.IsTerminal(snapshot!.Status) || snapshot.Status.StartsWith("NEEDS_", StringComparison.Ordinal)) break;
            await Task.Delay(25);
        }
        Assert.Equal("COMPLETED", snapshot!.Status);
        Assert.True(snapshot.CodingVerification!.Passed);
        var receipt = Assert.Single(snapshot.CodingVerification.Checks);
        Assert.Equal(spec.Checks[0].Requirement, receipt.Requirement);
        Assert.Equal(0, receipt.ExitCode);
        Assert.NotNull(receipt.Report);
        var reportReply = await CallAsync(client, "agent_task_report", new { taskId = id, artifactId = receipt.Report!.ArtifactId });
        var report = AssertOutputMatches(listed, "agent_task_report", reportReply).GetProperty("report");
        Assert.Equal(receipt.Report.Sha256, report.GetProperty("sha256").GetString());
        Assert.Contains("CODING_WORKFLOW_OK", report.GetProperty("text").GetString());
        var eventsReply = await CallAsync(client, "agent_task_events", new { taskId = id, offset = 0, limit = 20 });
        var events = AssertOutputMatches(listed, "agent_task_events", eventsReply).GetProperty("events").EnumerateArray().ToArray();
        Assert.Contains(events, item => item.GetProperty("type").GetString() == "check.completed");
        Assert.Contains(events, item => item.TryGetProperty("text", out var output) && output.GetString()!.Contains("CODING_WORKFLOW_OK", StringComparison.Ordinal));
        var forged = await CallAsync(client, "agent_task_report", new { taskId = id, artifactId = "../../private" });
        Assert.True(forged.GetProperty("isError").GetBoolean());
        AssertOutputMatches(listed, "agent_task_report", forged);
    }
}
