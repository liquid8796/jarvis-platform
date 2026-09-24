using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Real MCP stdio to the Blender server; never bypass its code validation with raw socket commands.</summary>
public sealed class BlenderMcpToolSet : ExternalMcpToolSet
{
    public BlenderMcpToolSet(string configPath,
        Func<McpServerConfig, CancellationToken, Task<IMcpClient>>? connect = null)
        : base(configPath, "blender", "Blender",
            "Uses mcpServers.blender in local JarvisAgent/mcp.json. Start the Blender addon first. " +
            "Discover get_addon_status, get_scene_info, get_object_info, execute_blender_code and get_viewport_screenshot. " +
            "Safe Mode stays enabled. Sessions share the configured Blender scene; inspect it before changes. " +
            "Pause cancels the bridge but cannot undo or guarantee interruption of Python already running in Blender.",
            BlenderMcpConfig.Load, connect, serializeAcrossSessions: true, catalogNamePrefix: "blender_") { }
}
