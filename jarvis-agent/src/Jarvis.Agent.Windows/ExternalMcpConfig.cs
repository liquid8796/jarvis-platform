using System.IO;
using System.Text.Json;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Strict reader for a named entry in the operator-owned MCP profile.</summary>
internal sealed class ExternalMcpConfig(string serverName, string displayName, string setupHint)
{
    public const string FileName = "mcp.json";
    public const int MaxConfigBytes = 256 * 1024;

    public McpServerConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"{displayName} MCP is not configured. {setupHint}");
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxConfigBytes)
            throw new InvalidDataException("The Jarvis MCP configuration exceeds 256 KiB.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        return Parse(document.RootElement);
    }

    public McpServerConfig Parse(JsonElement root)
    {
        RejectDuplicateProperties(root);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("mcpServers", out var servers) ||
            servers.ValueKind != JsonValueKind.Object || !servers.TryGetProperty(serverName, out var server) ||
            server.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"The MCP configuration must contain an mcpServers.{serverName} object. {setupHint}");
        RejectDuplicateProperties(servers);
        RejectDuplicateProperties(server);
        if (Flag(server, "disabled") == true || Flag(server, "enabled") == false)
            throw new InvalidDataException($"{displayName} MCP is disabled. Enable it locally. {setupHint}");

        var url = Text(server, "url");
        var type = Text(server, "type") ?? (url is null ? "stdio" : "http");
        if (type is not ("stdio" or "http"))
            throw new InvalidDataException($"{displayName} MCP transport must be stdio or http (Streamable HTTP).");
        var command = Text(server, "command");
        var args = new List<string>();
        if (server.TryGetProperty("args", out var arguments))
        {
            if (arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() > 128)
                throw new InvalidDataException($"{displayName} MCP args must be an array of at most 128 strings.");
            foreach (var arg in arguments.EnumerateArray())
            {
                if (arg.ValueKind != JsonValueKind.String || arg.GetString()!.Length > 16384 || arg.GetString()!.Contains('\0'))
                    throw new InvalidDataException($"{displayName} MCP arguments must be bounded strings.");
                args.Add(arg.GetString()!);
            }
        }
        if (type == "http")
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
                uri.Fragment.Length != 0 || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
                throw new InvalidDataException($"Use HTTPS for remote {displayName} MCP, or HTTP on localhost only.");
            if (command is not null || args.Count != 0)
                throw new InvalidDataException($"HTTP {displayName} MCP must not include a stdio command or args. Reconfigure it locally.");
        }
        else if (string.IsNullOrWhiteSpace(command) || url is not null)
            throw new InvalidDataException($"Stdio {displayName} MCP needs a command and must not include an HTTP URL.");

        return new McpServerConfig(serverName, command ?? "", args, Map(server, "env"))
        {
            Type = type,
            Url = url,
            Headers = Map(server, "headers")
        };
    }

    private static void RejectDuplicateProperties(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
            if (!seen.Add(property.Name))
                throw new InvalidDataException("The MCP configuration contains duplicate properties; fix it locally.");
    }

    private string? Text(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) ||
            value.GetString()!.Length > 16384 || value.GetString()!.IndexOf('\0') >= 0)
            throw new InvalidDataException($"{displayName} MCP {name} must be a non-empty bounded string.");
        return value.GetString();
    }

    private bool? Flag(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"{displayName} MCP {name} must be a boolean.");
        return value.GetBoolean();
    }

    private IReadOnlyDictionary<string, string> Map(JsonElement obj, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!obj.TryGetProperty(name, out var map)) return result;
        if (map.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{displayName} MCP {name} must be an object of strings.");
        foreach (var property in map.EnumerateObject())
        {
            if (result.Count >= 128 || string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 256 ||
                property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 16384)
                throw new InvalidDataException($"{displayName} MCP {name} contains an invalid entry.");
            var value = property.Value.GetString()!;
            if (property.Name.IndexOfAny(['\r', '\n', '\0']) >= 0 || value.Contains('\0') ||
                (name == "headers" && value.IndexOfAny(['\r', '\n']) >= 0))
                throw new InvalidDataException($"{displayName} MCP {name} contains invalid control characters.");
            if (!result.TryAdd(property.Name, value))
                throw new InvalidDataException($"{displayName} MCP {name} contains duplicate entries.");
        }
        return result;
    }
}
