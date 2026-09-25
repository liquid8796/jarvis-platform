using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.McpServer.Transport;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Server.Tests;

internal sealed class TaskAgentPeer : IAsyncDisposable
{
    public string DeviceId { get; private set; } = "";
    public string Token { get; private set; } = "";
    public string Workspace { get; private set; } = "";
    public string StorageRoot { get; private set; } = "";
    public AgentConnection Connection { get; private set; } = null!;
    public LocalControlGate Gate { get; } = new();
    public TestApproval Approval { get; } = new();
    public ToolPermissionPolicy Permissions { get; } = new();
    public ProcessToolSet Processes { get; } = new();
    public string? StartedJobId;
    public AgentExecutionContext? StartedJobContext;
    private JsonElement? _lastJobSnapshot;
    private readonly CancellationTokenSource _stop = new();
    private Task _run = Task.CompletedTask;
    public static async Task<TaskAgentPeer> ConnectAsync(ServerFixture app, HttpClient admin,
        IAgentTool? extra = null, bool arm = true, bool approve = true, string? deviceId = null, string? token = null)
    {
        var peer = new TaskAgentPeer();
        if (deviceId is null)
        {
            var response = await admin.PostAsJsonAsync("/api/devices", new { name = "Real task agent fixture" });
            response.EnsureSuccessStatusCode();
            var enrolled = await response.Content.ReadFromJsonAsync<JsonElement>();
            peer.DeviceId = enrolled.GetProperty("deviceId").GetString()!;
            peer.Token = enrolled.GetProperty("token").GetString()!;
        }
        else { peer.DeviceId = deviceId; peer.Token = token!; }
        peer.Workspace = Path.Combine(app.Root, "workspace-" + peer.DeviceId);
        peer.StorageRoot = Path.Combine(app.Root, "task-snapshots");
        Directory.CreateDirectory(peer.Workspace);
        peer.Approval.Answer = approve;
        if (arm) peer.Gate.Arm();
        var tools = peer.Processes.Tools.Select(t => t.Descriptor.Id == "unified_exec.exec_command" ? (IAgentTool)new StartProbe(t, peer) : t).ToList();
        if (extra is not null) tools.Add(extra);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var tool in tools)
                if (!await db.Tools.AnyAsync(t => t.AgentToolId == tool.Descriptor.Id))
                    db.Tools.Add(new ToolEntry { AgentToolId = tool.Descriptor.Id, Name = tool.Descriptor.Name,
                        Category = tool.Descriptor.Category, Description = "Synthetic test publication", PublicationMode = ToolPublicationMode.Published });
            await db.SaveChangesAsync();
        }
        peer.Connection = new(tools, peer.Approval, peer.Gate, peer.Permissions, taskStorageRoot: peer.StorageRoot,
            socketConnector: async (_, enrollment, ct) =>
            {
                var client = app.Server.CreateWebSocketClient();
                client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + enrollment;
                return await client.ConnectAsync(new Uri("wss://localhost/agent/connect"), ct);
            });
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Connection.ConnectionChanged += online => { if (online) connected.TrySetResult(); };
        peer._run = peer.Connection.RunAsync(new("https://localhost", peer.DeviceId, peer.Workspace), peer.Token, peer._stop.Token);
        await Task.WhenAny(connected.Task, peer._run).WaitAsync(TimeSpan.FromSeconds(15));
        if (!connected.Task.IsCompleted) await peer._run;
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return peer;
    }
    public AgentTaskInput Input(RemoteTaskPlan plan, string? taskId = null) => new()
    { DeviceId = DeviceId, TaskId = taskId, Goal = plan.Goal, Project = plan.Project,
        ExecutionMode = plan.ExecutionMode, TimeoutSeconds = plan.TimeoutSeconds, Steps = plan.Steps };
    public async Task<RemoteTaskSnapshot> CreateAsync(HttpClient client, RemoteTaskPlan plan, string? id = null)
    {
        var response = await client.PostAsJsonAsync("/api/agent/tasks", Input(plan, id));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RemoteTaskSnapshot>(WireJson.Options))!;
    }
    public string Url(string taskId, string suffix = "") => $"/api/agent/tasks/{taskId}{suffix}?deviceId={DeviceId}";
    public async Task<RemoteTaskSnapshot> TerminalAsync(HttpClient client, string taskId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            var result = await client.GetFromJsonAsync<RemoteTaskSnapshot>(Url(taskId), WireJson.Options, timeout.Token);
            if (RemoteTaskRules.IsTerminal(result!.Status)) return result;
            await Task.Delay(500, timeout.Token);
        }
    }
    public async Task<JsonElement> JobAsync()
    {
        if (StartedJobId is null || !long.TryParse(StartedJobId, out var sessionId))
            throw new InvalidOperationException("The fixture has not started an owned process.");
        var reply = await Processes.Tools.Single(t => t.Descriptor.Id == "unified_exec.write_stdin").ExecuteAsync(
            WireJson.Element(new { session_id = sessionId, chars = "", yield_time_ms = 50 }),
            StartedJobContext ?? throw new InvalidOperationException("The fixture has not started an owned process."), CancellationToken.None);
        if (reply.IsError && _lastJobSnapshot is { } cached) return cached;
        Assert.False(reply.IsError, reply.Text);
        using var json = JsonDocument.Parse(reply.Text);
        var root = json.RootElement;
        var snapshot = WireJson.Element(new
        {
            done = !root.TryGetProperty("session_id", out _),
            output = root.TryGetProperty("output", out var output) ? output.GetString() ?? "" : "",
            exitCode = root.TryGetProperty("exit_code", out var exit) ? exit.GetInt32() : (int?)null
        });
        _lastJobSnapshot = snapshot;
        return snapshot;
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await Connection.DisposeAsync();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        Processes.Dispose(); _stop.Dispose();
    }
    private sealed class StartProbe(IAgentTool inner, TaskAgentPeer peer) : IAgentTool
    {
        public ToolDescriptor Descriptor => inner.Descriptor;
        public async Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
        {
            var reply = await inner.ExecuteAsync(args, context, ct);
            if (!reply.IsError)
            {
                using var json = JsonDocument.Parse(reply.Text);
                if (json.RootElement.TryGetProperty("session_id", out var session))
                {
                    peer.StartedJobContext = context;
                    peer.StartedJobId = session.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            return reply;
        }
    }
    internal sealed class TestApproval : IApprovalService
    {
        public bool Answer = true;
        public int Calls;
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement args, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(Answer); }
    }
}
