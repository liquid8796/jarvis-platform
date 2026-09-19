using System.Text.Json;
using Jarvis.Protocol;
using Jarvis.McpServer.Transport;
using Microsoft.AspNetCore.DataProtection;

namespace Jarvis.Server.Tests;

public sealed class McpSessionContextTests
{
    [Fact]
    public void Handles_are_random_and_bound_to_both_account_and_enrolled_agent()
    {
        dynamic service = Create();
        dynamic first = service.Issue("owner-a", "device-a");
        dynamic second = service.Issue("owner-a", "device-a");
        string handle = first.SessionHandle;
        string id = first.SessionId;
        Assert.True(AgentSessionRules.IsSessionId(id));
        Assert.NotEqual(id, (string)second.SessionId);
        Assert.NotEqual(handle, (string)second.SessionHandle);
        Assert.Equal(id, (string)service.Resolve("owner-a", "device-a", handle));
        Assert.Throws<AgentRequestException>(() => service.Resolve("owner-b", "device-a", handle));
        Assert.Throws<AgentRequestException>(() => service.Resolve("owner-a", "device-b", handle));
        var middle = handle.Length / 2;
        var tampered = handle[..middle] + (handle[middle] == 'a' ? 'b' : 'a') + handle[(middle + 1)..];
        Assert.Throws<AgentRequestException>(() => service.Resolve("owner-a", "device-a", tampered));
    }

    [Fact]
    public void Legacy_expired_handle_does_not_end_an_open_application_session()
    {
        var provider = new EphemeralDataProtectionProvider();
        var service = new McpSessionContext(provider);
        var sessionId = AgentSessionRules.NewSessionId();
        var legacy = JsonSerializer.Serialize(new
        {
            version = 1,
            ownerId = "owner-a",
            deviceId = "device-a",
            sessionId,
            expiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        }, WireJson.Options);
        var handle = provider.CreateProtector("Jarvis.Mcp.ApplicationSession.v1").Protect(legacy);

        Assert.Equal(sessionId, service.Resolve("owner-a", "device-a", handle));
        var current = service.Issue("owner-a", "device-a");
        Assert.Null(current.ExpiresAt);
        Assert.Equal(current.SessionId, service.Resolve("owner-a", "device-a", current.SessionHandle));
    }

    [Fact]
    public void Schema_envelope_is_required_and_removed_before_installed_schema_validation()
    {
        dynamic service = Create();
        var original = WireJson.Element(new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" }, additionalProperties = false });
        JsonElement augmented = service.AugmentSchema(original, true);
        var schema = SchemaGuard.Compile(augmented);
        var withContext = WireJson.Element(new { path = "a.txt", _jarvis = new { sessionHandle = "opaque-handle" } });
        Assert.True(SchemaGuard.Matches(schema, withContext));
        Assert.False(SchemaGuard.Matches(schema, WireJson.Element(new { path = "a.txt" })));
        dynamic extracted = service.Extract(withContext);
        JsonElement clean = extracted.Arguments;
        Assert.Equal("opaque-handle", (string)extracted.SessionHandle);
        Assert.False(clean.TryGetProperty("_jarvis", out _));
        Assert.True(SchemaGuard.Matches(SchemaGuard.Compile(original), clean));
        Assert.False(original.GetProperty("properties").TryGetProperty("_jarvis", out _));
        Assert.Throws<AgentRequestException>(() => service.Extract(WireJson.Element(new { _jarvis = new { sessionHandle = "x", ownerId = "forged" } })));
    }

    private static dynamic Create()
    {
        var type = typeof(Jarvis.McpServer.Transport.McpGateway).Assembly.GetType("Jarvis.McpServer.Transport.McpSessionContext");
        Assert.NotNull(type);
        return Activator.CreateInstance(type, new EphemeralDataProtectionProvider())!;
    }
}
