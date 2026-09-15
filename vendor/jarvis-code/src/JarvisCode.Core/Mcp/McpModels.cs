using System.Text.Json.Nodes;

namespace JarvisCode.Core.Mcp;

/// <summary>
/// One configured MCP server. A stdio server carries a command; a remote one
/// carries a url (`type` "http" — streamable HTTP with SSE fallback — or "sse"
/// to force the legacy transport) plus optional extra request headers.
/// </summary>
public sealed record McpServerConfig(
    string Name,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env)
{
    /// <summary>stdio | http | sse.</summary>
    public string Type { get; init; } = "stdio";

    /// <summary>
    /// Which settings layer declared this server, which is what decides whether
    /// a tool whose input schema the API would reject is dropped or kept with a
    /// warning (the reference's Ye over its Fr). A checked-in project file is
    /// repo scope; the user's own file and this build's own servers are operator
    /// scope, which never drops.
    /// </summary>
    public McpServerScope Scope { get; init; } = McpServerScope.Operator;

    /// <summary>Endpoint of a remote server; null for stdio.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// The reference's per-server <c>timeout</c>, in milliseconds: how long one
    /// request to this server may take. Its <c>request_timeout_ms</c> folds into
    /// the same field at parse, capped at <see cref="MaxTimeoutMs"/>. Null leaves
    /// the transport's own default.
    /// </summary>
    public int? TimeoutMs { get; init; }

    /// <summary>The cap the reference applies when folding request_timeout_ms in.</summary>
    public const int MaxTimeoutMs = 300_000;

    /// <summary>Extra headers a remote server wants on every request (auth tokens etc.).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A client id issued for this app by the server's authorization server, in
    /// place of the dynamic registration the sign-in would otherwise do. The
    /// reference offers it under Advanced when a connector is added.
    /// </summary>
    public string? OAuthClientId { get; init; }

    /// <summary>The client secret that goes with <see cref="OAuthClientId"/>, for a confidential client.</summary>
    public string? OAuthClientSecret { get; init; }

    /// <summary>
    /// The reference's <c>headersHelper</c>: a command that prints a JSON object
    /// of HTTP headers for this server, whose output overlays
    /// <see cref="Headers"/>. Null means none — see <see cref="McpHeadersHelper"/>.
    /// </summary>
    public string? HeadersHelper { get; init; }

    /// <summary>
    /// The reference's <c>discoveryCache</c>, which is an <b>opt-out</b>: its own
    /// eligibility table refuses the cache when the value is exactly
    /// <c>false</c>, and admits it otherwise. Null therefore means "cache", and
    /// so does true.
    /// </summary>
    public bool? DiscoveryCache { get; init; }

    public bool IsRemote => !string.IsNullOrWhiteSpace(Url);

    /// <summary>Extends the stdio signature with the remote fields so a url/header
    /// change restarts the server like a command change would.</summary>
    public string RemoteSignatureSuffix =>
        "|" + Type + "|" + (Url ?? "") + "|" + (TimeoutMs?.ToString() ?? "") + "|" +
        string.Join(';', Headers.OrderBy(h => h.Key).Select(h => h.Key + "=" + h.Value)) +
        "|" + (OAuthClientId ?? "") + "|" + (OAuthClientSecret is null ? "" : "set") +
        "|" + (HeadersHelper ?? "") + "|" + (DiscoveryCache?.ToString() ?? "");

    /// <summary>Identity used to detect config changes across refreshes.</summary>
    public string Signature => BuildSignature() + RemoteSignatureSuffix;

    private string BuildSignature() =>
        Name + "|" + Command + "|" + string.Join('\u001f', Args) + "|" +
        string.Join('\u001f', Env.OrderBy(e => e.Key).Select(e => e.Key + "=" + e.Value));
}

/// <summary>A tool advertised by an MCP server.</summary>
public sealed record McpToolDescriptor(string Name, string Description, JsonObject InputSchema)
{
    /// <summary>
    /// The reference's <c>anthropic/alwaysLoad</c> tool annotation: this tool is
    /// never hidden behind tool_search, however large the server's surface is.
    /// A server marks the tools a turn cannot afford to go looking for.
    /// </summary>
    public bool AlwaysLoad { get; init; }

    /// <summary>
    /// The reference's <c>anthropic/searchHint</c>: a phrase describing what the
    /// tool is for, which its tool search scores after the name and ahead of the
    /// description.
    /// </summary>
    public string? SearchHint { get; init; }

    /// <summary>
    /// The reference's <c>anthropic/maxResultSizeChars</c>: the size past which
    /// this tool's result is persisted to disk instead of sent, read only when
    /// it is a finite number above zero.
    /// </summary>
    public int? MaxResultSizeChars { get; init; }

    /// <summary>
    /// The reference's <c>anthropic/requiresUserInteraction</c>: a call that is
    /// waiting on a person rather than running slowly, so it is never moved to
    /// the background for taking too long.
    /// </summary>
    public bool RequiresUserInteraction { get; init; }

    /// <summary>Reads the annotation out of a tools/list entry's <c>_meta</c>.</summary>
    public static bool ReadAlwaysLoad(JsonObject entry) =>
        ReadFlag(entry, "anthropic/alwaysLoad");

    /// <summary>Reads <c>anthropic/requiresUserInteraction</c>, which the reference reads as <c>=== true</c>.</summary>
    public static bool ReadRequiresUserInteraction(JsonObject entry) =>
        ReadFlag(entry, "anthropic/requiresUserInteraction");

    /// <summary>Reads <c>anthropic/searchHint</c>, which the reference takes only when it is a string.</summary>
    public static string? ReadSearchHint(JsonObject entry) =>
        Meta(entry)?["anthropic/searchHint"] is JsonValue value &&
        value.TryGetValue(out string? hint) && !string.IsNullOrEmpty(hint)
            ? hint
            : null;

    /// <summary>Reads <c>anthropic/maxResultSizeChars</c>; anything but a finite number above zero is ignored.</summary>
    public static int? ReadMaxResultSizeChars(JsonObject entry)
    {
        if (Meta(entry)?["anthropic/maxResultSizeChars"] is not JsonValue value ||
            !value.TryGetValue(out double size) || !double.IsFinite(size) || size <= 0)
        {
            return null;
        }

        return (int)Math.Min(size, int.MaxValue);
    }

    private static JsonObject? Meta(JsonObject entry) => entry["_meta"] as JsonObject;

    private static bool ReadFlag(JsonObject entry, string key) =>
        Meta(entry)?[key] is JsonValue value && value.TryGetValue(out bool flag) && flag;
}

/// <summary>Result of tools/call: concatenated text content plus the error flag.</summary>
public sealed record McpCallResult(string Text, bool IsError)
{
    public IReadOnlyList<Models.ImageBlock>? Images { get; init; }
    public JsonNode? StructuredContent { get; init; }
}

/// <summary>A resource advertised by an MCP server (resources/list).</summary>
public sealed record McpResourceDescriptor(string Uri, string Name, string? Description, string? MimeType);

/// <summary>One argument a server prompt declares.</summary>
public sealed record McpPromptArgument(string Name, string? Description, bool Required);

/// <summary>A prompt advertised by an MCP server (prompts/list); surfaces as /mcp__server__name.</summary>
public sealed record McpPromptDescriptor(string Name, string? Description, IReadOnlyList<McpPromptArgument> Arguments);

public class McpException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Loads MCP server configs from {cwd}/.jarvis/mcp.json plus a user-level file.</summary>
public static class McpConfig
{
    public const string ProjectRelativePath = ".jarvis/mcp.json";

    public static IReadOnlyList<McpServerConfig> Load(
        string workingDirectory, string? userConfigPath, IEnumerable<string>? extraConfigFiles = null)
    {
        var byName = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
        if (userConfigPath is not null)
        {
            foreach (var server in LoadFile(userConfigPath))
                byName[server.Name] = server;
        }
        foreach (var extra in extraConfigFiles ?? [])
        {
            foreach (var server in LoadFile(extra))
                byName[server.Name] = server;
        }
        // The project file is checked in, so its servers are the reference's repo
        // scope — the one scope whose invalid tools are dropped rather than kept.
        foreach (var server in LoadFile(Path.Combine(workingDirectory, ProjectRelativePath)))
            byName[server.Name] = server with { Scope = McpServerScope.Repo };
        return [.. byName.Values];
    }

    /// <summary>Servers of one config file only — used by UIs that must show the source.</summary>
    public static IReadOnlyList<McpServerConfig> LoadSingleFile(string path) => [.. LoadFile(path)];

    /// <summary>
    /// Writes the user-level config file. Only name/command/args/env are stored —
    /// the same shape <see cref="LoadFile"/> reads back.
    /// </summary>
    public static void SaveUserServers(string path, IEnumerable<McpServerConfig> servers)
    {
        var entries = new JsonArray();
        foreach (var server in servers)
        {
            var entry = new JsonObject { ["name"] = server.Name };
            if (server.IsRemote)
            {
                entry["type"] = server.Type;
                entry["url"] = server.Url;
                if (server.Headers.Count > 0)
                {
                    var headers = new JsonObject();
                    foreach (var (key, value) in server.Headers)
                        headers[key] = value;
                    entry["headers"] = headers;
                }
                if (server.OAuthClientId is { Length: > 0 } clientId)
                    entry["oauth_client_id"] = clientId;
                if (server.OAuthClientSecret is { Length: > 0 } clientSecret)
                    entry["oauth_client_secret"] = clientSecret;
                if (server.HeadersHelper is { Length: > 0 } headersHelper)
                    entry["headersHelper"] = headersHelper;
                if (server.DiscoveryCache is { } discoveryCache)
                    entry["discoveryCache"] = discoveryCache;
            }
            else
            {
                entry["command"] = server.Command;
            }
            if (server.Args.Count > 0)
                entry["args"] = new JsonArray([.. server.Args.Select(a => (JsonNode)JsonValue.Create(a))]);
            if (server.Env.Count > 0)
            {
                var env = new JsonObject();
                foreach (var (key, value) in server.Env)
                    env[key] = value;
                entry["env"] = env;
            }
            entries.Add(entry);
        }
        var root = new JsonObject { ["servers"] = entries };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<McpServerConfig> LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["servers"] is JsonArray servers)
            {
                var configs = new List<McpServerConfig>();
                foreach (var entry in servers.OfType<JsonObject>())
                {
                    if (TryParseServer(Tools.JsonArgs.GetString(entry, "name"), entry) is { } config)
                        configs.Add(config);
                }
                return configs;
            }

            // Claude Code / Claude Desktop shape: {"mcpServers": {"name": {command, args, env}}}.
            if (root?["mcpServers"] is JsonObject mcpServers)
            {
                var configs = new List<McpServerConfig>();
                foreach (var (name, value) in mcpServers)
                {
                    if (value is JsonObject entry && TryParseServer(name, entry) is { } config)
                        configs.Add(config);
                }
                return configs;
            }

            return [];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static McpServerConfig? TryParseServer(string? name, JsonObject entry)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace))
            return null;
        var command = Tools.JsonArgs.GetString(entry, "command");
        var url = Tools.JsonArgs.GetString(entry, "url");
        if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(url))
            return null;
        var args = (entry["args"] as JsonArray)?
            .Select(a => a?.GetValue<string>())
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList() ?? [];
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entry["env"] is JsonObject envJson)
        {
            foreach (var (key, value) in envJson)
            {
                if (value is JsonValue jsonValue && jsonValue.TryGetValue(out string? text))
                    env[key] = text;
            }
        }

        var timeout = ReadTimeout(entry);
        if (string.IsNullOrWhiteSpace(url))
            return new McpServerConfig(name, command!, args, env) { TimeoutMs = timeout };

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entry["headers"] is JsonObject headersJson)
        {
            foreach (var (key, value) in headersJson)
            {
                if (value is JsonValue jsonValue && jsonValue.TryGetValue(out string? text))
                    headers[key] = text;
            }
        }

        var type = Tools.JsonArgs.GetString(entry, "type")?.ToLowerInvariant();
        return new McpServerConfig(name, command ?? "", args, env)
        {
            Type = type is "sse" ? "sse" : "http",
            Url = url,
            Headers = headers,
            TimeoutMs = timeout,
            OAuthClientId = Tools.JsonArgs.GetString(entry, "oauth_client_id"),
            OAuthClientSecret = Tools.JsonArgs.GetString(entry, "oauth_client_secret"),
            HeadersHelper = Tools.JsonArgs.GetString(entry, "headersHelper"),
            DiscoveryCache = entry["discoveryCache"] is JsonValue cache && cache.TryGetValue(out bool enabled)
                ? enabled
                : null,
        };
    }

    /// <summary>
    /// The reference's <c>timeout</c>, or its <c>request_timeout_ms</c> folded
    /// into the same field and capped at 300s — the fold only applies when no
    /// explicit timeout was given, and a non-positive value is no timeout at all.
    /// </summary>
    internal static int? ReadTimeout(JsonObject entry)
    {
        if (Positive(entry, "timeout") is { } explicitTimeout)
            return explicitTimeout;
        return Positive(entry, "request_timeout_ms") is { } hint
            ? Math.Min(hint, McpServerConfig.MaxTimeoutMs)
            : null;

        static int? Positive(JsonObject entry, string key) =>
            entry[key] is JsonValue value && value.TryGetValue(out int number) && number > 0
                ? number
                : null;
    }
}
