using System.Text.Json;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.McpServer.Application;

/// <summary>Owner/device-bound gateway. Task state and execution stay on the agent.</summary>
public sealed class AgentTaskService(AppDbContext db, IAgentTaskRouter router, IAuditWriter audit)
{
    public bool SupportsTasks(string ownerId, string deviceId) => router.SupportsTasks(ownerId, deviceId);

    public Task<RemoteTaskReply> SendAsync(string ownerId, string deviceId, string operation, string? taskId,
        RemoteTaskPlan? plan, int offset, int limit, CancellationToken ct) =>
        SendAsync(ownerId, deviceId, operation, taskId, plan, offset, limit, null, ct);

    public async Task<RemoteTaskReply> SendAsync(string ownerId, string deviceId, string operation, string? taskId,
        RemoteTaskPlan? plan, int offset, int limit, string? parentTaskId, CancellationToken ct)
    {
        var device = await OwnedAsync(ownerId, deviceId, ct);
        if (!RemoteTaskRules.Operations.Contains(operation)) throw new ArgumentException("Unknown task operation.");
        var id = taskId is null && operation == "create" ? Guid.NewGuid().ToString("N") : RemoteTaskRules.TaskId(taskId ?? "");
        if (offset < 0 || limit is < 1 or > 20) throw new ArgumentException("offset must be nonnegative; limit must be 1..20.");
        if (operation is "create" or "plan")
        {
            if (plan is null) throw new ArgumentException("Task plan metadata is required.");
            RemoteTaskRules.Validate(plan);
            if (operation == "plan" && plan.Steps.Count == 0) throw new ArgumentException("The submitted plan must contain steps.");
            var installed = (JsonSerializer.Deserialize<ToolDescriptor[]>(device.CapabilitiesJson, WireJson.Options) ?? [])
                .ToDictionary(t => t.Id, StringComparer.Ordinal);
            var enabled = (await db.Tools.AsNoTracking().Where(t => t.Enabled).Select(t => t.AgentToolId).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var step in plan.Steps)
            {
                if (!enabled.Contains(step.ToolId) || !installed.TryGetValue(step.ToolId, out var tool))
                    throw new UnauthorizedAccessException("A task step references a disabled or uninstalled tool: " + step.ToolId);
                if (!SchemaGuard.Matches(SchemaGuard.Compile(tool.InputSchema), step.Arguments))
                    throw new ArgumentException("Task step arguments do not match the installed schema: " + step.Id);
                if ((plan.ExecutionMode == "READ_ONLY" || step.MaxAttempts > 1) && (!tool.ReadOnly || tool.Sensitive))
                    throw new ArgumentException("Read-only mode and retries cannot invoke mutating/sensitive tools.");
                if (step.ToolId == "process.start" && (!enabled.Contains("process.read") || !enabled.Contains("process.cancel") ||
                    !installed.ContainsKey("process.read") || !installed.ContainsKey("process.cancel")))
                    throw new UnauthorizedAccessException("Process task plans require enabled installed process.read and process.cancel capabilities.");
            }
        }
        await audit.WriteAsync(new() { UserId = ownerId, DeviceId = deviceId, Action = "task." + operation,
            CorrelationId = id, Outcome = "requested" }, ct);
        RemoteTaskReply reply;
        try { reply = await router.TaskAsync(ownerId, deviceId, operation, new(ownerId, id, plan, offset, limit, parentTaskId), ct); }
        catch (OperationCanceledException)
        { reply = RemoteTaskReply.Failure("timeout", "Task acknowledgement timed out. Query taskId=" + id + " before retrying; completion may be unknown."); }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException)
        { reply = RemoteTaskReply.Failure("offline", "Agent connection was lost. Query taskId=" + id + " after reconnect; do not blindly replay work."); }
        using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await audit.WriteAsync(new() { UserId = ownerId, DeviceId = deviceId, Action = "task." + operation,
            CorrelationId = id, Outcome = reply.ErrorCode ?? reply.Task?.Status ?? "acknowledged" }, auditTimeout.Token);
        return reply;
    }
    public async Task<IReadOnlyList<ToolDescriptor>> DescribeToolsAsync(string ownerId, string deviceId, CancellationToken ct)
    {
        var device = await OwnedAsync(ownerId, deviceId, ct);
        var enabled = (await db.Tools.AsNoTracking().Where(t => t.Enabled).Select(t => t.AgentToolId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return (JsonSerializer.Deserialize<ToolDescriptor[]>(device.CapabilitiesJson, WireJson.Options) ?? [])
            .Where(t => enabled.Contains(t.Id)).OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
    }
    private async Task<Device> OwnedAsync(string ownerId, string deviceId, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.OwnerId == ownerId && d.Id == deviceId, ct)
            ?? throw new KeyNotFoundException("Device not found.");
        if (!device.Enabled || device.TokenExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
            !await db.Users.AnyAsync(u => u.Id == ownerId && u.Status == "active", ct))
            throw new UnauthorizedAccessException("Device/user authorization is not active.");
        return device;
    }

}
