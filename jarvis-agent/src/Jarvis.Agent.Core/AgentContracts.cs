using System.Text.Json;
using Jarvis.Protocol;
namespace Jarvis.Agent.Core;

public sealed record AgentOptions(string ServerUrl, string DeviceId, string Workspace,
    bool AllowLoopbackHttp = false, IReadOnlyList<string>? AdditionalDirectories = null)
{
    public Uri ValidateAndGetWebSocketUri()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != "https" && !(AllowLoopbackHttp && uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("Use an HTTPS server URL. HTTP is allowed only for explicit loopback development.");
        if (!Guid.TryParse(DeviceId, out _)) throw new ArgumentException("Invalid device ID.");
        _ = new WorkspaceDirectories(Workspace, AdditionalDirectories);
        return new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws", Path = "/agent/connect" }.Uri;
    }
}
public sealed record AgentExecutionContext(string Workspace, string CallId, string SessionId)
{
    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];
    public string? ThreadId { get; init; }
    public string? TurnId { get; init; }
    // Set by the local dispatcher, never taken from remote arguments or the wire envelope.
    public bool FullPermission { get; init; }
}
public interface IAgentTool
{
    ToolDescriptor Descriptor { get; }
    Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken);
}
public interface IApprovalService
{
    Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken);
}
public interface IArtifactSink
{
    Task ShowAsync(WidgetArtifact artifact, CancellationToken cancellationToken);
}
public sealed record AgentEvent(DateTimeOffset Time, string Kind, string Message);
