using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// search_mcp_registry — the reference's tool doc, schema and JSON answer over
/// this build's own source. The reference searches the claude.ai connector
/// directory, which is account-bound; this searches the public MCP registry
/// (registry.modelcontextprotocol.io), so the rows are what that registry knows
/// and the two fields it has no notion of come back empty (see below). The
/// divergence is declared in Deltas/reference-surface-deltas.tsv.
/// </summary>
public sealed class McpRegistrySearchTool(
    HttpClient http, Func<IReadOnlyList<InstalledConnector>>? installed = null) : ITool
{
    public const string BaseUrl = "https://registry.modelcontextprotocol.io/v0/servers";

    /// <summary>The reference caps the answer at ten rows.</summary>
    public const int MaxResults = 10;

    /// <summary>…and each row's tool list at eight, with the rest counted.</summary>
    public const int MaxToolNames = 8;

    public string Name => "search_mcp_registry";

    public string Description =>
        "Search for available connectors in the MCP registry. Call this when connecting to a new MCP might " +
        "help resolve the user query.\n\nExamples:\n" +
        "- \"check my Asana tasks\" → search [\"asana\", \"tasks\", \"todo\"]\n" +
        "- \"find issues in Jira\" → search [\"jira\", \"issues\"]\n" +
        "- \"help me manage my tasks\" → search [\"tasks\", \"todo\", \"project management\"]\n" +
        "- \"did the call cover Mike's latest ticket\" → thinking: \"I don't have any context about the call " +
        "or meeting, let's see if there are any connectors available\" → search [\"meeting\", \"gong\", " +
        "\"meet\", \"zoom\"]\n\n" +
        "Returns results with connected status. Call suggest_connectors to show unconnected ones to the user.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.McpRegistry, "search_mcp_registry");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"SearchMcpRegistry({string.Join(", ", Keywords(arguments))})";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var keywords = Keywords(arguments);
        if (keywords.Count == 0)
        {
            return ToolResult.Error("keywords is required and cannot be empty.");
        }

        // The registry takes one search term, so the keywords are tried in order
        // and the first that matches anything answers — the reference's own
        // directory search takes the whole list at once.
        JsonArray servers = [];
        foreach (var keyword in keywords)
        {
            string body;
            try
            {
                using var response = await http.GetAsync(
                    $"{BaseUrl}?search={Uri.EscapeDataString(keyword)}&limit=20", cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return ToolResult.Error($"The MCP registry answered HTTP {(int)response.StatusCode}.");
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return ToolResult.Error($"Could not reach the MCP registry: {ex.Message}");
            }

            try
            {
                servers = (JsonNode.Parse(body) as JsonObject)?["servers"] as JsonArray ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return ToolResult.Error("The MCP registry returned an unreadable response.");
            }

            if (servers.Count > 0)
            {
                break;
            }
        }

        var connected = (installed?.Invoke() ?? [])
            .Where(static c => c.Connected)
            .Select(static c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ToolResult.Success(context.Truncate(Render(servers, connected), "registry results"));
    }

    /// <summary>
    /// The reference's answer shape, verbatim:
    /// <c>{"results":[{name, description, tools, url, iconUrl, directoryUuid,
    /// connected, enabledInChat}]}</c>, ten rows at most and eight tool names
    /// per row with a <c>"+N more"</c> tail.
    ///
    /// Two of those fields the public registry does not have: it publishes no
    /// tool list and no icon, so <c>tools</c> comes back empty and
    /// <c>iconUrl</c> null rather than being invented. <c>directoryUuid</c> is
    /// the registry's own server name, which is what identifies a server here
    /// and what suggest_connectors takes back.
    /// </summary>
    internal static string Render(JsonArray servers, IReadOnlySet<string> connectedNames)
    {
        JsonArray results = [];
        foreach (var entryNode in servers.OfType<JsonObject>())
        {
            if (results.Count == MaxResults)
            {
                break;
            }

            // v0 entries either carry the fields at the top level or nest them under "server".
            var entry = entryNode["server"] as JsonObject ?? entryNode;
            var name = JsonArgs.GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var url = (entry["remotes"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(static remote => JsonArgs.GetString(remote, "url"))
                .FirstOrDefault(static u => !string.IsNullOrWhiteSpace(u));

            // A stdio-only server has no URL; the package is how it is added, so
            // it stands in for one rather than leaving the row unactionable.
            url ??= (entry["packages"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(static package =>
                    JsonArgs.GetString(package, "identifier") ?? JsonArgs.GetString(package, "name"))
                .FirstOrDefault(static i => !string.IsNullOrWhiteSpace(i));

            var isConnected = connectedNames.Contains(name);
            results.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = JsonArgs.GetString(entry, "description"),
                ["tools"] = new JsonArray(),
                ["url"] = url,
                ["iconUrl"] = null,
                ["directoryUuid"] = name,
                ["connected"] = isConnected,
                ["enabledInChat"] = isConnected,
            });
        }

        return new JsonObject { ["results"] = results }.ToJsonString();
    }

    private static List<string> Keywords(JsonObject arguments) =>
    [
        .. (arguments["keywords"] as JsonArray ?? [])
            .Select(static v => v?.GetValue<string>())
            .Where(static v => !string.IsNullOrWhiteSpace(v))
            .Select(static v => v!.Trim()),
    ];
}
