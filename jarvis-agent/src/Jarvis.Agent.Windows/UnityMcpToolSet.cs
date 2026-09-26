using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Unity-specific registration over the shared, session-owned MCP transport.</summary>
public sealed class UnityMcpToolSet : ExternalMcpToolSet
{
    public UnityMcpToolSet(string configPath,
        Func<McpServerConfig, CancellationToken, Task<IMcpClient>>? connect = null)
        : base(configPath, "unity", "Unity",
            "Uses the local JarvisAgent/mcp.json Unity entry; keep Unity's MCP session running.",
            UnityMcpConfig.Load, connect, catalogNamePrefix: "unity_") { }
}
