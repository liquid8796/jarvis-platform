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
    public FrontendQaSpec? VerificationSpec { get; init; }
    public CodingVerificationSpec? CodingVerification { get; init; }
    public RemoteTaskPlan ToPlan() => new() { Goal = Goal, Project = Project, ExecutionMode = ExecutionMode,
        TimeoutSeconds = TimeoutSeconds, Steps = Steps, VerificationSpec = VerificationSpec, CodingVerification = CodingVerification };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentTaskWorkflowInput
{
    public string? DeviceId { get; init; }
    public string AttemptId { get; init; } = "";
    public RemoteTaskPlan? Plan { get; init; }
    public FrontendQaSpec? VerificationSpec { get; init; }
    public FrontendQaVisualReview? VisualReview { get; init; }
    public CodingVerificationSpec? CodingVerification { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentTaskContextInput(string DeviceId, IReadOnlyList<string>? Paths = null);

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
    [HttpPost("{taskId}/{operation:regex(^(verify|repair|review|complete)$)}")]
    public async Task<IActionResult> Workflow(string taskId, string operation, AgentTaskWorkflowInput input, CancellationToken ct) =>
        Respond(await tasks.SendAsync(await Owner(ct), Device(input.DeviceId), operation, taskId, input.Plan, 0, 20, null, ct,
            attemptId: input.AttemptId, verificationSpec: input.VerificationSpec, visualReview: input.VisualReview,
            codingVerification: input.CodingVerification), accepted: true);
    [HttpGet("{taskId}/events")]
    public async Task<IActionResult> Events(string taskId, [FromQuery] string deviceId, CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 20) =>
        Inspection(await tasks.SendAsync(await Owner(ct), Device(deviceId), "events", taskId, null, offset, limit, ct));
    [HttpGet("{taskId}/reports/{artifactId}")]
    public async Task<IActionResult> Report(string taskId, string artifactId, [FromQuery] string deviceId, CancellationToken ct,
        [FromQuery] int offset = 0) =>
        Inspection(await tasks.SendAsync(await Owner(ct), Device(deviceId), "report", taskId, null, offset, 20, null, ct, artifactId: artifactId));
    [HttpPost("{taskId}/context")]
    public async Task<IActionResult> Context(string taskId, AgentTaskContextInput input, CancellationToken ct) =>
        Inspection(await tasks.SendAsync(await Owner(ct), Device(input.DeviceId), "context", taskId, null, 0, 20, null, ct, contextPaths: input.Paths));
    [HttpGet("{taskId}/captures/{captureId}")]
    public async Task<IActionResult> Capture(string taskId, string captureId, [FromQuery] string deviceId, CancellationToken ct)
    {
        var reply = await tasks.SendAsync(await Owner(ct), Device(deviceId), "capture", taskId, null, 0, 20, null, ct, captureId: captureId);
        return reply.Error is not null ? Respond(reply) : reply.Images is { Count: 1 } images
            ? File(Convert.FromBase64String(images[0].Base64), images[0].MimeType) : StatusCode(409, new { error = "Capture unavailable." });
    }
    private IActionResult Respond(RemoteTaskReply reply, bool accepted = false, bool artifacts = false)
    {
        if (reply.Error is not null) return StatusCode(reply.ErrorCode switch
        { "not_found" => 404, "forbidden" => 403, "invalid" => 400, "timeout" => 504, "offline" => 503, _ => 409 },
            new { error = reply.Error, code = reply.ErrorCode });
        if (artifacts) return Ok(new { task = reply.Task, artifacts = reply.Artifacts, nextOffset = reply.NextOffset });
        return StatusCode(accepted ? 202 : 200, reply.Task);
    }
    private IActionResult Inspection(RemoteTaskReply reply) => reply.Error is null ? Ok(reply) : Respond(reply);
}
