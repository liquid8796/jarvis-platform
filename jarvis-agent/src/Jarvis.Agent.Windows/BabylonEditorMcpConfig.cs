using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>
/// Reads an active BabylonJS Editor 5.5.2+ Assistant-issued MCP profile.
/// The short-lived loopback token stays in the Editor's temporary session,
/// never in the Jarvis operator-owned MCP configuration.
/// </summary>
public static class BabylonEditorMcpConfig
{
    public const string FileName = "mcp.json";
    public const string ServerName = "babylonjs-editor";
    private static readonly ExternalMcpConfig Reader = new(ServerName, "BabylonJS Editor",
        "Open a project in BabylonJS Editor, start its AI Assistant session, and keep it open.");
    private const string SessionFolderName = "babylonjs-editor-assistant";

    public static McpServerConfig Load(string jarvisConfigPath) =>
        LoadFromSessions(Path.Combine(Path.GetTempPath(), SessionFolderName));

    /// <summary>Bounded, non-recursive discovery: no secrets are copied or persisted by Jarvis.</summary>
    public static McpServerConfig LoadFromSessions(string sessionsRoot)
    {
        if (!Directory.Exists(sessionsRoot))
            throw new InvalidDataException("BabylonJS Editor MCP is not active. Open a project and start AI Assistant in the Editor.");
        var candidates = new List<McpServerConfig>();
        var seen = 0;
        foreach (var dir in Directory.EnumerateDirectories(sessionsRoot))
        {
            if (++seen > 64)
                throw new InvalidDataException("Too many BabylonJS Editor assistant sessions; close stale sessions.");
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            var configPath = Path.Combine(dir, FileName);
            if (!File.Exists(configPath) || (File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0) continue;
            try { candidates.Add(Validate(Reader.Load(configPath))); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
            {
                // Ignore another assistant's incomplete or malformed session profile.
            }
        }
        if (candidates.Count == 0)
            throw new InvalidDataException("No valid BabylonJS Editor MCP session. Start Claude Code from the Editor AI Assistant and keep that session running.");
        if (candidates.Count > 1)
            throw new InvalidDataException("Multiple BabylonJS Editor assistant MCP sessions found. Close other sessions before using Jarvis.");
        return candidates[0];
    }

    public static McpServerConfig Parse(JsonElement root) => Validate(Reader.Parse(root));

    private static McpServerConfig Validate(McpServerConfig config)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "BabylonJS Editor", "BabylonJS Editor.exe");
        var script = Path.Combine(Path.GetDirectoryName(exe)!, "resources", "app.asar", "build", "mcp", "index.mjs");
        if (config.Type != "stdio" || config.Headers.Count != 0 ||
            !Path.IsPathFullyQualified(config.Command) ||
            !Path.GetFullPath(config.Command).Equals(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase) ||
            config.Args.Count != 1 || !Path.IsPathFullyQualified(config.Args[0]) ||
            !Path.GetFullPath(config.Args[0]).Equals(Path.GetFullPath(script), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BabylonJS Editor MCP must use its installed Electron executable and bundled MCP script over stdio.");

        if (config.Env.Count != 4 ||
            !config.Env.TryGetValue("ELECTRON_RUN_AS_NODE", out var runAsNode) || runAsNode != "1" ||
            !config.Env.TryGetValue("BABYLONJS_EDITOR_ASSISTANT", out var assistant) || assistant != "1" ||
            !config.Env.TryGetValue("BABYLONJS_EDITOR_MCP_TOKEN", out var token) ||
            !Regex.IsMatch(token, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant) ||
            !config.Env.TryGetValue("BABYLONJS_EDITOR_MCP_URL", out var url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" || uri.Host != "127.0.0.1" ||
            uri.Port <= 0 || uri.Port > 65535 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new InvalidDataException("BabylonJS Editor MCP needs an Editor-issued localhost session URL and 64-character token.");
        return config;
    }
}
