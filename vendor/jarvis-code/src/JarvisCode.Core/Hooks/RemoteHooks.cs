using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JarvisCode.Core.Hooks;

/// <summary>What an HTTP hook posts to, and with what.</summary>
public sealed record HttpHookSpec(
    string Url,
    IReadOnlyDictionary<string, string>? Headers = null,
    /// <summary>
    /// Environment variable names this hook's header values may interpolate.
    /// Anything else is left empty rather than leaking whatever the process has.
    /// </summary>
    IReadOnlyList<string>? AllowedEnvVars = null);

/// <summary>Which MCP tool an mcp_tool hook calls, and with what input.</summary>
public sealed record McpHookSpec(string Server, string Tool, JsonNode? Input = null);

/// <summary>
/// Host limits on HTTP hooks. Null means "not configured", which the reference
/// reads as no restriction — an empty list, by contrast, allows nothing.
/// </summary>
public sealed record HttpHookPolicy(
    IReadOnlyList<string>? AllowedUrls = null,
    IReadOnlyList<string>? AllowedEnvVars = null);

/// <summary>One HTTP hook call, already resolved: headers interpolated, timeout applied.</summary>
public sealed record HttpHookRequest(
    string Url, string PayloadJson, IReadOnlyDictionary<string, string> Headers, TimeSpan Timeout);

/// <summary>One MCP hook call, with its input already interpolated from the payload.</summary>
public sealed record McpHookRequest(string Server, string Tool, JsonObject Input, TimeSpan Timeout);

/// <summary>
/// What a non-command hook answered. It maps onto the same two things a command
/// hook produces — did it pass, and what did it say — so every event path treats
/// the three kinds alike.
/// </summary>
public sealed record HookTransportResult(
    bool Ok, string Body, int? StatusCode = null, string? Error = null, bool Aborted = false);

/// <summary>Posts an HTTP hook's payload. The host owns the socket; this file owns the rules.</summary>
public delegate Task<HookTransportResult> HttpHookSender(
    HttpHookRequest request, CancellationToken cancellationToken);

/// <summary>Calls an MCP server's tool for a hook. Null answers "no MCP in this host".</summary>
public delegate Task<HookTransportResult> McpHookCaller(
    McpHookRequest request, CancellationToken cancellationToken);

/// <summary>
/// The rules around the two hook kinds that leave the machine or the process:
/// which URLs may be posted to, which environment variables a header may carry,
/// which addresses are refused, and how a hook's input picks values out of the
/// payload it was handed.
/// </summary>
public static class RemoteHooks
{
    /// <summary>The reference's default timeout for one HTTP or MCP hook call.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Events that never run HTTP hooks: they fire before or outside a turn, where
    /// the reference refuses them outright rather than posting.
    /// </summary>
    public static bool AllowsHttpHooks(HookEvent hookEvent) =>
        hookEvent is not (HookEvent.SessionStart or HookEvent.Setup);

    public static string HttpNotSupported(string url, HookEvent hookEvent) =>
        $"Skipping HTTP hook {url} — HTTP hooks are not supported for {EventName(hookEvent)}";

    public static string UrlNotAllowed(string url) =>
        $"HTTP hook blocked: {url} does not match any pattern in allowedHttpHookUrls";

    public static string AddressBlocked(string host, string address) =>
        $"HTTP hook blocked: {host} resolves to {address} (private/link-local address). " +
        "Loopback (127.0.0.1, ::1) is allowed for local dev.";

    public static string McpUnavailable(HookEvent hookEvent) =>
        $"mcp_tool hooks are not available for the '{EventName(hookEvent)}' hook event (no MCP client context)";

    public static string McpNotConnected(string server) => $"MCP server '{server}' not connected";

    /// <summary>The reference's PascalCase event names, which its messages quote.</summary>
    public static string EventName(HookEvent hookEvent) => hookEvent switch
    {
        HookEvent.PreToolUse => "PreToolUse",
        HookEvent.PostToolUse => "PostToolUse",
        HookEvent.PostToolUseFailure => "PostToolUseFailure",
        HookEvent.PostToolBatch => "PostToolBatch",
        HookEvent.UserPromptSubmit => "UserPromptSubmit",
        HookEvent.UserPromptExpansion => "UserPromptExpansion",
        HookEvent.SessionStart => "SessionStart",
        HookEvent.SessionEnd => "SessionEnd",
        HookEvent.Stop => "Stop",
        HookEvent.StopFailure => "StopFailure",
        HookEvent.SubagentStop => "SubagentStop",
        HookEvent.SubagentStart => "SubagentStart",
        HookEvent.Notification => "Notification",
        HookEvent.PreCompact => "PreCompact",
        HookEvent.PostCompact => "PostCompact",
        HookEvent.PermissionRequest => "PermissionRequest",
        HookEvent.PermissionDenied => "PermissionDenied",
        HookEvent.Setup => "Setup",
        HookEvent.Elicitation => "Elicitation",
        HookEvent.ElicitationResult => "ElicitationResult",
        HookEvent.ConfigChange => "ConfigChange",
        HookEvent.WorktreeCreate => "WorktreeCreate",
        HookEvent.WorktreeRemove => "WorktreeRemove",
        HookEvent.InstructionsLoaded => "InstructionsLoaded",
        HookEvent.CwdChanged => "CwdChanged",
        HookEvent.FileChanged => "FileChanged",
        HookEvent.DirectoryAdded => "DirectoryAdded",
        HookEvent.MessageDisplay => "MessageDisplay",
        HookEvent.TaskCreated => "TaskCreated",
        HookEvent.TaskCompleted => "TaskCompleted",
        HookEvent.TeammateIdle => "TeammateIdle",
        HookEvent.PreModelSwitch => "PreModelSwitch",
        HookEvent.PostModelSwitch => "PostModelSwitch",
        HookEvent.TurnCompleted => "TurnCompleted",
        _ => hookEvent.ToString(),
    };

    /// <summary>
    /// Whether a URL matches one of the configured patterns. A pattern is a
    /// literal URL or a glob over one, so a single entry can cover a host's paths
    /// without listing every endpoint.
    /// </summary>
    public static bool IsUrlAllowed(string url, IReadOnlyList<string>? allowedUrls)
    {
        if (allowedUrls is null)
        {
            return true;
        }

        foreach (var pattern in allowedUrls)
        {
            if (pattern.Length == 0)
            {
                continue;
            }

            if (!pattern.Contains('*', StringComparison.Ordinal))
            {
                if (string.Equals(pattern, url, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            var expression = "^" + string.Join(
                ".*", pattern.Split('*').Select(Regex.Escape)) + "$";
            if (Regex.IsMatch(url, expression, RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Regex EnvReference =
        new(@"\$\{([A-Z_][A-Z0-9_]*)\}|\$([A-Z_][A-Z0-9_]*)", RegexOptions.Compiled);

    /// <summary>
    /// Resolves a header value's environment references. Only names the hook
    /// declared — intersected with the host's own list when it has one — are
    /// substituted; every other reference resolves to nothing, so a header cannot
    /// be used to read the environment by guessing names. Carriage returns, line
    /// feeds and NULs are stripped afterwards: a header value must not be able to
    /// start a second header.
    /// </summary>
    public static string InterpolateHeaderValue(
        string value, IReadOnlySet<string> allowed, Func<string, string?> environment)
    {
        var substituted = EnvReference.Replace(value, match =>
        {
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return allowed.Contains(name) ? environment(name) ?? "" : "";
        });

        return string.Concat(substituted.Where(c => c is not ('\r' or '\n' or '\0')));
    }

    /// <summary>The names a hook may interpolate: its own list, narrowed by the host's.</summary>
    public static IReadOnlySet<string> AllowedEnvNames(
        IReadOnlyList<string>? hookNames, IReadOnlyList<string>? policyNames)
    {
        var names = hookNames ?? [];
        if (policyNames is not null)
        {
            names = [.. names.Where(policyNames.Contains)];
        }

        return names.ToHashSet(StringComparer.Ordinal);
    }

    private static readonly Regex PathReference =
        new(@"\$\{([a-zA-Z_][a-zA-Z0-9_.]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// Fills an mcp_tool hook's input from the hook payload: every string value
    /// may name a dotted path into it. A path that resolves to nothing becomes an
    /// empty string, an object or array becomes its JSON — the reference's rule,
    /// so a hook never posts the literal placeholder.
    /// </summary>
    public static JsonObject InterpolateInput(JsonNode? input, JsonObject payload)
    {
        var filled = Fill(input, payload);
        return filled as JsonObject ?? [];
    }

    private static JsonNode? Fill(JsonNode? node, JsonObject payload) => node switch
    {
        JsonObject o => FillObject(o, payload),
        JsonArray a => FillArray(a, payload),
        JsonValue v when v.TryGetValue<string>(out var text) => JsonValue.Create(FillText(text, payload)),
        _ => node?.DeepClone(),
    };

    private static JsonNode FillObject(JsonObject source, JsonObject payload)
    {
        var result = new JsonObject();
        foreach (var (key, value) in source)
        {
            result[key] = Fill(value, payload);
        }

        return result;
    }

    private static JsonNode FillArray(JsonArray source, JsonObject payload)
    {
        var result = new JsonArray();
        foreach (var item in source)
        {
            result.Add(Fill(item, payload));
        }

        return result;
    }

    private static string FillText(string text, JsonObject payload) =>
        PathReference.Replace(text, match =>
        {
            var value = Resolve(match.Groups[1].Value, payload);
            return value switch
            {
                null => "",
                JsonValue scalar => scalar.TryGetValue<string>(out var s) ? s : scalar.ToJsonString(),
                _ => value.ToJsonString(),
            };
        });

    private static JsonNode? Resolve(string path, JsonObject payload)
    {
        JsonNode? current = payload;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject o || !o.TryGetPropertyValue(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>
    /// True for an address a hook may not post to: anything private, link-local,
    /// unique-local or otherwise not routable on the public internet. Loopback is
    /// allowed, because a hook served from the machine itself is the normal case
    /// during development.
    /// </summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            return octets[0] switch
            {
                10 => true,
                127 => true,
                169 when octets[1] == 254 => true,
                172 when octets[1] >= 16 && octets[1] <= 31 => true,
                192 when octets[1] == 168 => true,
                100 when octets[1] >= 64 && octets[1] <= 127 => true,
                0 or >= 224 => true,
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                // fc00::/7 — unique local.
                return true;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                return IsBlockedAddress(address.MapToIPv4());
            }

            return false;
        }

        return true;
    }
}
