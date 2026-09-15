using Jarvis.McpServer.Infrastructure;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Jarvis.McpServer.Security;

/// <summary>Advertises the actual MCP public-client registration flow to OAuth clients.</summary>
public sealed class OAuthDiscoveryHandler(JarvisOptions options)
    : IOpenIddictServerHandler<HandleConfigurationRequestContext>
{
    public ValueTask HandleAsync(HandleConfigurationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // DCR is implemented by our MVC endpoint, not by OpenIddict itself.
        // Derive it from the configured issuer, never from an untrusted Host header.
        context.AuthorizationEndpoint = new Uri(options.Issuer, "connect/authorize");
        context.TokenEndpoint = new Uri(options.Issuer, "connect/token");
        context.RevocationEndpoint = new Uri(options.Issuer, "connect/revoke");
        context.JsonWebKeySetEndpoint = new Uri(options.Issuer, ".well-known/jwks");
        context.Metadata["registration_endpoint"] = new Uri(options.Issuer, "connect/register").AbsoluteUri;
        context.TokenEndpointAuthenticationMethods.Add(OpenIddictConstants.ClientAuthenticationMethods.None);
        // Consent issues MCP access/refresh grants, not an OIDC identity token.
        // Do not invite clients to request an OIDC scope that this flow drops.
        context.Scopes.Remove(OpenIddictConstants.Scopes.OpenId);
        return ValueTask.CompletedTask;
    }
}
