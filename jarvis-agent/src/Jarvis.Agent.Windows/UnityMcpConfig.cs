using System.Text.Json;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Reads only the operator's Unity entry, never a repository-supplied MCP file.</summary>
public static class UnityMcpConfig
{
    public const string FileName = "mcp.json";
    public const string ServerName = "unityMCP";
    public const int MaxConfigBytes = ExternalMcpConfig.MaxConfigBytes;
    private static readonly ExternalMcpConfig Reader = new(ServerName, "Unity",
        "In Unity, select Jarvis Agent and click Configure.");

    public static McpServerConfig Load(string path) => Reader.Load(path);
    public static McpServerConfig Parse(JsonElement root) => Reader.Parse(root);
}
