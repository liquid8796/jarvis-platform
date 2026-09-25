using System.Text.Json;
using Jarvis.McpServer.Domain;
using Jarvis.Protocol;
using ModelContextProtocol.Protocol;

namespace Jarvis.McpServer.Transport;

internal static class DynamicAgentToolGateway
{
    public static JsonElement SearchInputSchema { get; } = WireJson.Element(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", maxLength = 200, description = "Optional text matched against canonical ID, public name and description." },
            category = new { type = "string", maxLength = 64, description = "Optional exact tool category filter." },
            limit = new { type = "integer", minimum = 1, maximum = 50, description = "Maximum results; defaults to 20." }
        },
        additionalProperties = false
    });

    public static JsonElement CallInputSchema { get; } = WireJson.Element(new
    {
        type = "object",
        properties = new
        {
            toolId = new { type = "string", minLength = 1, maxLength = 100, description = "Canonical installed tool ID returned by jarvis__tool_search." },
            arguments = new { type = "object", description = "Arguments matching the live installed tool schema.", additionalProperties = true }
        },
        required = new[] { "toolId", "arguments" },
        additionalProperties = false
    });

    public static IReadOnlyList<Tool> List(bool scoped, McpSessionContext sessions) =>
    [
        Create(
            DynamicAgentToolNames.Search,
            "Search the selected Jarvis Agent's live visible capability catalog by canonical ID, public name, category or description. Use this when a newly added Agent tool is not yet present as a direct MCP tool in the current chat.",
            scoped ? sessions.AugmentSchema(SearchInputSchema, required: false) : SearchInputSchema,
            McpOutputSchemas.DynamicToolSearch,
            readOnly: true),
        Create(
            DynamicAgentToolNames.Call,
            "Invoke a live visible Jarvis Agent capability by canonical tool ID. This is the permanent fallback for tools added after a chat started; local Arm, permission, approval, schema and execution-resource checks are unchanged.",
            scoped ? sessions.AugmentSchema(CallInputSchema, required: false) : CallInputSchema,
            McpOutputSchemas.ToolReply,
            readOnly: false)
    ];

    public static bool IsSearch(string name) => StringComparer.Ordinal.Equals(name, DynamicAgentToolNames.Search);
    public static bool IsCall(string name) => StringComparer.Ordinal.Equals(name, DynamicAgentToolNames.Call);

    private static Tool Create(string name, string description, JsonElement input, JsonElement output, bool readOnly)
    {
        var title = McpToolMetadata.TitleFor(name);
        return new Tool
        {
            Name = name,
            Title = title,
            Description = description,
            InputSchema = input,
            OutputSchema = output,
            Annotations = new ToolAnnotations
            {
                Title = title,
                ReadOnlyHint = readOnly,
                DestructiveHint = !readOnly,
                OpenWorldHint = true
            }
        };
    }
}
