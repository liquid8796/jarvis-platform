using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jarvis.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteTaskStep
{
    public string Id { get; init; } = "";
    public string ToolId { get; init; } = "";
    public JsonElement Arguments { get; init; } = WireJson.Element(new { });
    public string Stage { get; init; } = "EXECUTE";
    public int TimeoutSeconds { get; init; } = 120;
    public int MaxAttempts { get; init; } = 1;
    public string? ExpectedText { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteTaskPlan
{
    public string Goal { get; init; } = "";
    public string? Project { get; init; }
    public string ExecutionMode { get; init; } = "NORMAL";
    public int TimeoutSeconds { get; init; } = 1800;
    public IReadOnlyList<RemoteTaskStep> Steps { get; init; } = [];
}

public sealed record RemoteTaskRequest(string OwnerId, string TaskId, RemoteTaskPlan? Plan = null,
    int Offset = 0, int Limit = 20);
public sealed record RemoteTaskSnapshot(string TaskId, string Goal, string Project, string Status,
    string? CurrentStep, int CompletedSteps, int TotalSteps, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? Error = null);
public sealed record RemoteTaskArtifact(int Sequence, string StepId, string Stage, string ToolId,
    int Attempt, bool Success, string Output, bool Truncated, int? ExitCode, DateTimeOffset CreatedAt,
    string? Error = null);
public sealed record RemoteTaskReply(RemoteTaskSnapshot? Task = null,
    IReadOnlyList<RemoteTaskArtifact>? Artifacts = null, int? NextOffset = null,
    string? Error = null, string? ErrorCode = null)
{
    public static RemoteTaskReply Failure(string code, string message) => new(Error: message, ErrorCode: code);
}

public static partial class RemoteTaskRules
{
    public const int ProtocolVersion = 1;
    public const int MaxSteps = 32;
    public const int OutputLimit = 16000;
    public const int MaxPlanBytes = 128 * 1024;
    public static readonly string[] Operations = ["create", "plan", "get", "artifacts", "cancel"];
    public static string McpName(string operation) => "agent_task_" + operation;
    public static bool IsReservedName(string name) => name.StartsWith("agent_task_", StringComparison.Ordinal);
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    public static string TaskId(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id.ToString("N") : throw new ArgumentException("taskId must be a non-empty UUID.");

    public static void Validate(RemoteTaskPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.Goal) || plan.Goal.Length > 8000)
            throw new ArgumentException("goal must contain 1..8000 characters.");
        if (plan.Project?.Length > 1024 || plan.TimeoutSeconds is < 1 or > 3600 ||
            plan.ExecutionMode is not ("READ_ONLY" or "NORMAL" or "AUTONOMOUS"))
            throw new ArgumentException("Invalid project, executionMode or task timeout (1..3600 seconds).");
        if (plan.Steps is null || plan.Steps.Count > MaxSteps)
            throw new ArgumentException("A plan may contain at most 32 steps.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var stage = -1;
        string[] stages = ["EXECUTE", "BUILD", "TEST", "PACKAGE", "VERIFY"];
        foreach (var step in plan.Steps)
        {
            if (step is null || string.IsNullOrEmpty(step.Id) || !Identifier().IsMatch(step.Id) || !ids.Add(step.Id) ||
                string.IsNullOrEmpty(step.ToolId) || !Identifier().IsMatch(step.ToolId) ||
                step.Arguments.ValueKind != JsonValueKind.Object || step.TimeoutSeconds is < 1 or > 1800 ||
                step.MaxAttempts is < 1 or > 3 || step.ExpectedText?.Length > 1000)
                throw new ArgumentException("Invalid step ID, tool, arguments, timeout or attempt limit.");
            var next = Array.IndexOf(stages, step.Stage);
            if (next < stage || next < 0) throw new ArgumentException("Stages must follow EXECUTE, BUILD, TEST, PACKAGE, VERIFY order.");
            stage = next;
        }
        if (JsonSerializer.SerializeToUtf8Bytes(plan, WireJson.Options).Length > MaxPlanBytes)
            throw new ArgumentException("Task plan exceeds 128 KiB.");
    }

    public static bool IsTerminal(string status) => status is "COMPLETED" or "FAILED" or "CANCELLED" or "INTERRUPTED";
}
