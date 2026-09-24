using System.IO;
using System.Text.Json;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Reads only the operator's Unity entry, never a repository-supplied MCP file.</summary>
public static class UnityMcpConfig
{
    public const string FileName = "mcp.json";
    public const string ServerName = "unityMCP";
    public const int MaxConfigBytes = 256 * 1024;

    public static McpServerConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("Unity MCP is not configured. In Unity, select Jarvis Agent and click Configure.");
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxConfigBytes)
            throw new InvalidDataException("The Jarvis MCP configuration exceeds 256 KiB.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        return Parse(document.RootElement);
    }

    public static McpServerConfig Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("mcpServers", out var servers) ||
            servers.ValueKind != JsonValueKind.Object || !servers.TryGetProperty(ServerName, out var server) ||
            server.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The MCP configuration must contain an mcpServers.unityMCP object.");
        if (Flag(server, "disabled") == true || Flag(server, "enabled") == false)
            throw new InvalidDataException("Unity MCP is disabled. Enable it locally or use Configure in Unity.");

        var url = Text(server, "url");
        var type = Text(server, "type") ?? (url is null ? "stdio" : "http");
        if (type is not ("stdio" or "http"))
            throw new InvalidDataException("Unity MCP transport must be stdio or http (Streamable HTTP).");
        var command = Text(server, "command");
        var args = new List<string>();
        if (server.TryGetProperty("args", out var arguments))
        {
            if (arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() > 128)
                throw new InvalidDataException("Unity MCP args must be an array of at most 128 strings.");
            foreach (var arg in arguments.EnumerateArray())
            {
                if (arg.ValueKind != JsonValueKind.String || arg.GetString()!.Length > 16384)
                    throw new InvalidDataException("Unity MCP arguments must be bounded strings.");
                args.Add(arg.GetString()!);
            }
        }
        if (type == "http")
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
                uri.Fragment.Length != 0 || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
                throw new InvalidDataException("Use HTTPS for remote Unity MCP, or HTTP on localhost only.");
            if (command is not null || args.Count != 0)
                throw new InvalidDataException("HTTP Unity MCP must not include a stdio command or args. Reconfigure it in Unity.");
        }
        else if (string.IsNullOrWhiteSpace(command) || url is not null)
            throw new InvalidDataException("Stdio Unity MCP needs a command and must not include an HTTP URL.");

        return new McpServerConfig(ServerName, command ?? "", args, Map(server, "env"))
        {
            Type = type,
            Url = url,
            Headers = Map(server, "headers")
        };
    }

    private static string? Text(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) ||
            value.GetString()!.Length > 16384 || value.GetString()!.IndexOf('\0') >= 0)
            throw new InvalidDataException($"Unity MCP {name} must be a non-empty bounded string.");
        return value.GetString();
    }

    private static bool? Flag(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Unity MCP {name} must be a boolean.");
        return value.GetBoolean();
    }

    private static IReadOnlyDictionary<string, string> Map(JsonElement obj, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!obj.TryGetProperty(name, out var map)) return result;
        if (map.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Unity MCP {name} must be an object of strings.");
        foreach (var property in map.EnumerateObject())
        {
            if (result.Count >= 128 || string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 256 ||
                property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 16384)
                throw new InvalidDataException($"Unity MCP {name} contains an invalid entry.");
            var value = property.Value.GetString()!;
            if (property.Name.IndexOfAny(['\r', '\n', '\0']) >= 0 || value.Contains('\0') ||
                (name == "headers" && value.IndexOfAny(['\r', '\n']) >= 0))
                throw new InvalidDataException($"Unity MCP {name} contains invalid control characters.");
            result.Add(property.Name, value);
        }
        return result;
    }
}
