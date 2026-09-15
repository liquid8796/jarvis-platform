using Jarvis.Protocol;
namespace Jarvis.McpServer.Domain;

public sealed class Device
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string TokenHash { get; set; } = "";
    public long TokenExpiresAt { get; set; }
    public string CapabilitiesJson { get; set; } = "[]";
    public string Platform { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long LastSeenAt { get; set; }
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
}
public sealed class ToolEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string AgentToolId { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Enabled { get; set; }
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
}
public sealed class AuditEvent
{
    public long Id { get; set; }
    public long Time { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public string UserId { get; set; } = "";
    public string? DeviceId { get; set; }
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? CorrelationId { get; set; }
    public long DurationMs { get; set; }
}
public sealed record DeviceView(string Id, string Name, bool Enabled, bool Online, string Platform,
    string AgentVersion, long LastSeenAt, int ToolCount, string Revision);
public sealed record EnrollmentResult(string DeviceId, string ServerUrl, string Token);
public sealed record UserView(string Id, string Email, string DisplayName, string Role, string Status);
public interface IAgentRouter
{
    bool IsOnline(string ownerId, string deviceId);
    Task<ToolReply> CallAsync(string ownerId, string deviceId, string toolId,
        System.Text.Json.JsonElement arguments, string sessionId, CancellationToken cancellationToken);
    void DisconnectDevice(string deviceId);
    void DisconnectUser(string ownerId);
}
public interface IAuditWriter
{
    Task WriteAsync(AuditEvent entry, CancellationToken cancellationToken = default);
}
