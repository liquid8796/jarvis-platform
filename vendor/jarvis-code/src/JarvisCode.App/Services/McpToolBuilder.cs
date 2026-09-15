using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// Shared plumbing for the in-process MCP servers: the schema shorthand and a
/// tool whose body is a delegate. The servers each declare their own tools with
/// the reference's docs and schemas; only this scaffolding is common.
/// </summary>
internal static class McpToolBuilder
{
    public static JsonObject Prop(string type) => new() { ["type"] = type };

    public static JsonObject Prop(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    public static JsonObject ArrayProp(string itemType) =>
        new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = itemType } };

    public static JsonObject ArrayProp(string itemType, string description) =>
        new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = itemType },
            ["description"] = description,
        };

    public static JsonObject Schema(JsonObject? properties = null, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties ?? [],
        };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray([.. required.Select(static r => (JsonNode)r)]);
        }

        return schema;
    }

    public static ITool Tool(
        string name,
        string description,
        JsonObject schema,
        bool isReadOnly,
        Func<JsonObject, ToolExecutionContext, CancellationToken, Task<ToolResult>> execute,
        Func<JsonObject, string>? describe = null) =>
        new DelegateMcpTool(name, description, schema, isReadOnly, execute, describe);

    /// <summary>A synchronous body, for tools that only read memory.</summary>
    public static ITool Tool(
        string name,
        string description,
        JsonObject schema,
        bool isReadOnly,
        Func<JsonObject, ToolExecutionContext, ToolResult> execute,
        Func<JsonObject, string>? describe = null) =>
        new DelegateMcpTool(
            name, description, schema, isReadOnly,
            (args, context, _) => Task.FromResult(execute(args, context)),
            describe);

    private sealed class DelegateMcpTool(
        string name,
        string description,
        JsonObject schema,
        bool isReadOnly,
        Func<JsonObject, ToolExecutionContext, CancellationToken, Task<ToolResult>> execute,
        Func<JsonObject, string>? describe) : ITool
    {
        public string Name => name;

        public string Description => description;

        public JsonObject InputSchema => schema;

        public bool IsReadOnly => isReadOnly;

        public string DescribeCall(JsonObject arguments) =>
            describe is not null ? describe(arguments) : $"{name}()";

        public Task<ToolResult> ExecuteAsync(
            JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
            execute(arguments, context, cancellationToken);
    }
}
