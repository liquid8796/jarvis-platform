using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Protocol;
using Microsoft.AspNetCore.DataProtection;

namespace Jarvis.McpServer.Transport;

/// <summary>
/// Application sessions are independent of MCP transport sessions. Protected handles are only
/// correlation capabilities: every call still requires live OAuth, device and local tool authorization.
/// </summary>
public sealed class McpSessionContext
{
    public const int HandleLifetimeDays = 30;
    private readonly IDataProtector _protector;

    public McpSessionContext(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Jarvis.Mcp.ApplicationSession.v1");

    public IssuedMcpSession Issue(string ownerId, string deviceId)
    {
        ValidateOwner(ownerId, deviceId);
        var claims = new SessionClaims(1, ownerId, deviceId, AgentSessionRules.NewSessionId(), DateTimeOffset.UtcNow.AddDays(HandleLifetimeDays));
        return new(claims.SessionId, _protector.Protect(JsonSerializer.Serialize(claims, WireJson.Options)), claims.ExpiresAt);
    }

    public string Resolve(string ownerId, string deviceId, string? handle)
    {
        ValidateOwner(ownerId, deviceId);
        if (string.IsNullOrWhiteSpace(handle))
            throw new AgentRequestException("SESSION_REQUIRED", "Call session__open once per chat and include _jarvis.sessionHandle on subsequent tool calls.");
        if (handle.Length > 4096) throw Invalid();
        try
        {
            var claims = JsonSerializer.Deserialize<SessionClaims>(_protector.Unprotect(handle), WireJson.Options);
            if (claims is null || claims.Version != 1 || !AgentSessionRules.IsSessionId(claims.SessionId) ||
                !StringComparer.Ordinal.Equals(claims.OwnerId, ownerId) || !StringComparer.Ordinal.Equals(claims.DeviceId, deviceId))
                throw Invalid();
            if (claims.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new AgentRequestException("SESSION_EXPIRED", "Open a new session. The previous handle has expired; no operation was dispatched.");
            return claims.SessionId;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
        { throw Invalid(); }
    }

    public JsonElement AugmentSchema(JsonElement installedSchema, bool required)
    {
        var schema = JsonNode.Parse(installedSchema.GetRawText())?.AsObject() ?? throw new ArgumentException("Object schema required.");
        var properties = schema["properties"] as JsonObject;
        if (properties is null) { properties = new JsonObject(); schema["properties"] = properties; }
        if (properties.ContainsKey("_jarvis")) throw new InvalidOperationException("The installed tool uses reserved session metadata '_jarvis'.");
        properties["_jarvis"] = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JsonArray("sessionHandle"),
            ["description"] = "Private context of this chat. Retain the handle from session__open; never copy another chat's handle.",
            ["properties"] = new JsonObject
            {
                ["sessionHandle"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4096 }
            }
        };
        if (required)
        {
            var requiredProperties = schema["required"] as JsonArray;
            if (requiredProperties is null) { requiredProperties = new JsonArray(); schema["required"] = requiredProperties; }
            if (!requiredProperties.Any(value => value?.GetValue<string>() == "_jarvis")) requiredProperties.Add("_jarvis");
        }
        return JsonSerializer.SerializeToElement(schema, WireJson.Options);
    }

    public ExtractedMcpSession Extract(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be an object.");
        var clean = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        string? handle = null;
        if (clean.Remove("_jarvis", out var metadata))
        {
            if (metadata is not JsonObject envelope || envelope.Count != 1 ||
                envelope["sessionHandle"] is not JsonValue value || !value.TryGetValue<string>(out handle) ||
                string.IsNullOrWhiteSpace(handle) || handle.Length > 4096)
                throw new AgentRequestException("SESSION_INVALID", "_jarvis must contain only a non-empty sessionHandle returned by session__open.");
        }
        return new(JsonSerializer.SerializeToElement(clean, WireJson.Options), handle);
    }

    private static void ValidateOwner(string ownerId, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 256 || string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 100)
            throw new AgentRequestException("SESSION_INVALID", "Authenticated owner and enrolled device are required.");
    }
    private static AgentRequestException Invalid() => new("SESSION_INVALID", "The session handle is invalid for this authenticated account and enrolled agent. No command was dispatched.");
    private sealed record SessionClaims(int Version, string OwnerId, string DeviceId, string SessionId, DateTimeOffset ExpiresAt);
}

public sealed record IssuedMcpSession(string SessionId, string SessionHandle, DateTimeOffset ExpiresAt);
public sealed record ExtractedMcpSession(JsonElement Arguments, string? SessionHandle);
