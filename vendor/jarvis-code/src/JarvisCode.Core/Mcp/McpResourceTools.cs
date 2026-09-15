using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Mcp;

/// <summary>
/// Lists the resources connected MCP servers expose (the reference
/// ListMcpResources). Read-only: listing costs nothing and changes nothing.
/// </summary>
public sealed class ListMcpResourcesTool(McpManager manager) : ITool
{
    public string Name => "list_mcp_resources";

    public string Description =>
        "Lists the resources available from the connected MCP servers (files, documents, data the servers " +
        "expose for reading — separate from their tools). Pass server to list one server's resources only. " +
        "Read a listed resource with read_mcp_resource.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("server", SchemaBuilder.String("Only list resources from this MCP server")),
        ]);

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"ListMcpResources({JsonArgs.GetString(arguments, "server") ?? "all servers"})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var filter = JsonArgs.GetString(arguments, "server");
        var resources = manager.Resources;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            if (!manager.ConnectedToolCounts.Keys.Contains(filter, StringComparer.OrdinalIgnoreCase))
                return Task.FromResult(ToolResult.Error(
                    $"No connected MCP server named '{filter}'. Connected: " +
                    $"{string.Join(", ", manager.ConnectedToolCounts.Keys)}."));
            resources = [.. resources.Where(r =>
                string.Equals(r.Server, filter, StringComparison.OrdinalIgnoreCase))];
        }
        if (resources.Count == 0)
            return Task.FromResult(ToolResult.Success("No MCP resources are available."));

        var lines = new StringBuilder();
        foreach (var (server, resource) in resources)
        {
            lines.Append($"{server} · {resource.Uri} · {resource.Name}");
            if (!string.IsNullOrWhiteSpace(resource.Description))
                lines.Append($" — {resource.Description}");
            if (!string.IsNullOrWhiteSpace(resource.MimeType))
                lines.Append($" ({resource.MimeType})");
            lines.AppendLine();
        }
        return Task.FromResult(ToolResult.Success(context.Truncate(lines.ToString().TrimEnd(), "resource list")));
    }
}

/// <summary>Reads one MCP resource by server and uri (the reference ReadMcpResource).</summary>
public sealed class ReadMcpResourceTool(McpManager manager) : ITool
{
    public string Name => "read_mcp_resource";

    public string Description =>
        "Reads one resource from a connected MCP server by its uri (find both with list_mcp_resources). " +
        "Returns the resource's text content; binary content is noted but not returned.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("server", SchemaBuilder.String("The MCP server that owns the resource")),
            ("uri", SchemaBuilder.String("The resource uri, exactly as listed")),
        ],
        "server", "uri");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"ReadMcpResource({JsonArgs.GetString(arguments, "server") ?? "?"}: {JsonArgs.GetString(arguments, "uri") ?? "?"})";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var server = JsonArgs.GetString(arguments, "server");
        var uri = JsonArgs.GetString(arguments, "uri");
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(uri))
            return ToolResult.Error("server and uri are both required.");
        try
        {
            var content = await manager.ReadResourceAsync(server, uri, cancellationToken);
            return ToolResult.Success(context.Truncate(
                content.Length == 0 ? "(the resource has no text content)" : content, "resource content"));
        }
        catch (McpException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}
