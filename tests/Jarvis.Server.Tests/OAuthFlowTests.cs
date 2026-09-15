using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
namespace Jarvis.Server.Tests;

public sealed class OAuthFlowTests
{
    private const string Resource = "https://jarvis.test/mcp";
    private const string Callback = "https://client.example/callback";

    [Fact]
    public async Task Resource_bound_S256_code_flow_issues_access_token()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var client = app.Client();
        var flow = await ConsentAsync(admin, client);
        foreach (var name in new[] { "client_id", "redirect_uri", "response_type", "scope", "resource", "state", "code_challenge", "code_challenge_method" })
            Assert.True(flow.Form.ContainsKey(name), $"Consent form omitted {name}.");
        Assert.Equal("fixture-state&<quoted>\"", flow.Form["state"]);
        var code = await ApproveAsync(admin, flow);
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(ExchangeForm(flow, code)));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(token.GetProperty("access_token").GetString()));
        Assert.True(token.TryGetProperty("refresh_token", out _));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.GetProperty("access_token").GetString());
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        var initialized = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "jarvis-test", version = "1.0" } }
        });
        initialized.EnsureSuccessStatusCode();
        Assert.Contains("jarvis-mcp-server", await initialized.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
        var listed = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        listed.EnsureSuccessStatusCode();
        Assert.Contains("\"tools\"", await listed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Consent_requires_its_antiforgery_token()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var client = app.Client();
        var flow = await ConsentAsync(admin, client);
        flow.Form.Remove("__RequestVerificationToken");
        var response = await admin.PostAsync(flow.Action, new FormUrlEncodedContent(flow.Form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Denied_consent_returns_access_denied_and_preserves_state()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var client = app.Client();
        var flow = await ConsentAsync(admin, client);
        flow.Form["decision"] = "deny";
        var response = await admin.PostAsync(flow.Action, new FormUrlEncodedContent(flow.Form));
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.Equal(flow.Form["state"], query["state"].ToString());
        Assert.False(query.ContainsKey("code"));
    }

    [Theory]
    [InlineData("missing-verifier")]
    [InlineData("wrong-verifier")]
    [InlineData("wrong-resource")]
    public async Task Token_exchange_rejects_invalid_binding(string failure)
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var client = app.Client();
        var flow = await ConsentAsync(admin, client);
        var form = ExchangeForm(flow, await ApproveAsync(admin, flow));
        if (failure == "missing-verifier") form.Remove("code_verifier");
        if (failure == "wrong-verifier") form["code_verifier"] = new string('x', 43);
        if (failure == "wrong-resource") form["resource"] = "https://other.example/mcp";
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("\"access_token\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Authorization_code_cannot_be_replayed()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var client = app.Client();
        var flow = await ConsentAsync(admin, client);
        var form = ExchangeForm(flow, await ApproveAsync(admin, flow));
        (await client.PostAsync("/connect/token", new FormUrlEncodedContent(form))).EnsureSuccessStatusCode();
        var replay = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.DoesNotContain("\"access_token\"", await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Refresh_token_accepts_omitted_resource_without_changing_the_grant()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var client = app.Client();
        var flow = await ConsentAsync(admin, client);
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(ExchangeForm(flow, await ApproveAsync(admin, flow))));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();
        var refresh = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["client_id"] = flow.ClientId,
            ["refresh_token"] = token.GetProperty("refresh_token").GetString()!
        }));
        refresh.EnsureSuccessStatusCode();
        Assert.False(string.IsNullOrEmpty((await refresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()));
    }

    private static async Task<ConsentFlow> ConsentAsync(HttpClient admin, HttpClient client)
    {
        var enrollment = await admin.PostAsJsonAsync("/api/devices", new { name = "OAuth fixture" });
        enrollment.EnsureSuccessStatusCode();
        var device = await enrollment.Content.ReadFromJsonAsync<JsonElement>();
        var discovery = await client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        var registrationPath = new Uri(discovery.GetProperty("registration_endpoint").GetString()!).PathAndQuery;
        var advertisedScopes = string.Join(" ", discovery.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()));
        var registered = await client.PostAsJsonAsync(registrationPath, new { redirect_uris = new[] { Callback }, token_endpoint_auth_method = "none" });
        registered.EnsureSuccessStatusCode();
        var registration = await registered.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = registration.GetProperty("client_id").GetString()!;
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["redirect_uri"] = Callback, ["response_type"] = "code",
            ["scope"] = advertisedScopes, ["resource"] = Resource, ["state"] = "fixture-state&<quoted>\"",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["deviceId"] = "must-not-be-copied", ["decision"] = "must-not-be-copied"
        });
        using var consentResponse = await admin.GetAsync(url);
        consentResponse.EnsureSuccessStatusCode();
        var policy = Assert.Single(consentResponse.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(Jarvis.McpServer.Security.BrowserContentSecurityPolicy.Default + " https://client.example", policy);
        var html = await consentResponse.Content.ReadAsStringAsync();
        // Optional browser evidence: only synthetic fixture HTML, with hidden values redacted.
        if (Environment.GetEnvironmentVariable("JARVIS_CONSENT_BROWSER_FIXTURE") is { Length: > 0 } fixturePath)
        {
            var safeHtml = Regex.Replace(html, "(<input type=\"hidden\" name=\"[^\"]+\" value=\")[^\"]*(\">)", "$1fixture-value$2");
            File.WriteAllText(fixturePath, JsonSerializer.Serialize(new
            {
                defaultPolicy = Jarvis.McpServer.Security.BrowserContentSecurityPolicy.Default,
                consentPolicy = policy, callbackOrigin = "https://client.example", html = safeHtml
            }));
        }
        var action = Regex.Match(html, "<form method=\"post\" action=\"([^\"]+)\"");
        Assert.True(action.Success);
        var form = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value), m => WebUtility.HtmlDecode(m.Groups[2].Value));
        Assert.False(form.ContainsKey("deviceId"));
        Assert.False(form.ContainsKey("decision"));
        Assert.True(form.ContainsKey("__RequestVerificationToken"));
        // Simulate the browser's actual form submission, not a hand-built OAuth request.
        // Do not let the API CSRF header mask a missing hidden token regression.
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        form["deviceId"] = device.GetProperty("deviceId").GetString()!;
        form["decision"] = "allow";
        return new(WebUtility.HtmlDecode(action.Groups[1].Value), form, clientId, verifier);
    }

    private static async Task<string> ApproveAsync(HttpClient admin, ConsentFlow flow)
    {
        var response = await admin.PostAsync(flow.Action, new FormUrlEncodedContent(flow.Form));
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        var callback = response.Headers.Location!;
        Assert.Equal("client.example", callback.Host);
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal(flow.Form["state"], query["state"].ToString());
        Assert.True(query.ContainsKey("code"), callback.ToString());
        return query["code"].ToString();
    }

    private static Dictionary<string, string> ExchangeForm(ConsentFlow flow, string code) => new()
    {
        ["grant_type"] = "authorization_code", ["client_id"] = flow.ClientId, ["redirect_uri"] = Callback,
        ["code"] = code, ["code_verifier"] = flow.Verifier, ["resource"] = Resource
    };

    private sealed record ConsentFlow(string Action, Dictionary<string, string> Form, string ClientId, string Verifier);
}
