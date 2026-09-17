namespace Jarvis.Protocol;

/// <summary>Authenticated application-session identity; client transport identity is deliberately separate.</summary>
public sealed record AgentSessionIdentity(string OwnerId, string DeviceId, string SessionId);

public static class AgentSessionRules
{
    public const string Capability = "application-sessions-v1";
    public const string Prefix = "js_";
    public const string EphemeralPrefix = "call_";
    public static bool IsSessionId(string? value) => value is { Length: 35 } &&
        value.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(value[Prefix.Length..], "N", out _);
    public static bool IsEphemeralExecutionId(string? value) => value is { Length: 37 } &&
        value.StartsWith(EphemeralPrefix, StringComparison.Ordinal) && Guid.TryParseExact(value[EphemeralPrefix.Length..], "N", out _);
    public static string NewSessionId() => Prefix + Guid.NewGuid().ToString("N");
    public static string NewEphemeralExecutionId() => EphemeralPrefix + Guid.NewGuid().ToString("N");
    public static bool IsTool(string toolId) => toolId is "session.open" or "session.get" or "session.list" or
        "session.send_message" or "session.read_events" or "session.stop_work" or "session.close" or "workspace.get" or "workspace.set";
    public static bool IsPublicTool(string name) => name is "session__open" or "session__get" or "session__list" or
        "session__send_message" or "session__read_events" or "session__stop_work" or "session__close" or "workspace__get" or "workspace__set";
    public static bool IsControlTool(string toolId) => IsTool(toolId) || toolId is "process.read" or "process.cancel";
    public static bool AllowedWhilePaused(string toolId) => toolId is "session.get" or "session.list" or
        "session.read_events" or "session.stop_work" or "session.close" or "workspace.get" or "process.read" or "process.cancel";
}

public sealed record AgentSessionSnapshot(
    string SessionId, string DeviceId, string Label, string Workspace, IReadOnlyList<string> AdditionalDirectories,
    long WorkspaceRevision, DateTimeOffset CreatedAt, DateTimeOffset LastActiveAt, DateTimeOffset? ClosedAt,
    string? ParentSessionId, long UnreadEvents)
{
    public string Status => ClosedAt is null ? "idle" : "closed";
}

public sealed record AgentSessionEvent(long Cursor, string Kind, string? SenderSessionId,
    DateTimeOffset CreatedAt, string Text, string Source = "agent-coordination-data");
public sealed record AgentSessionEventPage(IReadOnlyList<AgentSessionEvent> Events, long NextCursor, bool HasMore, bool Truncated);

public sealed class AgentRequestException(string code, string message) : InvalidOperationException(code + ": " + message)
{
    public string Code { get; } = code;
}