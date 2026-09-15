using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jarvis.Server.Tests;

public sealed class OAuthDiscoveryTests
{
    [Theory]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/openid-configuration")]
    public async Task Discovery_advertises_the_existing_public_DCR_flow(string route)
    {
        using var app = new ServerFixture();
        using var client = app.Client();
        var metadata = await client.GetFromJsonAsync<JsonElement>(route);
        Assert.Equal("https://jarvis.test/", metadata.GetProperty("issuer").GetString());
        Assert.Equal("https://jarvis.test/connect/register", metadata.GetProperty("registration_endpoint").GetString());
        Assert.Equal("https://jarvis.test/connect/authorize", metadata.GetProperty("authorization_endpoint").GetString());
        Assert.Equal("https://jarvis.test/connect/token", metadata.GetProperty("token_endpoint").GetString());
        Assert.Contains("none", Values(metadata, "token_endpoint_auth_methods_supported"));
        Assert.Equal(new[] { "S256" }, Values(metadata, "code_challenge_methods_supported"));
        Assert.Contains("mcp:tools", Values(metadata, "scopes_supported"));
        Assert.Contains("offline_access", Values(metadata, "scopes_supported"));
        Assert.DoesNotContain("openid", Values(metadata, "scopes_supported"));
        Assert.False(metadata.TryGetProperty("client_id_metadata_document_supported", out var cimd) && cimd.GetBoolean());
    }

    [Fact]
    public async Task Registration_from_discovery_creates_a_public_client_without_a_secret()
    {
        using var app = new ServerFixture();
        using var client = app.Client();
        var metadata = await client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        var endpoint = new Uri(metadata.GetProperty("registration_endpoint").GetString()!).PathAndQuery;
        var response = await client.PostAsJsonAsync(endpoint, new
        {
            redirect_uris = new[] { "https://client.example/callback" },
            client_name = "MCP discovery fixture", token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" }, response_types = new[] { "code" }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registration = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("jarvis_", registration.GetProperty("client_id").GetString());
        Assert.Equal("none", registration.GetProperty("token_endpoint_auth_method").GetString());
        Assert.False(registration.TryGetProperty("client_secret", out _));
    }

    [Theory]
    [InlineData("https://client.example/callback/other")]
    [InlineData("https://chatgpt.com/connector/oauth/not-allowlisted")]
    [InlineData("https://chatgpt.com.attacker.example/connector/oauth/fixture")]
    public async Task Discovery_does_not_relax_exact_callback_allowlists(string redirect)
    {
        using var app = new ServerFixture();
        using var client = app.Client();
        var response = await client.PostAsJsonAsync("/connect/register", new
        {
            redirect_uris = new[] { redirect }, token_endpoint_auth_method = "none"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_redirect_uri", error.GetProperty("error").GetString());
    }

    private static string[] Values(JsonElement metadata, string field) =>
        metadata.GetProperty(field).EnumerateArray().Select(value => value.GetString()!).ToArray();
}
