using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Session-owned bridge to the operator-installed Reverse Engineer Anything MCP server.</summary>
public sealed class ReaMcpToolSet : ExternalMcpToolSet
{
    public ReaMcpToolSet(string configPath,
        Func<McpServerConfig, CancellationToken, Task<IMcpClient>>? connect = null)
        : base(configPath, "rea", "REA",
            "Uses mcpServers.rea in the local JarvisAgent/mcp.json, independent of Codex. " +
            "Discover downstream schemas with rea_list_tools before rea_call_tool. " +
            "The installed REA server may run native analysis engines and perform modifying operations, " +
            "so discovery and every call require normal sensitive-tool approval. " +
            "Ghidra native analysis needs a compatible Ghidra installation and JDK in the child environment. " +
            "Cancellation cannot undo a downstream native action; never automatically retry uncertain calls.",
            ReaMcpConfig.Load, connect, catalogNamePrefix: "rea_") { }
}
