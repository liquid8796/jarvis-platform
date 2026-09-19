using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
        var url = arguments["url"]?.GetValue<string>() ?? arguments["spec"]?["url"]?.GetValue<string>();
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

/// <summary>Browser IDs are local to a connection; ambiguous numeric IDs require an explicit family.</summary>
public sealed partial class BrowserSessionRouting
{
    private readonly object _sync = new();
    private readonly Dictionary<int, HashSet<BrowserFamily>> _tabs = [];
    private BrowserFamily? _selected;

    public BrowserFamily Resolve(BrowserFamily requested, JsonObject args)
    {
        if (requested != BrowserFamily.Auto) return requested;
        lock (_sync)
        {
            var tab = args["tabId"]?.GetValue<int>() ?? args["tab_id"]?.GetValue<int>();
            if (tab is { } id && _tabs.TryGetValue(id, out var families))
                return families.Count == 1 ? families.Single() : throw new InvalidOperationException(
                    "Ambiguous browser tab ID. Specify browserFamily for this tab.");
            if (args["url"] is not null || args["spec"]?["url"] is not null)
                return BrowserFamilyRouting.Resolve(requested, args);
            return _selected ?? BrowserFamily.Extension;
        }
    }

    public void Observe(BrowserFamily family, string tool, JsonElement args, string output)
    {
        lock (_sync)
        {
            if (tool is not "browser.list_connected_browsers") _selected = family;
            int? tab = args.TryGetProperty("tabId", out var id) && id.TryGetInt32(out var number) ? number : null;
            if (tool == "browser.tabs_close_mcp" && tab is { } closed)
            {
                if (_tabs.TryGetValue(closed, out var owners) && owners.Remove(family) && owners.Count == 0) _tabs.Remove(closed);
                return;
            }
            if (tool == "browser.qa") return; // QA closes its private tab before returning.
            if (tab is { } existing) Remember(existing, family);
            if (tool is "browser.navigate" or "browser.tabs_create_mcp" or "browser.tabs_context_mcp")
                foreach (Match match in TabPattern().Matches(output))
                    if (int.TryParse(match.Groups[1].Value, out var found)) Remember(found, family);
        }
    }

    private void Remember(int tab, BrowserFamily family)
    {
        if (!_tabs.TryGetValue(tab, out var values)) _tabs[tab] = values = [];
        values.Add(family);
    }

    [GeneratedRegex(@"(?:\btab\s+|(?m:^\[))(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TabPattern();
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
        "frontend-verification-v1",
        "structured-frontend-qa-v1"
    ];

    public static JsonSerializerOptions Json { get; } = new(WireJson.Options)
    {
        PropertyNameCaseInsensitive = true
    };
}
