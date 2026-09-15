using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Mcp;

/// <summary>What a server's discovery listing looks like once it is cached.</summary>
public sealed record McpDiscoveryEntry(
    IReadOnlyList<McpToolDescriptor> Tools,
    IReadOnlyList<McpPromptDescriptor> Prompts,
    IReadOnlyList<McpResourceDescriptor> Resources)
{
    /// <summary>When the entry was written (unix milliseconds).</summary>
    public long SavedAt { get; init; }

    /// <summary>How many refreshes in a row failed after it was written.</summary>
    public int ConsecutiveRefreshFailures { get; init; }
}

/// <summary>How a cache lookup ended.</summary>
public enum McpDiscoveryCacheStatus
{
    /// <summary>Inside the TTL: serve it and do not list.</summary>
    Fresh,

    /// <summary>Past the TTL but inside the stale window: serve now and refresh in the background.</summary>
    Stale,

    /// <summary>Nothing usable: list.</summary>
    Miss,
}

/// <summary>The answer to a lookup: the status, the entry when there is one, and why a miss.</summary>
public sealed record McpDiscoveryCacheLookup(
    McpDiscoveryCacheStatus Status, McpDiscoveryEntry? Entry, string? Reason);

/// <summary>
/// The reference's <c>discoveryCache</c>: a remote server's <c>tools/list</c>,
/// <c>prompts/list</c> and <c>resources/list</c> kept between runs so a start
/// does not spend three round trips per server on a listing that rarely moves.
/// </summary>
/// <remarks>
/// Measured in CLI 2.1.257 (the cache module around byte 194292000):
/// <list type="bullet">
/// <item><description><c>discoveryCache</c> is an <b>opt-out</b>, not an opt-in:
///   its eligibility table refuses on <c>e === false</c> and admits every other
///   value. Its sibling row refuses whenever a <c>headersHelper</c> is set,
///   which is why a server whose headers are minted per run is never
///   cached.</description></item>
/// <item><description>Its other local refusals are the transport (only
///   <c>http</c> and <c>sse</c>) and an <c>${…}</c> placeholder left in the url
///   or a header value. The two this build cannot reach — a CLI-owned server and
///   an ambient credential — are declared in
///   <c>Deltas/reference-surface-deltas.tsv</c>.</description></item>
/// <item><description>The freshness ladder is its <c>ult</c>: past the strike
///   threshold (<c>MCP_DISCOVERY_CACHE_STRIKES</c>, default 1) is a miss;
///   an entry stamped further ahead than the stale window, or older than it, is
///   a miss; inside the TTL (<c>MCP_DISCOVERY_CACHE_TTL_S</c>, default 900s,
///   itself capped at the stale window) it is fresh unless the server declared
///   tools and cached none; otherwise it is stale.</description></item>
/// <item><description>The stale window is
///   <c>MCP_DISCOVERY_CACHE_MAX_STALE_S</c> (default 14400s) capped at 7 days,
///   and an entry file over 8 MiB is refused.</description></item>
/// </list>
/// One deliberate difference remains: the reference seals each entry
/// with AES-256-GCM under a key derived from the account token and an OAuth
/// grant, and keys the file by that account identity — there is no account here,
/// and what an entry holds is tool schemas rather than a credential, so entries
/// are plain JSON keyed by the server's own normalized config.
/// </remarks>
public sealed class McpDiscoveryCache(string directory)
{
    /// <summary>The reference's <c>ye</c>: the entry format version.</summary>
    private const int Version = 1;

    /// <summary>The reference's <c>lt</c>: the default TTL, in seconds.</summary>
    public const int DefaultTtlSeconds = 900;

    /// <summary>Its <c>pt</c>: the default stale window, in seconds.</summary>
    public const int DefaultMaxStaleSeconds = 14_400;

    /// <summary>Its <c>yt</c>: the ceiling on that window (7 days).</summary>
    public const int MaxStaleCeilingSeconds = 604_800;

    /// <summary>Its <c>ht</c>: how many consecutive refresh failures retire an entry.</summary>
    public const int DefaultStrikes = 1;

    /// <summary>Its <c>L</c>: the largest entry file it will read.</summary>
    public const int MaxEntryBytes = 8 * 1024 * 1024;

    public const string TtlVariable = "MCP_DISCOVERY_CACHE_TTL_S";
    public const string MaxStaleVariable = "MCP_DISCOVERY_CACHE_MAX_STALE_S";
    public const string StrikesVariable = "MCP_DISCOVERY_CACHE_STRIKES";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>
    /// Why this server may not be cached, or null when it may. The reasons are
    /// the reference's own spellings.
    /// </summary>
    public static string? IneligibleReason(McpServerConfig config)
    {
        if (!config.IsRemote ||
            (!string.Equals(config.Type, "http", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(config.Type, "sse", StringComparison.OrdinalIgnoreCase)))
        {
            return "transport";
        }

        if (HasPlaceholder(config.Url) || config.Headers.Values.Any(HasPlaceholder))
        {
            return "env-placeholder";
        }

        if (config.DiscoveryCache == false)
        {
            return "opt-out";
        }

        if (!string.IsNullOrWhiteSpace(config.HeadersHelper))
        {
            return "headers-helper";
        }

        return null;
    }

    /// <summary>The reference's <c>wt</c>: an unexpanded <c>${…}</c> left in a value.</summary>
    private static bool HasPlaceholder(string? value) =>
        value is not null && value.Contains("${", StringComparison.Ordinal);

    /// <summary>
    /// The cache key: the server's name and the config that decides what it
    /// answers, with the two properties that only govern eligibility removed —
    /// the reference's <c>Ge</c>, so flipping <c>discoveryCache</c> back on does
    /// not orphan the entry written before it.
    /// </summary>
    public static string CacheKey(McpServerConfig config) =>
        Hash(string.Join(
            '\u001f',
            config.Name,
            config.Type,
            config.Url ?? "",
            config.TimeoutMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            string.Join(';', config.Headers.OrderBy(h => h.Key, StringComparer.Ordinal)
                .Select(h => h.Key + "=" + h.Value)),
            config.OAuthClientId ?? ""));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static int Seconds(string variable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    /// <summary>The stale window in milliseconds (its <c>ze</c>).</summary>
    public static long MaxStaleMs =>
        Math.Min(Seconds(MaxStaleVariable, DefaultMaxStaleSeconds), MaxStaleCeilingSeconds) * 1000L;

    /// <summary>The TTL in milliseconds, itself capped at the stale window (its <c>vt</c>).</summary>
    public static long TtlMs => Math.Min(Seconds(TtlVariable, DefaultTtlSeconds) * 1000L, MaxStaleMs);

    /// <summary>How many consecutive failures retire an entry (its <c>$e</c>).</summary>
    public static int Strikes => Seconds(StrikesVariable, DefaultStrikes);

    /// <summary>The reference's ladder, as a pure function of the entry and the clock.</summary>
    public static McpDiscoveryCacheLookup Classify(McpDiscoveryEntry? entry, long nowMs)
    {
        if (entry is null)
        {
            return new McpDiscoveryCacheLookup(McpDiscoveryCacheStatus.Miss, null, "absent");
        }

        if (entry.ConsecutiveRefreshFailures >= Strikes)
        {
            return new McpDiscoveryCacheLookup(McpDiscoveryCacheStatus.Miss, entry, "strike-threshold");
        }

        var maxStale = MaxStaleMs;
        // The reference's clock-skew guard: an entry stamped further ahead than
        // the whole window is not usable.
        if (entry.SavedAt - nowMs > maxStale)
        {
            return new McpDiscoveryCacheLookup(McpDiscoveryCacheStatus.Miss, entry, "expired");
        }

        var age = Math.Max(0, nowMs - entry.SavedAt);
        if (age >= maxStale)
        {
            return new McpDiscoveryCacheLookup(McpDiscoveryCacheStatus.Miss, entry, "expired");
        }

        return age < TtlMs
            ? new McpDiscoveryCacheLookup(McpDiscoveryCacheStatus.Fresh, entry, null)
            : new McpDiscoveryCacheLookup(McpDiscoveryCacheStatus.Stale, entry, null);
    }

    private string PathFor(McpServerConfig config) =>
        Path.Combine(directory, Hash(CacheKey(config) + "|v" + Version)[..32] + ".json");

    /// <summary>Reads this server's entry and says whether it may be served.</summary>
    public McpDiscoveryCacheLookup Read(McpServerConfig config)
    {
        if (IneligibleReason(config) is { } reason)
        {
            return new McpDiscoveryCacheLookup(McpDiscoveryCacheStatus.Miss, null, reason);
        }

        return Classify(ReadEntry(config), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private McpDiscoveryEntry? ReadEntry(McpServerConfig config)
    {
        try
        {
            var path = PathFor(config);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxEntryBytes || info.LinkTarget is not null)
            {
                return null;
            }

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root ||
                root["v"]?.GetValue<int>() != Version ||
                root["cacheKey"]?.GetValue<string>() != CacheKey(config))
            {
                return null;
            }

            return new McpDiscoveryEntry(
                ReadTools(root["tools"] as JsonArray),
                ReadPrompts(root["commands"] as JsonArray),
                ReadResources(root["resources"] as JsonArray))
            {
                SavedAt = root["savedAt"]?.GetValue<long>() ?? 0,
                ConsecutiveRefreshFailures = root["consecutiveRefreshFailures"]?.GetValue<int>() ?? 0,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Writes this server's listing, replacing whatever was there.</summary>
    public void Write(McpServerConfig config, McpDiscoveryEntry entry)
    {
        if (IneligibleReason(config) is not null)
        {
            return;
        }

        var root = new JsonObject
        {
            ["v"] = Version,
            ["serverName"] = config.Name,
            ["cacheKey"] = CacheKey(config),
            ["savedAt"] = entry.SavedAt == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : entry.SavedAt,
            ["consecutiveRefreshFailures"] = entry.ConsecutiveRefreshFailures,
            ["tools"] = WriteTools(entry.Tools),
            ["commands"] = WritePrompts(entry.Prompts),
            ["resources"] = WriteResources(entry.Resources),
        };
        try
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(config);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(SerializerOptions), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written is a cache miss next time, nothing more.
        }
    }

    /// <summary>Records that a refresh failed, so the strike threshold can retire the entry.</summary>
    public void RecordRefreshFailure(McpServerConfig config, McpDiscoveryEntry entry) =>
        Write(config, entry with { ConsecutiveRefreshFailures = entry.ConsecutiveRefreshFailures + 1 });

    /// <summary>Drops this server's entry (its config changed, or it was removed).</summary>
    public void Forget(McpServerConfig config)
    {
        try
        {
            File.Delete(PathFor(config));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IReadOnlyList<McpToolDescriptor> ReadTools(JsonArray? array) =>
    [
        .. (array ?? []).OfType<JsonObject>()
            .Where(entry => entry["name"] is not null)
            .Select(entry => new McpToolDescriptor(
                entry["name"]!.GetValue<string>(),
                entry["description"]?.GetValue<string>() ?? "",
                entry["inputSchema"] as JsonObject ?? [])
            {
                AlwaysLoad = entry["alwaysLoad"]?.GetValue<bool>() ?? false,
                SearchHint = entry["searchHint"]?.GetValue<string>(),
                MaxResultSizeChars = entry["maxResultSizeChars"]?.GetValue<int>(),
                RequiresUserInteraction = entry["requiresUserInteraction"]?.GetValue<bool>() ?? false,
            }),
    ];

    private static JsonArray WriteTools(IReadOnlyList<McpToolDescriptor> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            var entry = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            };
            if (tool.AlwaysLoad)
            {
                entry["alwaysLoad"] = true;
            }

            if (tool.SearchHint is { Length: > 0 } hint)
            {
                entry["searchHint"] = hint;
            }

            if (tool.MaxResultSizeChars is { } size)
            {
                entry["maxResultSizeChars"] = size;
            }

            if (tool.RequiresUserInteraction)
            {
                entry["requiresUserInteraction"] = true;
            }

            array.Add(entry);
        }

        return array;
    }

    private static IReadOnlyList<McpPromptDescriptor> ReadPrompts(JsonArray? array) =>
    [
        .. (array ?? []).OfType<JsonObject>()
            .Where(entry => entry["name"] is not null)
            .Select(entry => new McpPromptDescriptor(
                entry["name"]!.GetValue<string>(),
                entry["description"]?.GetValue<string>(),
                [
                    .. (entry["arguments"] as JsonArray ?? []).OfType<JsonObject>()
                        .Where(argument => argument["name"] is not null)
                        .Select(argument => new McpPromptArgument(
                            argument["name"]!.GetValue<string>(),
                            argument["description"]?.GetValue<string>(),
                            argument["required"]?.GetValue<bool>() ?? false)),
                ])),
    ];

    private static JsonArray WritePrompts(IReadOnlyList<McpPromptDescriptor> prompts)
    {
        var array = new JsonArray();
        foreach (var prompt in prompts)
        {
            var arguments = new JsonArray();
            foreach (var argument in prompt.Arguments)
            {
                arguments.Add(new JsonObject
                {
                    ["name"] = argument.Name,
                    ["description"] = argument.Description,
                    ["required"] = argument.Required,
                });
            }

            array.Add(new JsonObject
            {
                ["name"] = prompt.Name,
                ["description"] = prompt.Description,
                ["arguments"] = arguments,
            });
        }

        return array;
    }

    private static IReadOnlyList<McpResourceDescriptor> ReadResources(JsonArray? array) =>
    [
        .. (array ?? []).OfType<JsonObject>()
            .Where(entry => entry["uri"] is not null)
            .Select(entry => new McpResourceDescriptor(
                entry["uri"]!.GetValue<string>(),
                entry["name"]?.GetValue<string>() ?? "",
                entry["description"]?.GetValue<string>(),
                entry["mimeType"]?.GetValue<string>())),
    ];

    private static JsonArray WriteResources(IReadOnlyList<McpResourceDescriptor> resources)
    {
        var array = new JsonArray();
        foreach (var resource in resources)
        {
            array.Add(new JsonObject
            {
                ["uri"] = resource.Uri,
                ["name"] = resource.Name,
                ["description"] = resource.Description,
                ["mimeType"] = resource.MimeType,
            });
        }

        return array;
    }
}
