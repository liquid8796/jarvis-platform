using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Server.Tests;

public sealed class AgentTaskRobustnessTests
{
    private static RemoteTaskStep LongStep(int timeout = 30) => new()
    {
        Id = "wait", ToolId = "process.start", TimeoutSeconds = timeout,
        Arguments = WireJson.Element(new { command = OperatingSystem.IsWindows()
            ? "Write-Output PARTIAL_OUTPUT_MARKER; Start-Sleep -Seconds 30; Write-Output SHOULD_NOT_COMPLETE"
            : "echo PARTIAL_OUTPUT_MARKER; sleep 30; echo SHOULD_NOT_COMPLETE", timeoutSeconds = 60 })
    };
    private static RemoteTaskPlan Plan(RemoteTaskStep step) => new() { Goal = "Robustness fixture", Steps = [step] };

    [Fact]
    public async Task Task_budget_expiry_is_failed_not_transport_interrupted()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        var task = await peer.CreateAsync(admin, Plan(LongStep()) with { TimeoutSeconds = 3 });
        var result = await peer.TerminalAsync(admin, task.TaskId);
        Assert.Equal("FAILED", result.Status);
        Assert.Contains("deadline", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Step_deadline_preserves_partial_process_output()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        var task = await peer.CreateAsync(admin, Plan(LongStep(3)));
        Assert.Equal("FAILED", (await peer.TerminalAsync(admin, task.TaskId)).Status);
        var artifacts = await admin.GetFromJsonAsync<JsonElement>(peer.Url(task.TaskId, "/artifacts"));
        var item = Assert.Single(artifacts.GetProperty("artifacts").EnumerateArray());
        Assert.False(item.GetProperty("success").GetBoolean());
        Assert.Contains("PARTIAL_OUTPUT_MARKER", item.GetProperty("output").GetString());
        Assert.DoesNotContain("SHOULD_NOT_COMPLETE", item.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Health_reports_actual_server_assembly_version()
    {
        using var app = new ServerFixture(); using var client = app.Client();
        var health = await client.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal(typeof(Program).Assembly.GetName().Version!.ToString(3), health.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Transport_loss_interrupts_owned_job_without_replaying_on_reconnect()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        var task = await peer.CreateAsync(admin, Plan(LongStep()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (peer.StartedJobId is null) await Task.Delay(50, timeout.Token);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Connection.ConnectionChanged += online => { if (online) reconnected.TrySetResult(); };
        app.Services.GetRequiredService<IAgentRouter>().DisconnectDevice(peer.DeviceId);
        await reconnected.Task.WaitAsync(timeout.Token);
        Assert.Equal("INTERRUPTED", (await peer.TerminalAsync(admin, task.TaskId)).Status);
        Assert.Equal(1, peer.Approval.Calls); Assert.True(peer.Gate.IsArmed);
        while (!(await peer.JobAsync()).GetProperty("done").GetBoolean()) await Task.Delay(50, timeout.Token);
    }

    [Fact]
    public async Task Restart_marks_unfinished_snapshot_interrupted_and_does_not_execute_it()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var first = await TaskAgentPeer.ConnectAsync(app, admin);
        var task = await first.CreateAsync(admin, new RemoteTaskPlan { Goal = "Crash fixture" });
        await first.DisposeAsync();
        var path = Assert.Single(Directory.EnumerateFiles(first.StorageRoot, task.TaskId + ".json", SearchOption.AllDirectories));
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        saved["snapshot"]!["status"] = "RUNNING";
        await File.WriteAllTextAsync(path, saved.ToJsonString());
        await using var second = await TaskAgentPeer.ConnectAsync(app, admin, deviceId: first.DeviceId, token: first.Token);
        Assert.Equal("INTERRUPTED", (await second.TerminalAsync(admin, task.TaskId)).Status);
        Assert.Equal(0, second.Approval.Calls); Assert.Null(second.StartedJobId);
    }

    [Fact]
    public async Task Task_output_is_bounded_and_expected_text_is_checked()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, new LargeTool());
        var step = new RemoteTaskStep { Id = "large", ToolId = "test.large", ExpectedText = "missing-marker" };
        var task = await peer.CreateAsync(admin, Plan(step));
        Assert.Equal("FAILED", (await peer.TerminalAsync(admin, task.TaskId)).Status);
        var artifacts = await admin.GetFromJsonAsync<JsonElement>(peer.Url(task.TaskId, "/artifacts"));
        var item = Assert.Single(artifacts.GetProperty("artifacts").EnumerateArray());
        Assert.True(item.GetProperty("truncated").GetBoolean());
        Assert.Equal(RemoteTaskRules.OutputLimit, item.GetProperty("output").GetString()!.Length);
        Assert.EndsWith("END", item.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Disabled_capability_and_schema_forgery_are_rejected_before_execution()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        var forged = LongStep() with { Arguments = WireJson.Element(new { command = "echo x", fullPermission = true }) };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/agent/tasks", peer.Input(Plan(forged)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/agent/tasks", peer.Input(Plan(LongStep()) with { ExecutionMode = "READ_ONLY" }))).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Tools.SingleAsync(t => t.AgentToolId == "process.start")).Enabled = false;
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/agent/tasks", peer.Input(Plan(LongStep())))).StatusCode);
        Assert.Null(peer.StartedJobId); Assert.Equal(0, peer.Approval.Calls);
    }

    [Fact]
    public async Task Revoking_standing_permission_cancels_running_task_without_rearming()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        peer.Permissions.Replace(["process.start"]);
        var task = await peer.CreateAsync(admin, Plan(LongStep()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (peer.StartedJobId is null) await Task.Delay(50, timeout.Token);
        peer.Permissions.Replace([]);
        Assert.Equal("CANCELLED", (await peer.TerminalAsync(admin, task.TaskId)).Status);
        Assert.True(peer.Gate.IsArmed); Assert.Equal(0, peer.Approval.Calls);
    }

    [Fact]
    public async Task Expected_text_must_exist_in_retained_output_not_a_discarded_prefix()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, new LargeTool());
        var step = new RemoteTaskStep { Id = "large", ToolId = "test.large", ExpectedText = "DISCARDED_PREFIX" };
        var task = await peer.CreateAsync(admin, Plan(step));
        Assert.Equal("FAILED", (await peer.TerminalAsync(admin, task.TaskId)).Status);
    }

    private sealed class LargeTool : IAgentTool
    {
        public ToolDescriptor Descriptor => new("test.large", "test__large", "test", "Large fixture output",
            WireJson.Element(new { type = "object", additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct) =>
            Task.FromResult(new ToolReply("DISCARDED_PREFIX" + new string('x', 100000) + "END"));
    }
}
