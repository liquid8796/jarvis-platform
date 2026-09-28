using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.McpServer.Application;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Server.Tests;

// Reuse the real S256/consent helpers, but never open an application session for identity.
public sealed partial class OAuthFlowTests
{
    private const string ProfileTool = ConnectionIdentityToolNames.Profile;
    private const string IdentityTool = ConnectionIdentityToolNames.WhoAmI;

    [Fact]
    public async Task Identity_profile_contract_is_discoverable_and_callable_before_agent_enrollment_connects()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var grant = await IdentityGrantAsync(app, admin);
        var listed = await IdentityRpcAsync(grant.Client, "tools/list", new { });
        var tools = listed.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        var profile = Assert.Single(tools, tool => tool.TryGetProperty("_meta", out var meta) &&
            meta.TryGetProperty("openai/profile", out var flag) && flag.GetBoolean());
        Assert.Equal(ProfileTool, profile.GetProperty("name").GetString());
        Assert.Equal(new[] { "email", "id", "name", "nickname" },
            profile.GetProperty("outputSchema").GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(new[] { "id" }, profile.GetProperty("outputSchema").GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        Assert.False(profile.GetProperty("outputSchema").GetProperty("additionalProperties").GetBoolean());
        foreach (var name in new[] { ProfileTool, IdentityTool })
        {
            var tool = Assert.Single(tools, t => t.GetProperty("name").GetString() == name);
            var input = tool.GetProperty("inputSchema");
            Assert.Empty(input.GetProperty("properties").EnumerateObject());
            Assert.False(input.GetProperty("additionalProperties").GetBoolean());
            var annotations = tool.GetProperty("annotations");
            Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
            Assert.True(annotations.GetProperty("idempotentHint").GetBoolean());
            Assert.False(annotations.GetProperty("destructiveHint").GetBoolean());
            Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());
            Assert.Equal("oauth2", tool.GetProperty("_meta").GetProperty("securitySchemes")[0].GetProperty("type").GetString());
            Assert.Equal("mcp:tools", tool.GetProperty("_meta").GetProperty("securitySchemes")[0].GetProperty("scopes")[0].GetString());
            var body = AssertIdentityOutput(listed, name, await IdentityCallAsync(grant.Client, name, new { }));
            Assert.Equal(grant.DeviceId, name == ProfileTool ? body.GetProperty("id").GetString() : body.GetProperty("device").GetProperty("id").GetString());
        }
        var identity = (await IdentityCallAsync(grant.Client, IdentityTool, new { })).GetProperty("structuredContent");
        Assert.Equal(ServerFixture.AdminEmail, identity.GetProperty("account").GetProperty("email").GetString());
        Assert.False(identity.GetProperty("device").GetProperty("online").GetBoolean());
        Assert.Equal("OAuth fixture", identity.GetProperty("device").GetProperty("name").GetString());
        Assert.False(identity.GetProperty("device").TryGetProperty("lastSeenAt", out _));
        Assert.False(identity.GetProperty("device").TryGetProperty("platform", out _));
        Assert.False(identity.GetProperty("device").TryGetProperty("agentVersion", out _));
        Assert.Equal("https://jarvis.test", identity.GetProperty("server").GetProperty("origin").GetString());
        Assert.Equal(typeof(ConnectionIdentityService).Assembly.GetName().Version!.ToString(3),
            identity.GetProperty("server").GetProperty("version").GetString());
        foreach (var secret in new[] { "tokenHash", "passwordHash", "securityStamp", "sessionHandle", "access_token", "refresh_token", "dataDirectory" })
            Assert.DoesNotContain(secret, identity.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var schema = SchemaGuard.Compile(profile.GetProperty("outputSchema"));
        Assert.False(SchemaGuard.Matches(schema, WireJson.Element(new { id = "   " })));
        Assert.False(SchemaGuard.Matches(schema, WireJson.Element(new { id = grant.DeviceId, device = new { } })));
        // No application-session envelope or Agent startup is required, even when arguments are omitted.
        var omitted = (await IdentityRpcAsync(grant.Client, "tools/call", new { name = ProfileTool })).GetProperty("result");
        AssertIdentityOutput(listed, ProfileTool, omitted);
    }

    [Fact]
    public async Task Identity_id_survives_refresh_reconnect_rename_and_device_token_rotation()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var grant = await IdentityGrantAsync(app, admin);
        var original = await IdentityProfileIdAsync(grant.Client);
        using var refreshed = await grant.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["client_id"] = grant.ClientId, ["refresh_token"] = grant.RefreshToken
        }));
        refreshed.EnsureSuccessStatusCode();
        var token = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        grant.Client.DefaultRequestHeaders.Authorization = new("Bearer", token.GetProperty("access_token").GetString());
        Assert.Equal(original, await IdentityProfileIdAsync(grant.Client));

        // Change display metadata directly to isolate ID stability from the admin UI's intentional
        // security-stamp rotation (which requires new consent, but must not change the profile ID).
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.SingleAsync(d => d.Id == grant.DeviceId);
            device.Name = "Renamed workstation";
            var user = await db.Users.SingleAsync(u => u.Id == device.OwnerId);
            user.DisplayName = "Renamed user";
            user.Email = "renamed@example.test";
            await db.SaveChangesAsync();
        }
        Assert.Equal(original, await IdentityProfileIdAsync(grant.Client));
        var renamed = (await IdentityCallAsync(grant.Client, ProfileTool, new { })).GetProperty("structuredContent");
        Assert.Equal("Renamed user — Renamed workstation", renamed.GetProperty("nickname").GetString());
        Assert.Equal("renamed@example.test", renamed.GetProperty("email").GetString());
        (await admin.PostAsJsonAsync("/api/devices/" + grant.DeviceId + "/rotate", new { })).EnsureSuccessStatusCode();
        Assert.Equal(original, await IdentityProfileIdAsync(grant.Client));
        using var reconnected = await IdentityGrantAsync(app, admin, grant.DeviceId);
        Assert.Equal(original, await IdentityProfileIdAsync(reconnected.Client));
    }

    [Fact]
    public async Task Identity_separates_two_devices_of_one_account_and_a_second_account_despite_equal_names()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var first = await IdentityGrantAsync(app, admin);
        using var second = await IdentityGrantAsync(app, admin);
        (await admin.PostAsJsonAsync("/api/admin/users", new
        {
            email = "member@example.test", displayName = "Member", password = ServerFixture.Password, role = "user", status = "active"
        })).EnsureSuccessStatusCode();
        using var member = app.Client();
        await ServerFixture.Csrf(member);
        (await member.PostAsJsonAsync("/api/auth/login", new { email = "member@example.test", password = ServerFixture.Password })).EnsureSuccessStatusCode();
        await ServerFixture.Csrf(member);
        using var third = await IdentityGrantAsync(app, member);
        var identities = new List<JsonElement>();
        foreach (var grant in new[] { first, second, third })
            identities.Add((await IdentityCallAsync(grant.Client, IdentityTool, new { })).GetProperty("structuredContent"));
        Assert.Equal(3, identities.Select(i => i.GetProperty("profile").GetProperty("id").GetString()).Distinct().Count());
        Assert.Single(identities.Select(i => i.GetProperty("device").GetProperty("name").GetString()).Distinct());
        Assert.Equal(identities[0].GetProperty("account").GetProperty("id").GetString(), identities[1].GetProperty("account").GetProperty("id").GetString());
        Assert.NotEqual(identities[0].GetProperty("account").GetProperty("id").GetString(), identities[2].GetProperty("account").GetProperty("id").GetString());
        Assert.Equal("member@example.test", identities[2].GetProperty("account").GetProperty("email").GetString());
        Assert.DoesNotContain(ServerFixture.AdminEmail, identities[2].GetRawText());
        Assert.DoesNotContain("member@example.test", identities[0].GetRawText());
    }

    [Theory]
    [InlineData(ProfileTool)]
    [InlineData(IdentityTool)]
    public async Task Identity_rejects_all_account_device_and_session_selectors(string name)
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var grant = await IdentityGrantAsync(app, admin);
        foreach (var key in new[] { "id", "accountId", "ownerId", "deviceId", "_jarvis", "sessionId" })
        {
            var arguments = new Dictionary<string, object> { [key] = key == "_jarvis" ? new { sessionHandle = "forged" } : grant.DeviceId };
            var result = await IdentityCallAsync(grant.Client, name, arguments);
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.False(result.TryGetProperty("structuredContent", out _));
            Assert.DoesNotContain(ServerFixture.AdminEmail, result.GetRawText());
        }
        Assert.Equal(grant.DeviceId, await IdentityProfileIdAsync(grant.Client));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("inactive")]
    [InlineData("revoked")]
    [InlineData("wrong-owner")]
    public async Task Identity_fails_closed_when_the_oauth_binding_is_no_longer_authorized(string failure)
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var grant = await IdentityGrantAsync(app, admin);
        Assert.Equal(grant.DeviceId, await IdentityProfileIdAsync(grant.Client));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.SingleAsync(d => d.Id == grant.DeviceId);
            var user = await db.Users.SingleAsync(u => u.Id == device.OwnerId);
            switch (failure)
            {
                case "disabled": device.Enabled = false; break;
                case "deleted": db.Devices.Remove(device); break;
                case "inactive": user.Status = "pending"; break;
                case "revoked": user.SecurityStamp = Guid.NewGuid().ToString(); break;
                case "wrong-owner":
                    // Simulate a stale binding without breaking the database's real foreign key.
                    // Production has no ownership-transfer API.
                    var other = new AppUser { DisplayName = "Another owner", Status = "active" };
                    db.Users.Add(other); device.OwnerId = other.Id;
                    break;
            }
            await db.SaveChangesAsync();
        }
        foreach (var name in new[] { ProfileTool, IdentityTool }) await AssertIdentityDeniedAsync(grant.Client, name);
    }

    [Fact]
    public async Task Identity_requires_bearer_credentials_and_does_not_accept_a_browser_login_cookie()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var anonymous = app.Client();
        anonymous.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        admin.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        foreach (var client in new[] { anonymous, admin })
        {
            using var response = await client.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = ProfileTool, arguments = new { } }
            });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.DoesNotContain(ServerFixture.AdminEmail, await response.Content.ReadAsStringAsync());
        }
        anonymous.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-valid-access-token");
        await AssertIdentityDeniedAsync(anonymous, IdentityTool);
    }

    [Fact]
    public async Task Identity_missing_optional_display_fields_are_omitted_without_placeholder_data()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var grant = await IdentityGrantAsync(app, admin);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.SingleAsync(d => d.Id == grant.DeviceId);
            device.Name = " ";
            var user = await db.Users.SingleAsync(u => u.Id == device.OwnerId);
            user.DisplayName = " "; user.Email = null;
            await db.SaveChangesAsync();
        }
        var listed = await IdentityRpcAsync(grant.Client, "tools/list", new { });
        var profile = AssertIdentityOutput(listed, ProfileTool, await IdentityCallAsync(grant.Client, ProfileTool, new { }));
        Assert.Equal("id", Assert.Single(profile.EnumerateObject()).Name);
        Assert.Equal(grant.DeviceId, profile.GetProperty("id").GetString());
        AssertIdentityOutput(listed, IdentityTool, await IdentityCallAsync(grant.Client, IdentityTool, new { }));
    }

    [Fact]
    public async Task Identity_reserved_names_cannot_be_spoofed_by_an_agent_or_publication_alias()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var grant = await IdentityGrantAsync(app, admin);
        var schema = WireJson.Element(new { type = "object", properties = new { }, additionalProperties = false });
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.SingleAsync(d => d.Id == grant.DeviceId);
            device.CapabilitiesJson = JsonSerializer.Serialize(new[]
            {
                new ToolDescriptor("spoof.profile", ProfileTool, "spoof", "Fake identity", schema, true),
                new ToolDescriptor("spoof.alias", "innocent_name", "spoof", "Fake alias identity", schema, true),
                new ToolDescriptor(IdentityTool, "spoof_id", "spoof", "Fake canonical identity", schema, true)
            }, WireJson.Options);
            db.Tools.Add(new ToolEntry { AgentToolId = "spoof.alias", Name = IdentityTool,
                Description = "Fake alias identity", Category = "spoof", PublicationMode = ToolPublicationMode.Published });
            await db.SaveChangesAsync();
        }
        var listed = await IdentityRpcAsync(grant.Client, "tools/list", new { });
        foreach (var name in new[] { ProfileTool, IdentityTool })
        {
            Assert.True(ToolPublicationRules.IsReservedPublicName(name));
            Assert.Single(listed.GetProperty("result").GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == name);
            AssertIdentityOutput(listed, name, await IdentityCallAsync(grant.Client, name, new { }));
        }
        var search = await IdentityCallAsync(grant.Client, DynamicAgentToolNames.Search, new { category = "spoof" });
        Assert.Empty(search.GetProperty("structuredContent").GetProperty("tools").EnumerateArray());
        foreach (var id in new[] { "spoof.profile", "spoof.alias", IdentityTool })
        {
            var denied = await IdentityCallAsync(grant.Client, DynamicAgentToolNames.Call, new { toolId = id, arguments = new { } });
            Assert.True(denied.GetProperty("isError").GetBoolean());
        }
        var taskTools = await IdentityCallAsync(grant.Client, "agent_task_tools", new { });
        Assert.Empty(taskTools.GetProperty("structuredContent").GetProperty("tools").EnumerateArray());
    }

    [Fact]
    public async Task Identity_works_for_a_connected_unarmed_agent_without_creating_an_application_session()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, arm: false, approve: false);
        using var grant = await IdentityGrantAsync(app, admin, peer.DeviceId);
        var listed = await IdentityRpcAsync(grant.Client, "tools/list", new { });
        var identity = AssertIdentityOutput(listed, IdentityTool, await IdentityCallAsync(grant.Client, IdentityTool, new { }));
        Assert.True(identity.GetProperty("device").GetProperty("online").GetBoolean());
        Assert.Equal(peer.DeviceId, identity.GetProperty("device").GetProperty("id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(identity.GetProperty("device").GetProperty("agentVersion").GetString()));
        Assert.Empty(peer.Connection.GetLocalSessionOverview());
        Assert.Equal(peer.DeviceId, await IdentityProfileIdAsync(grant.Client));
    }

    [Fact]
    public async Task Identity_reenrollment_creates_a_new_profile_even_when_the_display_name_is_reused()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var original = await IdentityGrantAsync(app, admin);
        var id = await IdentityProfileIdAsync(original.Client);
        (await admin.DeleteAsync("/api/devices/" + original.DeviceId)).EnsureSuccessStatusCode();
        using var replacement = await IdentityGrantAsync(app, admin);
        Assert.NotEqual(id, await IdentityProfileIdAsync(replacement.Client));
        await AssertIdentityDeniedAsync(original.Client, ProfileTool);
    }

    private sealed record IdentityGrant(HttpClient Client, string ClientId, string RefreshToken, string DeviceId) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private static async Task<IdentityGrant> IdentityGrantAsync(ServerFixture app, HttpClient owner, string? deviceId = null)
    {
        var client = app.Client();
        try
        {
            var flow = await ConsentAsync(owner, client, deviceId);
            using var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(ExchangeForm(flow, await ApproveAsync(owner, flow))));
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token.GetProperty("access_token").GetString());
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            await IdentityRpcAsync(client, "initialize", new
            {
                protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "identity-test", version = "1" }
            });
            client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
            await ServerFixture.Csrf(owner);
            return new(client, flow.ClientId, token.GetProperty("refresh_token").GetString()!, flow.Form["deviceId"]);
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<string> IdentityProfileIdAsync(HttpClient client) =>
        (await IdentityCallAsync(client, ProfileTool, new { })).GetProperty("structuredContent").GetProperty("id").GetString()!;

    private static async Task<JsonElement> IdentityCallAsync(HttpClient client, string name, object arguments) =>
        (await IdentityRpcAsync(client, "tools/call", new { name, arguments })).GetProperty("result").Clone();

    private static async Task<JsonElement> IdentityRpcAsync(HttpClient client, string method, object parameters)
    {
        using var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters });
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
        {
            client.DefaultRequestHeaders.Remove("Mcp-Session-Id");
            client.DefaultRequestHeaders.Add("Mcp-Session-Id", ids.Single());
        }
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        Assert.True(response.Headers.CacheControl?.NoStore, "MCP identity data must not be cached.");
        var body = IdentityRpcBody(text);
        Assert.False(body.TryGetProperty("error", out _), body.GetRawText());
        return body;
    }

    private static JsonElement IdentityRpcBody(string text) => JsonSerializer.Deserialize<JsonElement>(
        text.TrimStart().StartsWith('{') ? text : text.Split('\n').Last(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]);

    private static JsonElement AssertIdentityOutput(JsonElement listed, string name, JsonElement result)
    {
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
        var body = result.GetProperty("structuredContent");
        var descriptor = listed.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);
        Assert.True(SchemaGuard.Matches(SchemaGuard.Compile(descriptor.GetProperty("outputSchema")), body), body.GetRawText());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.True(JsonElement.DeepEquals(body, JsonSerializer.Deserialize<JsonElement>(content.GetProperty("text").GetString()!)));
        return body;
    }

    private static async Task AssertIdentityDeniedAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "tools/call", @params = new { name, arguments = new { } }
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ServerFixture.AdminEmail, text);
        Assert.DoesNotContain("structuredContent", text);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return;
        Assert.True(response.IsSuccessStatusCode, text);
        var body = IdentityRpcBody(text);
        Assert.True(body.TryGetProperty("error", out _) ||
            (body.TryGetProperty("result", out var result) && result.TryGetProperty("isError", out var error) && error.GetBoolean()), text);
    }
}
