using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Protocol;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Real MCP stdio to the Blender server; never bypass its code validation with raw socket commands.</summary>
public sealed class BlenderMcpToolSet : ExternalMcpToolSet
{
    public BlenderMcpToolSet(string configPath,
        Func<McpServerConfig, CancellationToken, Task<IMcpClient>>? connect = null)
        : base(configPath, "blender", "Blender",
            "Uses mcpServers.blender in local JarvisAgent/mcp.json. Start the Blender addon first. " +
            "Use blender_get_capabilities to audit the live catalog and optionally the addon handshake. " +
            "Rediscover schemas after upgrades: Poly Haven 2.1 uses query/category/attributes/min_size_m/limit, not categories. " +
            "Inspect get_scene_info and get_viewport_screenshot before and after mutations; look up describe_node_type and bpy_api_lookup instead of guessing APIs. " +
            "Preview Poly Haven/Sketchfab assets before downloading. Tripo requires upstream Premium; listed tools do not prove provider readiness or authorize paid generation. " +
            "Safe Mode stays enabled. Sessions share the configured Blender scene; inspect it before changes. " +
            "Pause cancels the bridge but cannot undo or guarantee interruption of Python already running in Blender.",
            BlenderMcpConfig.Load, connect, serializeAcrossSessions: true, catalogNamePrefix: "blender_",
            extensionTools: [BlenderMcpCapabilities.Descriptor]) { }

    protected override bool IsToolError(string name, McpCallResult result) => BlenderMcpResult.IsError(name, result);

    protected override async Task<string?> ValidateCallAsync(string name, JsonObject arguments,
        IMcpClient client, CancellationToken token)
    {
        if (name != "search_polyhaven_assets" || !arguments.ContainsKey("categories")) return null;
        var tools = await client.ListToolsAsync(token).ConfigureAwait(false);
        var schema = tools.FirstOrDefault(t => t.Name == name)?.InputSchema;
        return schema?["properties"] is JsonObject properties && !properties.ContainsKey("categories")
            ? "Poly Haven no longer accepts 'categories'. Rediscover the schema and use a valid 'category' path from get_polyhaven_categories. The search was not sent; no filter was silently dropped."
            : null;
    }

    protected override Task<ToolReply> ExecuteExtensionAsync(string operation, JsonElement arguments,
        IMcpClient client, CancellationToken token) => operation == "get_capabilities"
        ? BlenderMcpCapabilities.ReadAsync(arguments, client, token)
        : base.ExecuteExtensionAsync(operation, arguments, client, token);
}
