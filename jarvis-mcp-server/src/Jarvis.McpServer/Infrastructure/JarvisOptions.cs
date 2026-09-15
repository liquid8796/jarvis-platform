namespace Jarvis.McpServer.Infrastructure;
public sealed class JarvisOptions
{
    public string PublicOrigin { get; set; } = "http://localhost:18765";
    public string DataDirectory { get; set; } = "data";
    public bool AllowRegistration { get; set; } = true;
    public bool AutoApproveRegistration { get; set; }
    public int ToolTimeoutSeconds { get; set; } = 120;
    public string[] OAuthRedirectUris { get; set; } = [];
    public string Resource => PublicOrigin.TrimEnd('/') + "/mcp";
    public Uri Issuer => new(PublicOrigin.TrimEnd('/') + "/");
    public void Validate(bool development)
    {
        var origin = new Uri(PublicOrigin, UriKind.Absolute);
        if (origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/")
            throw new InvalidOperationException("Jarvis:PublicOrigin must be an origin without path, query or credentials.");
        if (origin.Scheme != "https" && !(development && origin.Scheme == "http" && origin.IsLoopback))
            throw new InvalidOperationException("Production requires an HTTPS public origin.");
        if (ToolTimeoutSeconds is < 5 or > 240) throw new InvalidOperationException("Tool timeout must be 5..240 seconds.");
        if (OAuthRedirectUris.Length > 16 || OAuthRedirectUris.Distinct(StringComparer.Ordinal).Count() != OAuthRedirectUris.Length)
            throw new InvalidOperationException("Configure no more than 16 unique exact callback URIs.");
        PublicOrigin = PublicOrigin.TrimEnd('/');
        foreach (var redirect in OAuthRedirectUris)
        {
            var uri = new Uri(redirect, UriKind.Absolute);
            if (uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
                (uri.Scheme != "https" && !(development && uri.IsLoopback && uri.Scheme == "http")))
                throw new InvalidOperationException("OAuth callbacks must be exact HTTPS URIs (or development loopback).");
        }
    }
}
