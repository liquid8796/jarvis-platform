using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Agent;

/// <summary>
/// The structured messages a lead and its teammates exchange through
/// SendMessage, ported from the reference's SendMessage union: a shutdown
/// request and its response, and the lead's answer to a plan-approval request.
/// A plain string message stays a plain message — this only covers the objects.
/// </summary>
public enum TeamProtocolKind
{
    ShutdownRequest,
    ShutdownResponse,
    PlanApprovalResponse,
}

/// <summary>One parsed protocol message; <see cref="Approve"/> is unset on a request.</summary>
public sealed record TeamProtocolMessage(
    TeamProtocolKind Kind,
    string? RequestId = null,
    bool? Approve = null,
    string? Reason = null,
    string? Feedback = null)
{
    /// <summary>The request-id caps the reference enforces on a response.</summary>
    public const int MaxRequestIdLength = 100 + 100;

    /// <summary>
    /// Reads the object form of SendMessage's `message`. Returns null when the
    /// value is not an object (a plain message), and sets <paramref name="error"/>
    /// when it is an object the union does not accept.
    /// </summary>
    public static TeamProtocolMessage? TryParse(JsonNode? message, out string? error)
    {
        error = null;
        if (message is not JsonObject payload)
            return null;

        var type = payload["type"]?.GetValue<string>();
        var kind = type switch
        {
            "shutdown_request" => TeamProtocolKind.ShutdownRequest,
            "shutdown_response" => TeamProtocolKind.ShutdownResponse,
            "plan_approval_response" => TeamProtocolKind.PlanApprovalResponse,
            _ => (TeamProtocolKind?)null,
        };
        if (kind is null)
        {
            error = type is null
                ? "A structured message needs a \"type\" of shutdown_request, shutdown_response or " +
                  "plan_approval_response."
                : $"Unknown message type '{type}'. Use shutdown_request, shutdown_response or " +
                  "plan_approval_response, or send plain text.";
            return null;
        }

        string? requestId = payload["request_id"]?.GetValue<string>();
        if (kind is TeamProtocolKind.ShutdownResponse or TeamProtocolKind.PlanApprovalResponse)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                error = "request_id must be the request id being responded to";
                return null;
            }

            if (requestId.Contains('\n') || requestId.Contains('\r'))
            {
                error = "request_id must be a single-line request id";
                return null;
            }

            if (requestId.Length > MaxRequestIdLength)
            {
                error = $"request id longer than any real one (max {MaxRequestIdLength} characters)";
                return null;
            }

            if (ReadApprove(payload) is not { } approve)
            {
                error = "approve must be true or false";
                return null;
            }

            return new TeamProtocolMessage(kind.Value, requestId, approve,
                payload["reason"]?.GetValue<string>(), payload["feedback"]?.GetValue<string>());
        }

        return new TeamProtocolMessage(kind.Value, Reason: payload["reason"]?.GetValue<string>());
    }

    private static bool? ReadApprove(JsonObject payload) => payload["approve"] switch
    {
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<string>(out var text) &&
            bool.TryParse(text, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>Renders the message the way it travels to the recipient's inbox.</summary>
    public string ToJson()
    {
        var payload = new JsonObject { ["type"] = TypeName(Kind) };
        if (RequestId is not null)
            payload["request_id"] = RequestId;
        if (Approve is { } approve)
            payload["approve"] = approve;
        if (Reason is not null)
            payload["reason"] = Reason;
        if (Feedback is not null)
            payload["feedback"] = Feedback;
        return payload.ToJsonString();
    }

    public static string TypeName(TeamProtocolKind kind) => kind switch
    {
        TeamProtocolKind.ShutdownRequest => "shutdown_request",
        TeamProtocolKind.ShutdownResponse => "shutdown_response",
        _ => "plan_approval_response",
    };
}

/// <summary>
/// The lead's side of the plan handshake: a teammate that finishes planning
/// raises a request here and waits for the lead's answer, which arrives in its
/// inbox as a plan_approval_response.
/// </summary>
public sealed class PlanApprovalRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private int _nextId;

    private sealed record PendingApproval(string AgentName, string Plan, DateTimeOffset RaisedAt);

    /// <summary>Records a teammate's request and returns its id.</summary>
    public string Raise(string agentName, string plan)
    {
        lock (_gate)
        {
            var id = $"plan-{++_nextId}";
            _pending[id] = new PendingApproval(agentName, plan, DateTimeOffset.Now);
            return id;
        }
    }

    /// <summary>The teammate a request belongs to, or null when the id is unknown.</summary>
    public string? OwnerOf(string requestId)
    {
        lock (_gate)
            return _pending.TryGetValue(requestId, out var pending) ? pending.AgentName : null;
    }

    /// <summary>Settles a request; false when nothing was waiting on that id.</summary>
    public bool Settle(string requestId)
    {
        lock (_gate)
            return _pending.Remove(requestId);
    }

    /// <summary>Requests still waiting, oldest first, as (id, agent, plan).</summary>
    public IReadOnlyList<(string Id, string AgentName, string Plan)> Pending()
    {
        lock (_gate)
        {
            return [.. _pending
                .OrderBy(entry => entry.Value.RaisedAt)
                .Select(entry => (entry.Key, entry.Value.AgentName, entry.Value.Plan))];
        }
    }
}

/// <summary>The result strings the handshakes produce, in the reference's wording.</summary>
public static class TeamProtocolMessages
{
    public static string ShutdownRequestSent(string target, string requestId) =>
        $"Shutdown request sent to {target}. Request ID: {requestId}";

    public static string ShutdownRequestFailed(string target) =>
        $"Failed to write the shutdown request to {target}'s inbox — nothing was sent.";

    public static string ShutdownApproved(string agentName, string confirmation) =>
        $"Shutdown approved. {confirmation} Agent {agentName} is now exiting.";

    public const string ShutdownConfirmationSent = "Sent confirmation to team-lead.";

    public const string ShutdownConfirmationFailed =
        "The confirmation could not be written to team-lead's inbox.";

    public const string ShutdownRejectionFailed =
        "Failed to write the shutdown rejection to team-lead's inbox — nothing was sent. Try again.";

    public static string ShutdownRejected(string reason) =>
        $"Shutdown rejected. Reason: \"{Truncate(reason, 50)}\". Continuing to work.";

    public const string PlanApprovalNotLeader =
        "Only the team lead can approve plans. Teammates cannot approve their own or other plans.";

    public static string PlanApproved(string agentName) =>
        $"Plan approved for {agentName}. They will receive the approval and can proceed with implementation.";

    public static string PlanApprovalFailed(string agentName) =>
        $"Failed to write the plan approval to {agentName}'s inbox — nothing was sent. Try again.";

    public static string PlanRejected(string agentName, string feedback) =>
        $"Plan rejected for {agentName} with feedback: \"{Truncate(feedback, 50)}\"";

    public static string PlanRejectionFailed(string agentName) =>
        $"Failed to write the plan rejection to {agentName}'s inbox — nothing was sent. Try again.";

    /// <summary>What a teammate is told after raising a plan for the lead.</summary>
    public static string PlanAwaitingLeader(string requestId, string? planFilePath = null) =>
        """
        Your plan has been submitted to the team lead for approval.

        Plan file: {PLAN}

        **What happens next:**
        1. Wait for the team lead to review your plan
        2. You will receive a message in your inbox with approval/rejection
        3. If approved, you can proceed with implementation
        4. If rejected, refine your plan based on the feedback

        **Important:** Do NOT proceed until you receive approval. Check your inbox for response.

        Request ID: {ID}
        """
        .Replace("{PLAN}", planFilePath ?? "(none)")
        .Replace("{ID}", requestId);

    /// <summary>The reference truncates a quoted reason rather than rejecting it.</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
