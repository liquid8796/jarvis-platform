namespace Jarvis.McpServer.Security;

/// <summary>Keep normal forms same-origin; allow only the selected OAuth client's
/// validated callback origin on its consent document. Chromium applies form-action
/// to redirects after a form POST as well as to the initial action URL.</summary>
public static class BrowserContentSecurityPolicy
{
    public const string Default = "default-src 'self'; script-src 'self'; style-src 'self'; " +
        "img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    public static string ForOAuthConsent(string? redirectUri, IEnumerable<string> allowedRedirectUris)
    {
        if (string.IsNullOrEmpty(redirectUri) ||
            !allowedRedirectUris.Contains(redirectUri, StringComparer.Ordinal) ||
            !Uri.TryCreate(redirectUri, UriKind.Absolute, out var callback) ||
            callback.UserInfo.Length != 0 || callback.Fragment.Length != 0 ||
            callback.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic ||
            callback.IdnHost.Contains('*') ||
            (callback.Scheme != Uri.UriSchemeHttps &&
             !(callback.Scheme == Uri.UriSchemeHttp && callback.IsLoopback)))
            throw new ArgumentException("The OAuth callback is not approved for this consent page.");

        // Do not interpolate raw query parameters or allow every configured client.
        // OpenIddict separately enforces the exact redirect URI for this client;
        // CSP is a browser navigation restriction, not the OAuth allowlist.
        return Default + " " + callback.GetLeftPart(UriPartial.Authority);
    }
}
