using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows;

public enum BrowserFamily
{
    Auto,
    Dev,
    Chrome,
    Edge,
    Extension
}

public static class BrowserFamilyRouting
{
    public static BrowserFamily Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => BrowserFamily.Auto,
        "dev" or "iab" => BrowserFamily.Dev,
        "chrome" => BrowserFamily.Chrome,
        "edge" => BrowserFamily.Edge,
        "extension" or "external" => BrowserFamily.Extension,
        _ => throw new ArgumentException("browserFamily must be auto, dev, chrome, edge, or extension.")
    };

    public static BrowserFamily Resolve(BrowserFamily requested, JsonObject arguments)
    {
        if (requested != BrowserFamily.Auto) return requested;
        var url = arguments["url"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(
                url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url,
                UriKind.Absolute, out var uri) &&
            (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            return BrowserFamily.Dev;
        return BrowserFamily.Extension;
    }

    public static string WireName(this BrowserFamily family) => family switch
    {
        BrowserFamily.Dev => "dev",
        BrowserFamily.Chrome => "chrome",
        BrowserFamily.Edge => "edge",
        BrowserFamily.Extension => "extension",
        _ => "auto"
    };
}

public sealed record BrowserRuntimeContext(
    string CallId,
    string? ApplicationSessionId,
    string IsolationScopeId,
    string WorkingDirectory,
    IReadOnlyList<string> AdditionalDirectories,
    bool FullPermission,
    string BrowserFamily);

public sealed record BrowserRuntimeRequest(
    string ToolId,
    JsonElement Arguments,
    BrowserRuntimeContext Context);

public sealed record BrowserRuntimeReply(
    string Text,
    bool IsError,
    IReadOnlyList<WireImage>? Images = null);

public sealed record BrowserRuntimeHandshake(
    int ProtocolVersion,
    string ServiceVersion,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> BrowserFamilies);

internal static class BrowserRuntimeProtocol
{
    public const int Version = 1;
    public static readonly string[] Capabilities =
    [
        "tool-proxy-v1",
        "session-isolation-v1",
        "browser-family-routing-v1",
        "isolated-dev-profile-v1",
        "frontend-verification-v1"
    ];

    public static JsonSerializerOptions Json { get; } = new(WireJson.Options)
    {
        PropertyNameCaseInsensitive = true
    };
}
