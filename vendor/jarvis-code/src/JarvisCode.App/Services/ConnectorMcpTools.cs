using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>One MCP server the user already has configured, as list_connectors renders it.</summary>
/// <param name="Url">
/// Where the server lives, when it is a remote one. The reference's rows carry
/// the connector's directory URL; a stdio server configured here has none, and
/// the field is null rather than filled with something that is not a URL.
/// </param>
public sealed record InstalledConnector(
    string Name, string? Description, bool Connected, int ToolCount, string? Url = null);

/// <summary>
/// The reference desktop's <c>mcp-registry</c> in-process MCP server:
/// search_mcp_registry queries the public registry, list_connectors renders what
/// the user already has, and suggest_connectors offers one to add. Docs and
/// schemas are the reference's own (desktop 1.40609.0.0,
/// <c>index2.chunk-CEBgETf7.js</c>).
///
/// The reference's cards are claude.ai connector cards; here they are the app's
/// own Connectors page, which is where a connector is actually added — the two
/// tools render a card and the user's click happens out of band either way.
/// </summary>
public static class ConnectorMcpTools
{
    public static InternalMcpServerDefinition Server(
        McpRegistrySearchTool search,
        Func<IReadOnlyList<InstalledConnector>> installed,
        Action<IReadOnlyList<string>> showSuggestions) =>
        // The reference's order: search, suggest, list.
        new(InternalMcpServerNames.McpRegistry, [search, .. Create(installed, showSuggestions)])
        {
            // The reference's own predicate is `JD().type !== "3p"`: a session
            // running on a third-party provider gets no connector registry,
            // because the directory it searches is the account's. The local
            // equivalent is the same question about this session's provider.
            IsEnabled = static context =>
                context.SessionType == InternalMcpSessionContext.CodeSessionType &&
                !context.IsThirdPartyProvider,

            // search_mcp_registry was registered bare here as well until this
            // server took it; the old name stays resolvable so a stored session
            // replays, and is never advertised.
            LegacyNames = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["search_mcp_registry"] = ["search_mcp_registry"],
            },
        };

    /// <summary>The connector card's note, both of the reference's variants.</summary>
    public const string CardRendered =
        "Connector card rendered above. Any lead-in goes before this call; skip re-listing the connectors " +
        "in text.";

    public const string NoConnectors =
        "No installed connectors found — the card did not render. Offer to search the registry " +
        "(search_mcp_registry).";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static IReadOnlyList<ITool> Create(
        Func<IReadOnlyList<InstalledConnector>> installed,
        Action<IReadOnlyList<string>> showSuggestions) =>
    [
        McpToolBuilder.Tool(
            "suggest_connectors",
            "Display connector suggestions to the user with Connect buttons. Call this:\n- After " +
            "search_mcp_registry when it returned connectors that are not yet connected or whose tools are " +
            "disabled in chat, and would help with the user's task\n- When a tool call fails with an " +
            "authentication or credential error — pass the server UUID from the failed tool name (format: " +
            "mcp__{uuid}__{toolName}) so the user can re-authenticate\n\nDo NOT call this if:\n- The connector is " +
            "already connected and working (just use it directly)\n- None of the search results are relevant to " +
            "what the user needs",
            CapturedMcpSchemas.Schema(InternalMcpServerNames.McpRegistry, "suggest_connectors"),
            isReadOnly: false,
            (args, _) =>
            {
                var uuids = Strings(args, "uuids");
                if (uuids.Count == 0)
                {
                    return ToolResult.Error("uuids is required and cannot be empty.");
                }

                showSuggestions(uuids);

                // The reference answers with JSON, not prose: the card is what
                // the user sees, and this is the model's record of what it
                // asked for.
                // The reference's row: name, description, url, iconUrl,
                // directoryUuid. Here a connector is identified by its
                // configured name, so that name is both.
                var known = installed().ToDictionary(static c => c.Name, StringComparer.OrdinalIgnoreCase);
                return ToolResult.Success(new JsonObject
                {
                    ["connectors"] = new JsonArray([.. uuids.Select(uuid => (JsonNode)new JsonObject
                    {
                        ["name"] = uuid,
                        ["description"] = known.TryGetValue(uuid, out var c) ? c.Description : null,
                        ["url"] = c?.Url,
                        ["iconUrl"] = null,
                        ["directoryUuid"] = uuid,
                    })]),
                    ["keywords"] = new JsonArray([.. Keywords(args).Select(static k => (JsonNode)k)]),
                }.ToJsonString(Indented));
            },
            static args => $"suggest_connectors({Strings(args, "uuids").Count})"),

        McpToolBuilder.Tool(
            "list_connectors",
            "Render the user's installed connectors as an interactive card. Call this when the user asks what " +
            "connectors they have; pass keywords to filter. To suggest a connector for the user to add, use " +
            "suggest_connectors instead.",
            CapturedMcpSchemas.Schema(InternalMcpServerNames.McpRegistry, "list_connectors"),
            isReadOnly: true,
            (args, _) =>
            {
                var keywords = Keywords(args);
                var rows = installed()
                    .Where(c => keywords.Count == 0 || keywords.Any(k =>
                        c.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                        (c.Description?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false)))
                    .ToList();

                // The reference's shape: the connectors, the keywords it
                // filtered by, and a note picked by whether the card rendered.
                return ToolResult.Success(new JsonObject
                {
                    ["connectors"] = new JsonArray([.. rows.Select(static c => (JsonNode)new JsonObject
                    {
                        ["name"] = c.Name,
                        ["description"] = c.Description,
                        ["url"] = c.Url,
                        ["iconUrl"] = null,
                        ["directoryUuid"] = c.Name,
                        ["connected"] = c.Connected,
                    })]),
                    ["keywords"] = new JsonArray([.. keywords.Select(static k => (JsonNode)k)]),
                    ["note"] = rows.Count > 0 ? CardRendered : NoConnectors,
                }.ToJsonString(Indented));
            },
            static _ => "list_connectors()"),
    ];

    private static List<string> Keywords(JsonObject args) => Strings(args, "keywords");

    private static List<string> Strings(JsonObject args, string key) =>
    [
        .. (args[key] as JsonArray ?? [])
            .Select(static v => v?.GetValue<string>())
            .Where(static v => !string.IsNullOrWhiteSpace(v))
            .Select(static v => v!.Trim()),
    ];
}
