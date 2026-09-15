using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Jarvis.McpServer.Infrastructure;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
namespace Jarvis.McpServer.Security;

public sealed class OAuthController(AppDbContext db, JarvisOptions options, IOpenIddictApplicationManager clients,
    IAntiforgery antiforgery) : Controller
{
    [HttpGet("/connect/authorize")]
    public async Task<IActionResult> Consent(CancellationToken ct)
    {
        var request = OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(HttpContext) ?? throw new InvalidOperationException("Missing OAuth request.");
        if (!ValidResource(request) || !request.GetScopes().Contains(CurrentAccess.Scope)) return BadRequest("Expected resource and mcp:tools scope.");
        if (User.Identity?.IsAuthenticated != true)
            return Redirect("/?returnUrl=" + Uri.EscapeDataString(Request.Path + Request.QueryString));
        var user = await CurrentAccess.RequireAsync(User, db, false, ct);
        var devices = await db.Devices.AsNoTracking().Where(d => d.OwnerId == user.Id && d.Enabled).ToListAsync(ct);
        var client = await clients.FindByClientIdAsync(request.ClientId!, ct);
        if (client is null) return BadRequest("Unknown OAuth client.");
        if (string.IsNullOrEmpty(request.RedirectUri) ||
            !await clients.ValidateRedirectUriAsync(client, request.RedirectUri, ct))
            return BadRequest("Invalid OAuth client callback.");
        // Set this on the originating consent document, before the browser submits.
        // A same-origin POST can then follow its verified cross-origin OAuth redirect.
        Response.Headers.ContentSecurityPolicy = BrowserContentSecurityPolicy.ForOAuthConsent(
            request.RedirectUri, options.OAuthRedirectUris);
        string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");
        var token = antiforgery.GetAndStoreTokens(HttpContext);
        var choices = string.Join("", devices.Select(d => $"<option value=\"{E(d.Id)}\">{E(d.Name)}</option>"));
        var name = await clients.GetDisplayNameAsync(client, ct) ?? request.ClientId!;
        // OpenIddict extracts POST parameters from the form, not the action query string.
        // Preserve repeated values and HTML-encode both names and values. Local controls
        // are generated below and must never be supplied by the authorization query.
        var parameters = string.Join("", Request.Query
            .Where(p => p.Key is not ("decision" or "deviceId" or "__RequestVerificationToken"))
            .SelectMany(p => p.Value.Select(value =>
                $"<input type=\"hidden\" name=\"{E(p.Key)}\" value=\"{E(value)}\">")));
        var html = $$"""
        <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Authorize · Jarvis Control</title><link rel="stylesheet" href="/styles.css">
        <main class="auth-shell"><section class="auth-card"><div class="brand-orb">J</div><p class="eyebrow">SECURE CONNECTION</p>
        <h1>Connect your workspace.</h1><p><strong>{{E(name)}}</strong> requests permission to call tools on one device.</p>
        <div class="notice">This can read files, execute commands and control your desktop. The agent still requires your local approval.
        Never authorize a client you do not recognize.</div>
        <form method="post" action="{{E(Request.Path)}}">
        {{parameters}}
        <input type="hidden" name="__RequestVerificationToken" value="{{E(token.RequestToken)}}">
        <label>Allow access to<select name="deviceId" required>{{choices}}</select></label>
        <div class="button-row"><button name="decision" value="allow" class="primary" {{(devices.Count == 0 ? "disabled" : "")}}>Authorize selected device</button>
        <button name="decision" value="deny" formnovalidate>Cancel</button></div></form>
        <p class="muted">Access tokens expire in 10 minutes. Disabling this device or account revokes access.</p></section></main></html>
        """;
        Response.Headers.CacheControl = "no-store";
        return Content(html, "text/html");
    }
    [HttpPost("/connect/authorize"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Accept([FromForm] string decision, [FromForm] string? deviceId, CancellationToken ct)
    {
        var request = OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(HttpContext) ?? throw new InvalidOperationException("Missing OAuth request.");
        if (decision != "allow") return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var user = await CurrentAccess.RequireAsync(User, db, false, ct);
        if (!ValidResource(request) || !request.GetScopes().Contains(CurrentAccess.Scope)) return BadRequest("Invalid resource/scope.");
        if (!await db.Devices.AnyAsync(d => d.Id == deviceId && d.OwnerId == user.Id && d.Enabled, ct)) return BadRequest("Select your own enabled device.");
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id);
        identity.SetClaim(CurrentAccess.DeviceClaim, deviceId);
        identity.SetClaim(CurrentAccess.StampClaim, user.SecurityStamp);
        identity.SetScopes(request.GetScopes().Where(s => s is CurrentAccess.Scope or Scopes.OfflineAccess));
        identity.SetResources(options.Resource);
        identity.SetDestinations(_ => [Destinations.AccessToken]);
        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
    [HttpPost("/connect/token"), IgnoreAntiforgeryToken]
    public async Task<IActionResult> Exchange(CancellationToken ct)
    {
        var request = OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(HttpContext) ?? throw new InvalidOperationException("Missing OAuth request.");
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType()) return BadRequest("Unsupported grant.");
        // Clients can omit resource on refresh. They may never replace the resource bound to the grant.
        if (request.GetResources().Length != 0 && !ValidResource(request)) return BadRequest("Invalid resource.");
        var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (result.Principal is null) return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var id = result.Principal.GetClaim(Claims.Subject);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        var deviceId = result.Principal.GetClaim(CurrentAccess.DeviceClaim);
        if (user?.Status != "active" || user.SecurityStamp != result.Principal.GetClaim(CurrentAccess.StampClaim) ||
            !await db.Devices.AnyAsync(d => d.Id == deviceId && d.OwnerId == id && d.Enabled, ct))
            return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        return SignIn(result.Principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
    private bool ValidResource(OpenIddictRequest request) => request.GetResources().Length == 1 && request.GetResources()[0] == options.Resource;

    public sealed record Registration(string[]? Redirect_uris, string? Client_name, string? Token_endpoint_auth_method,
        string[]? Grant_types, string[]? Response_types);
    [HttpPost("/connect/register"), IgnoreAntiforgeryToken, EnableRateLimiting("registration")]
    public async Task<IActionResult> Register([FromBody] Registration registration, CancellationToken ct)
    {
        var redirects = registration.Redirect_uris;
        if (redirects is null || redirects.Length is < 1 or > 4 || redirects.Any(r => !options.OAuthRedirectUris.Contains(r, StringComparer.Ordinal)))
            return BadRequest(new { error = "invalid_redirect_uri", error_description = "Administrator must allowlist the exact OAuth callback first." });
        if (registration.Token_endpoint_auth_method is not (null or "none") ||
            registration.Grant_types?.Any(g => g is not ("authorization_code" or "refresh_token")) == true ||
            registration.Response_types?.Any(r => r != "code") == true)
            return BadRequest(new { error = "invalid_client_metadata" });
        // An exact callback set identifies a public client. A caller cannot create unlimited records by changing display names.
        var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join("\n", redirects.Order(StringComparer.Ordinal))))).ToLowerInvariant();
        var clientId = "jarvis_" + fingerprint;
        var existing = await clients.FindByClientIdAsync(clientId, ct);
        if (existing is null)
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = clientId, ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Explicit,
                DisplayName = "MCP client — " + new Uri(redirects[0]).Host
            };
            foreach (var uri in redirects) descriptor.RedirectUris.Add(new Uri(uri));
            descriptor.Permissions.UnionWith([Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation, Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code, Permissions.Prefixes.Scope + CurrentAccess.Scope,
                Permissions.Prefixes.Resource + options.Resource]);
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
            await clients.CreateAsync(descriptor, ct);
        }
        Response.Headers.CacheControl = "no-store";
        return StatusCode(201, new { client_id = clientId, client_name = registration.Client_name ?? "MCP client", redirect_uris = redirects,
            token_endpoint_auth_method = "none", grant_types = new[] { "authorization_code", "refresh_token" }, response_types = new[] { "code" } });
    }
}





