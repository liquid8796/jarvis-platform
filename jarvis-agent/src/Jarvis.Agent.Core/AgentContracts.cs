using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Protocol;
namespace Jarvis.Agent.Core;

public sealed record AgentOptions(string ServerUrl, string DeviceId, string Workspace,
    bool AllowLoopbackHttp = false, IReadOnlyList<string>? AdditionalDirectories = null)
{
    public AgentExecutionSettings ExecutionSettings { get; init; } = new();
    public Uri ValidateAndGetWebSocketUri()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != "https" && !(AllowLoopbackHttp && uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("Use an HTTPS server URL. HTTP is allowed only for explicit loopback development.");
        if (!Guid.TryParse(DeviceId, out _)) throw new ArgumentException("Invalid device ID.");
        _ = new WorkspaceDirectories(Workspace, AdditionalDirectories);
        ExecutionSettings.Validate();
        return new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws", Path = "/agent/connect" }.Uri;
    }
}
public sealed record AgentExecutionContext(string Workspace, string CallId, string SessionId)
{
    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];
    public string? ThreadId { get; init; }
    public string? TurnId { get; init; }
    public string? OwnerId { get; init; }
    public string? AgentDeviceId { get; init; }
    public long? WorkspaceRevision { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationToken SessionCancellation { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<IDisposable>? RetainResources { get; init; }
    public bool HasExplicitSession => AgentSessionRules.IsSessionId(SessionId);
    public bool IsSessionlessExecution => AgentSessionRules.IsEphemeralExecutionId(SessionId);
    public string IsolationScopeId => IsSessionlessExecution ? SessionlessIsolationScope(OwnerId, AgentDeviceId) : SessionId;
    public static string SessionlessIsolationScope(string? ownerId, string? deviceId)
    {
        var material = Encoding.UTF8.GetBytes((ownerId ?? "legacy-owner") + "\n" + (deviceId ?? "legacy-device"));
        var hash = SHA256.HashData(material);
        return "anon_" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
    public AgentSessionIdentity? TrySessionIdentity() =>
        !string.IsNullOrWhiteSpace(OwnerId) && !string.IsNullOrWhiteSpace(AgentDeviceId) && HasExplicitSession
            ? new(OwnerId, AgentDeviceId, SessionId) : null;
    public AgentSessionIdentity RequireSessionIdentity() => TrySessionIdentity() ??
        throw new AgentRequestException("SESSION_REQUIRED", "Open a session with session__open and use its sessionHandle.");
    // Set by the local dispatcher, never taken from remote arguments or the wire envelope.
    public bool FullPermission { get; init; }
}
public interface IAgentTool
{
    ToolDescriptor Descriptor { get; }
    Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken);
}
/// <summary>Marker for orchestrators whose own execution slots must be released before nested guarded tool calls.</summary>
public interface ICompositeAgentTool : IAgentTool { }
public interface IApprovalService
{
    Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken);
}
public interface IArtifactSink
{
    Task ShowAsync(WidgetArtifact artifact, CancellationToken cancellationToken);
}
public sealed record AgentEvent(DateTimeOffset Time, string Kind, string Message);
public sealed record AgentReachabilityStatus(string Code, string Detail, bool Online);