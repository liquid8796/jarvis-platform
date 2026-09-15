using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.McpServer.Infrastructure;
namespace Jarvis.Server.Tests;
public sealed class AccessTests
{
    [Fact] public async Task Anonymous_Mcp_exposes_discovery_not_tools()
    {
        using var app=new ServerFixture();using var client=app.Client();
        var response=await client.PostAsJsonAsync("/mcp",new { jsonrpc="2.0",id=1,method="tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        Assert.Contains("resource_metadata",response.Headers.WwwAuthenticate.ToString());
        var metadata=await client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        Assert.Equal("https://jarvis.test/mcp",metadata.GetProperty("resource").GetString());
    }
    [Fact] public async Task Unsafe_cookie_request_requires_CSRF()
    { using var app=new ServerFixture();using var client=app.Client();Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/auth/login",new {email=ServerFixture.AdminEmail,password=ServerFixture.Password})).StatusCode); }
    [Fact] public async Task New_registration_is_pending_and_cannot_sign_in()
    {
        using var app=new ServerFixture();using var client=app.Client();await ServerFixture.Csrf(client);
        (await client.PostAsJsonAsync("/api/auth/register",new{email="pending@example.test",displayName="Pending",password=ServerFixture.Password})).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/auth/login",new{email="pending@example.test",password=ServerFixture.Password})).StatusCode);
    }
    [Fact] public async Task Member_cannot_manage_admin_tools_or_another_users_device()
    {
        using var app=new ServerFixture();using var admin=await app.Admin();
        (await admin.PostAsJsonAsync("/api/admin/users",new{email="member@example.test",displayName="Member",password=ServerFixture.Password,role="user",status="active"})).EnsureSuccessStatusCode();
        var enrollment=await (await admin.PostAsJsonAsync("/api/devices",new{name="Admin PC"})).Content.ReadFromJsonAsync<JsonElement>();
        using var member=app.Client();await ServerFixture.Csrf(member);
        (await member.PostAsJsonAsync("/api/auth/login",new{email="member@example.test",password=ServerFixture.Password})).EnsureSuccessStatusCode();await ServerFixture.Csrf(member);
        Assert.Equal(HttpStatusCode.Forbidden,(await member.GetAsync("/api/admin/tools")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await member.DeleteAsync("/api/devices/"+enrollment.GetProperty("deviceId").GetString())).StatusCode);
    }
    [Fact] public async Task Device_token_is_returned_once_and_rotation_changes_it()
    {
        using var app=new ServerFixture();using var admin=await app.Admin();
        var created=await (await admin.PostAsJsonAsync("/api/devices",new{name="Workstation"})).Content.ReadFromJsonAsync<JsonElement>();
        string token=created.GetProperty("token").GetString()!;Assert.StartsWith("jra_",token);Assert.Equal(68,token.Length);
        Assert.DoesNotContain(token,await admin.GetStringAsync("/api/devices"));
        var changed=await (await admin.PostAsJsonAsync("/api/devices/"+created.GetProperty("deviceId").GetString()+"/rotate",new{})).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(token,changed.GetProperty("token").GetString());
    }
    [Fact] public async Task Current_admin_cannot_delete_self()
    {
        using var app=new ServerFixture();using var admin=await app.Admin();
        var session=await admin.GetFromJsonAsync<JsonElement>("/api/auth/session");
        Assert.Equal(HttpStatusCode.Conflict,(await admin.DeleteAsync("/api/admin/users/"+session.GetProperty("user").GetProperty("id").GetString())).StatusCode);
    }
    [Fact] public async Task Dynamic_registration_rejects_unapproved_callback()
    { using var app=new ServerFixture();using var client=app.Client();Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/connect/register",new{redirect_uris=new[]{"https://untrusted.example/cb"}})).StatusCode); }
    [Fact] public async Task Public_client_identity_does_not_depend_on_untrusted_display_name()
    {
        using var app=new ServerFixture();using var client=app.Client();
        var first=await (await client.PostAsJsonAsync("/connect/register",new{redirect_uris=new[]{"https://client.example/callback"},client_name="A"})).Content.ReadFromJsonAsync<JsonElement>();
        var second=await (await client.PostAsJsonAsync("/connect/register",new{redirect_uris=new[]{"https://client.example/callback"},client_name="B"})).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("client_id").GetString(),second.GetProperty("client_id").GetString());
    }
    [Fact] public void Production_does_not_allow_plain_http() => Assert.Throws<InvalidOperationException>(() => new JarvisOptions{PublicOrigin="http://localhost:18765"}.Validate(false));
}
