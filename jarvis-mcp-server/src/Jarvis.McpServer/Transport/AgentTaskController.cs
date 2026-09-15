using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Jarvis.McpServer.Application;
using Jarvis.McpServer.Infrastructure;
using Jarvis.McpServer.Security;
using Jarvis.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jarvis.McpServer.Transport;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentTaskInput
{
    public string? DeviceId { get; init; }
    public string? TaskId { get; init; }
    [StringLength(36)] public string? ParentTaskId { get; init; }
    [Required, StringLength(8000)] public string Goal { get; init; } = "";
    [StringLength(1024)] public string? Project { get; init; }
    public string ExecutionMode { get; init; } = "NORMAL";
    public int TimeoutSeconds { get; init; } = 1800;
    public IReadOnlyList<RemoteTaskStep> Steps { get; init; } = [];
    public RemoteTaskPlan ToPlan() => new() { Goal = Goal, Project = Project, ExecutionMode = ExecutionMode,
        TimeoutSeconds = TimeoutSeconds, Steps = Steps };
}

/// <summary>Cookie APIs retain existing CSRF validation. OAuth clients use the MCP task tools.</summary>
[ApiController, Authorize, Route("api/agent/tasks")]
public sealed class AgentTaskController(AppDbContext db, AgentTaskService tasks) : ControllerBase
{
    private async Task<string> Owner(CancellationToken ct) => (await CurrentAccess.RequireAsync(User, db, false, ct)).Id;
    private static string Device(string? id) => Guid.TryParse(id, out _) ? id! : throw new ArgumentException("deviceId is required and must be a UUID.");
    [HttpPost]
    public async Task<IActionResult> Create(AgentTaskInput input, CancellationToken ct) =>
        Respond(await tasks.SendAsync(await Owner(ct), Device(input.DeviceId), "create", input.TaskId, input.ToPlan(), 0, 20, input.ParentTaskId, ct), accepted: true);
    [HttpPost("{taskId}/plan")]
    public async Task<IActionResult> Plan(string taskId, AgentTaskInput input, CancellationToken ct) =>
        Respond(await tasks.SendAsync(await Owner(ct), Device(input.DeviceId), "plan", taskId, input.ToPlan(), 0, 20, ct), accepted: true);
    [HttpGet("{taskId}")]
    public async Task<IActionResult> Get(string taskId, [FromQuery] string deviceId, CancellationToken ct) =>
        Respond(await tasks.SendAsync(await Owner(ct), Device(deviceId), "get", taskId, null, 0, 20, ct));
    [HttpGet("{taskId}/artifacts")]
    public async Task<IActionResult> Artifacts(string taskId, [FromQuery] string deviceId, CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 20) =>
        Respond(await tasks.SendAsync(await Owner(ct), Device(deviceId), "artifacts", taskId, null, offset, limit, ct), artifacts: true);
    [HttpPost("{taskId}/cancel")]
    public async Task<IActionResult> Cancel(string taskId, [FromQuery] string deviceId, CancellationToken ct) =>
        Respond(await tasks.SendAsync(await Owner(ct), Device(deviceId), "cancel", taskId, null, 0, 20, ct));
    private IActionResult Respond(RemoteTaskReply reply, bool accepted = false, bool artifacts = false)
    {
        if (reply.Error is not null) return StatusCode(reply.ErrorCode switch
        { "not_found" => 404, "forbidden" => 403, "invalid" => 400, "timeout" => 504, "offline" => 503, _ => 409 },
            new { error = reply.Error, code = reply.ErrorCode });
        if (artifacts) return Ok(new { task = reply.Task, artifacts = reply.Artifacts, nextOffset = reply.NextOffset });
        return StatusCode(accepted ? 202 : 200, reply.Task);
    }
}
