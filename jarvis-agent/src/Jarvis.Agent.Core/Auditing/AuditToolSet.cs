using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Auditing;

public sealed class AuditToolSet(IAgentAuditReader reader)
{
    public const string QueryId = "audit.query";
    public IReadOnlyList<IAgentTool> Tools { get; } = [new QueryTool(reader)];
    public static IReadOnlyList<ToolDescriptor> Descriptors { get; } = [DescribeQuery()];

    private sealed class QueryTool(IAgentAuditReader reader) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = DescribeQuery();

        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = arguments.TryGetProperty("limit", out var limitNode) ? limitNode.GetInt32() : 100;
            var code = Optional(arguments, "code");
            var toolId = Optional(arguments, "tool_id");
            var allSessions = arguments.TryGetProperty("all_sessions", out var allNode) && allNode.GetBoolean();
            if (allSessions && !context.FullPermission)
                return Task.FromResult(ToolReply.Error("all_sessions requires local Full permission for audit.query."));
            var records = reader.Query(limit, code, toolId, allSessions ? null : context.SessionId);
            return Task.FromResult(new ToolReply(JsonSerializer.Serialize(new { records }, WireJson.Options)));
        }
    }

    private static ToolDescriptor DescribeQuery() => new(
        QueryId,
        "audit_query",
        "audit",
        "Read recent reason-coded Jarvis Agent audit records. Payloads and authentication data are not logged. The default scope is the current session; all_sessions requires local Full permission.",
        WireJson.Element(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["limit"] = new { type = "integer", minimum = 1, maximum = 500, @default = 100 },
                ["code"] = new { type = "string", minLength = 1, maxLength = 80 },
                ["tool_id"] = new { type = "string", minLength = 1, maxLength = 256 },
                ["all_sessions"] = new { type = "boolean", @default = false }
            },
            additionalProperties = false
        }),
        ReadOnly: true,
        Sensitive: true);

    private static string? Optional(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
}
