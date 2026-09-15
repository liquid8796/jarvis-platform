using Jarvis.McpServer.Security;
namespace Jarvis.Server.Tests;

public sealed class OAuthConsentPolicyTests
{
    private const string Callback = "https://chatgpt.com/connector_platform_oauth_redirect";

    [Fact]
    public void Consent_allows_only_selected_callback_origin_and_retains_other_directives()
    {
        var policy = BrowserContentSecurityPolicy.ForOAuthConsent(Callback,
            [Callback, "https://other-client.example/callback"]);
        Assert.Equal(BrowserContentSecurityPolicy.Default + " https://chatgpt.com", policy);
        Assert.DoesNotContain("other-client.example", policy);
        Assert.DoesNotContain("*", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
    }

    [Theory]
    [InlineData("https://chatgpt.com/other-callback")]
    [InlineData("https://chatgpt.com.evil.example/connector_platform_oauth_redirect")]
    [InlineData("https://other-client.example/callback")]
    [InlineData(null)]
    [InlineData("")]
    public void Unapproved_callback_never_expands_policy(string? callback) =>
        Assert.Throws<ArgumentException>(() => BrowserContentSecurityPolicy.ForOAuthConsent(callback, [Callback]));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://*.example/callback")]
    [InlineData("https://user:password@example.test/callback")]
    [InlineData("https://client.example/callback#fragment")]
    [InlineData("http://nonloopback.example/callback")]
    public void Malformed_callback_cannot_inject_a_policy_even_if_misconfigured(string callback) =>
        Assert.Throws<ArgumentException>(() => BrowserContentSecurityPolicy.ForOAuthConsent(callback, [callback]));

    [Fact]
    public void Callback_query_is_not_interpolated_into_CSP()
    {
        const string callback = "https://client.example/callback?hint=semicolon%3Bscript-src%20*";
        Assert.Equal(BrowserContentSecurityPolicy.Default + " https://client.example",
            BrowserContentSecurityPolicy.ForOAuthConsent(callback, [callback]));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/api/auth/csrf")]
    public async Task Nonconsent_documents_keep_strict_same_origin_forms(string path)
    {
        using var app = new ServerFixture();
        using var client = app.Client();
        using var response = await client.GetAsync(path);
        Assert.Equal(BrowserContentSecurityPolicy.Default,
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }
}
