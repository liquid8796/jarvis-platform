using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Providers;

namespace JarvisCode.Providers.Vertex;

/// <summary>
/// Exchanges a Google service-account JSON for a cloud-platform access token:
/// a self-signed RS256 JWT posted to the OAuth token endpoint, cached per
/// client_email until shortly before expiry. Dependency-free — RSA.ImportFromPem
/// carries the key handling.
/// </summary>
internal static class GoogleServiceAccount
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    private static readonly TimeSpan ExpirySlack = TimeSpan.FromSeconds(60);

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    private static readonly Dictionary<string, CachedToken> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>True when the stored credential is a service-account JSON rather than a raw token.</summary>
    public static bool LooksLikeServiceAccountJson(string credential) =>
        credential.TrimStart().StartsWith('{');

    public static async Task<string> GetTokenAsync(
        string serviceAccountJson, HttpClient http, string providerName, CancellationToken cancellationToken)
    {
        JsonObject account;
        try
        {
            account = JsonNode.Parse(serviceAccountJson) as JsonObject
                ?? throw new ProviderException($"{providerName}: the stored credential is not a JSON object.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ProviderException($"{providerName}: the stored service-account JSON is unreadable: {ex.Message}");
        }

        var email = account["client_email"]?.GetValue<string>();
        var privateKeyPem = account["private_key"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(privateKeyPem))
            throw new ProviderException(
                $"{providerName}: the service-account JSON needs client_email and private_key " +
                "(download the key file from IAM → Service Accounts).");

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (Cache.TryGetValue(email, out var cached) && cached.ExpiresAt - ExpirySlack > DateTimeOffset.UtcNow)
                return cached.AccessToken;

            var assertion = BuildJwt(email, privateKeyPem, DateTimeOffset.UtcNow);
            using var response = await http.PostAsync(
                TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion,
                }),
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(
                    $"{providerName}: Google token exchange failed (HTTP {(int)response.StatusCode}) — " +
                    Http.ProviderHttp.ExtractErrorMessage(body));

            var parsed = JsonNode.Parse(body) as JsonObject;
            var token = parsed?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(token))
                throw new ProviderException($"{providerName}: Google returned no access token.");
            double lifetime = parsed!["expires_in"]?.GetValue<double>() is { } seconds and > 0 ? seconds : 3600;
            Cache[email] = new CachedToken(token, DateTimeOffset.UtcNow.AddSeconds(lifetime));
            return token;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The RS256 assertion: header.payload signed with the account's private key.</summary>
    internal static string BuildJwt(string clientEmail, string privateKeyPem, DateTimeOffset utcNow)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var claims = new JsonObject
        {
            ["iss"] = clientEmail,
            ["scope"] = Scope,
            ["aud"] = TokenEndpoint,
            ["iat"] = utcNow.ToUnixTimeSeconds(),
            ["exp"] = utcNow.AddMinutes(60).ToUnixTimeSeconds(),
        };
        var payload = Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signingInput = $"{header}.{payload}";

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(privateKeyPem);
        }
        catch (ArgumentException ex)
        {
            throw new ProviderException($"The service account's private_key is not a readable PEM key: {ex.Message}");
        }

        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
