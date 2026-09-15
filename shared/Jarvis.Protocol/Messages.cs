using System.Text.Json;
namespace Jarvis.Protocol;

public sealed record ToolDescriptor(string Id, string Name, string Category, string Description,
    JsonElement InputSchema, bool ReadOnly, bool Sensitive = false);
public sealed record WireImage(string MimeType, string Base64);
public sealed record WidgetArtifact(string Title, string Html);
public sealed record ToolReply(string Text, bool IsError = false, IReadOnlyList<WireImage>? Images = null,
    WidgetArtifact? Widget = null)
{
    public static ToolReply Error(string message) => new(message, true);
}
public sealed record AgentHello(string DeviceId, string Version, string Platform, string MachineName,
    IReadOnlyList<ToolDescriptor> Tools)
{
    public int TaskProtocolVersion { get; init; }
}
public sealed record WireMessage(string Type)
{
    public int Version { get; init; } = 1;
    public string? Id { get; init; }
    public string? ToolId { get; init; }
    public string? SessionId { get; init; }
    public string? ThreadId { get; init; }
    public string? TurnId { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public JsonElement? Arguments { get; init; }
    public AgentHello? Hello { get; init; }
    public ToolReply? Result { get; init; }
    public long? Timestamp { get; init; }
    public string? TaskOperation { get; init; }
    public RemoteTaskRequest? TaskRequest { get; init; }
    public RemoteTaskReply? TaskReply { get; init; }
}
public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
