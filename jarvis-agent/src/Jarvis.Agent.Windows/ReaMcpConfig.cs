using System.IO;
using System.Text.Json;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Operator-owned, local-only REA stdio configuration (never read Codex's MCP profile).</summary>
public static class ReaMcpConfig
{
    public const string FileName = "mcp.json";
    public const string ServerName = "rea";
    private static readonly ExternalMcpConfig Reader = new(ServerName, "REA",
        "Add mcpServers.rea to the local JarvisAgent/mcp.json with an absolute node.exe path, " +
        "the installed rea-agents/scripts/rea.mjs path and the mcp argument.");

    public static McpServerConfig Load(string path) => Validate(Reader.Load(path));
    public static McpServerConfig Parse(JsonElement root) => Validate(Reader.Parse(root));

    private static McpServerConfig Validate(McpServerConfig config)
    {
        if (config.Type != "stdio" || config.Headers.Count != 0 ||
            !Path.IsPathFullyQualified(config.Command) ||
            !string.Equals(Path.GetFileName(config.Command), "node.exe", StringComparison.OrdinalIgnoreCase) ||
            config.Args.Count != 2 || !Path.IsPathFullyQualified(config.Args[0]) ||
            !string.Equals(Path.GetFileName(config.Args[0]), "rea.mjs", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(config.Args[0])), "scripts", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(config.Args[0]))), "rea-agents", StringComparison.OrdinalIgnoreCase) ||
            config.Args[1] != "mcp")
            throw new InvalidDataException(
                "REA MCP requires local stdio: an absolute node.exe command and exactly [absolute rea-agents/scripts/rea.mjs, mcp] arguments. HTTP and shell launchers are not supported.");

        foreach (var key in new[] { "GHIDRA_INSTALL_DIR", "JAVA_HOME" })
        {
            if (config.Env.TryGetValue(key, out var path) &&
                (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['\r', '\n', '\0']) >= 0))
                throw new InvalidDataException($"REA MCP {key} must be an absolute local directory path.");
        }
        return config;
    }
}
