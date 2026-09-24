using System.Globalization;
using System.IO;
using System.Text.Json;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Local Python/stdio Blender MCP, with the same safe-mode/telemetry contract as the authoring pipeline.</summary>
public static class BlenderMcpConfig
{
    public const string FileName = "mcp.json";
    public const string ServerName = "blender";
    public const int MaxConfigBytes = ExternalMcpConfig.MaxConfigBytes;
    private static readonly ExternalMcpConfig Reader = new(ServerName, "Blender",
        "Run scripts/Configure-BlenderMcp.ps1 with the existing Blender MCP Python executable.");

    public static McpServerConfig Load(string path) => Validate(Reader.Load(path));
    public static McpServerConfig Parse(JsonElement root) => Validate(Reader.Parse(root));

    private static McpServerConfig Validate(McpServerConfig config)
    {
        if (config.Type != "stdio" || config.Headers.Count != 0 ||
            !Path.IsPathFullyQualified(config.Command) ||
            !string.Equals(Path.GetFileName(config.Command), "python.exe", StringComparison.OrdinalIgnoreCase) ||
            !config.Args.SequenceEqual(["-m", "blender_mcp.server"]))
            throw new InvalidDataException("Blender MCP requires an absolute python.exe path and args [-m, blender_mcp.server] over stdio; HTTP and shell launchers are not supported.");

        var env = new Dictionary<string, string>(config.Env, StringComparer.OrdinalIgnoreCase);
        var host = env.GetValueOrDefault("BLENDER_HOST", "127.0.0.1");
        if (host != "127.0.0.1" && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Blender's addon must listen on local IPv4 loopback (127.0.0.1).");
        if (!int.TryParse(env.GetValueOrDefault("BLENDER_PORT", "9876"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new InvalidDataException("BLENDER_PORT must be an integer from 1 to 65535.");
        RequireEnabled(env, "BLENDER_MCP_SAFE_MODE", "1");
        RequireEnabled(env, "BLENDER_MCP_DISABLE_TELEMETRY", "true");
        RequireEnabled(env, "DISABLE_TELEMETRY", "true");
        env["BLENDER_HOST"] = "127.0.0.1";
        env["BLENDER_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        env["PYTHONIOENCODING"] = "utf-8";
        return config with { Env = env };
    }

    private static void RequireEnabled(Dictionary<string, string> env, string key, string normalized)
    {
        if (env.TryGetValue(key, out var value) && value.Trim().ToLowerInvariant() is not ("1" or "true" or "yes" or "on"))
            throw new InvalidDataException("Blender integration requires Safe Mode enabled and telemetry disabled. Review the local Blender MCP environment settings.");
        env[key] = normalized; // Explicitly override an unsafe value inherited by the Agent process.
    }
}
