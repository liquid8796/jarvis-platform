using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Settings;

namespace JarvisCode.Core.Mcp;

/// <summary>One stored OAuth grant for a remote MCP server.</summary>
public sealed record McpToken(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string? ClientId,
    string? TokenEndpoint,
    string? Resource);

/// <summary>
/// Per-server OAuth tokens for remote MCP servers, in one JSON file keyed by
/// server URL. Values are sealed through the host's <see cref="ISecretProtector"/>
/// when one is supplied (DPAPI on Windows).
/// </summary>
public sealed class McpTokenStore(string filePath, ISecretProtector? protector = null)
{
    private readonly Lock _gate = new();

    public string? GetAccessToken(McpServerConfig config) => Get(config)?.AccessToken;

    public McpToken? Get(McpServerConfig config)
    {
        lock (_gate)
        {
            var all = LoadAll();
            return all.TryGetValue(KeyFor(config), out var token) ? token : null;
        }
    }

    public void Save(McpServerConfig config, McpToken token)
    {
        lock (_gate)
        {
            var all = LoadAll();
            all[KeyFor(config)] = token;
            SaveAll(all);
        }
    }

    public void Remove(McpServerConfig config)
    {
        lock (_gate)
        {
            var all = LoadAll();
            if (all.Remove(KeyFor(config)))
                SaveAll(all);
        }
    }

    /// <summary>
    /// Refreshes an expired (or rejected) grant with its refresh token. True when
    /// a new access token was stored and the caller should retry the request.
    /// </summary>
    public async Task<bool> TryRefreshAsync(McpServerConfig config, HttpClient http, CancellationToken cancellationToken)
    {
        var token = Get(config);
        if (token?.RefreshToken is not { Length: > 0 } refresh ||
            token.TokenEndpoint is not { Length: > 0 } endpoint)
            return false;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
        };
        if (token.ClientId is { Length: > 0 })
            form["client_id"] = token.ClientId;
        if (token.Resource is { Length: > 0 })
            form["resource"] = token.Resource;

        try
        {
            using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form), cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            if (Tools.JsonArgs.GetString(body ?? [], "access_token") is not { Length: > 0 } access)
                return false;
            Save(config, token with
            {
                AccessToken = access,
                RefreshToken = Tools.JsonArgs.GetString(body!, "refresh_token") ?? refresh,
                ExpiresAt = DateTimeOffset.Now.AddSeconds(
                    body!["expires_in"]?.GetValue<double>() is { } seconds and > 0 ? seconds : 3600),
            });
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return false;
        }
    }

    private static string KeyFor(McpServerConfig config) => config.Url ?? config.Name;

    private Dictionary<string, McpToken> LoadAll()
    {
        try
        {
            if (!File.Exists(filePath))
                return [];
            var text = File.ReadAllText(filePath);
            if (protector is not null)
                text = protector.Unprotect(text) ?? "";
            if (text.Length == 0)
                return [];
            return JsonSerializer.Deserialize<Dictionary<string, McpToken>>(text) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            return [];
        }
    }

    private void SaveAll(Dictionary<string, McpToken> all)
    {
        try
        {
            var text = JsonSerializer.Serialize(all);
            if (protector is not null)
                text = protector.Protect(text);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a token means signing in again; never crash over it.
        }
    }
}

/// <summary>
/// Interactive OAuth for remote MCP servers, per the MCP authorization spec:
/// protected-resource discovery (RFC 9728) → authorization-server metadata
/// (RFC 8414) → dynamic client registration (RFC 7591) → authorization-code +
/// PKCE through the system browser with a loopback redirect, resource-indicated
/// (RFC 8707). The resulting grant lands in the <see cref="McpTokenStore"/>.
/// </summary>
public static class McpOAuth
{
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(3);

    public static async Task AuthorizeInteractivelyAsync(
        McpServerConfig config, HttpClient http, McpTokenStore tokens, CancellationToken cancellationToken)
    {
        if (config.Url is not { Length: > 0 } serverUrl)
            throw new McpException($"MCP server '{config.Name}' is not a remote server.");

        var metadata = await DiscoverAsync(serverUrl, http, cancellationToken);
        var redirect = $"http://127.0.0.1:{FreePort()}/callback/";
        var clientId = config.OAuthClientId is { Length: > 0 } configured
            ? configured
            : await RegisterClientAsync(metadata, redirect, http, cancellationToken);

        var verifierBytes = RandomNumberGenerator.GetBytes(48);
        var verifier = Base64Url(verifierBytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var authorizeUrl = metadata.AuthorizationEndpoint +
            (metadata.AuthorizationEndpoint.Contains('?') ? "&" : "?") +
            "response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&code_challenge={challenge}" +
            "&code_challenge_method=S256" +
            $"&state={state}" +
            $"&resource={Uri.EscapeDataString(serverUrl)}";

        string code;
        using (var listener = new HttpListener())
        {
            listener.Prefixes.Add(redirect);
            listener.Start();
            Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CallbackTimeout);
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new McpException(
                    $"Signing in to '{config.Name}' timed out — the browser never came back to the app.");
            }

            var query = context.Request.QueryString;
            var error = query["error"];
            code = query["code"] ?? "";
            var answered = error is null && code.Length > 0 && query["state"] == state;
            var page = answered
                ? "<html><body style=\"font-family:sans-serif\"><h3>Signed in.</h3>You can close this tab and return to Jarvis Code.</body></html>"
                : $"<html><body style=\"font-family:sans-serif\"><h3>Sign-in failed.</h3>{WebUtility.HtmlEncode(error ?? "missing code or state mismatch")}</body></html>";
            var bytes = Encoding.UTF8.GetBytes(page);
            context.Response.ContentType = "text/html";
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            context.Response.Close();
            if (!answered)
                throw new McpException($"Signing in to '{config.Name}' failed: {error ?? "invalid callback"}.");
        }

        await ExchangeCodeAsync(config, http, tokens, metadata, clientId, redirect, verifier, code, cancellationToken);
    }

    /// <summary>The authorization-code exchange and the store write, shared with the stateful flow.</summary>
    internal static async Task ExchangeCodeAsync(
        McpServerConfig config, HttpClient http, McpTokenStore tokens, AuthServerMetadata metadata,
        string clientId, string redirect, string verifier, string code, CancellationToken cancellationToken)
    {
        var serverUrl = config.Url!;
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
            ["resource"] = serverUrl,
        };
        // A confidential client the user configured authenticates the exchange;
        // a dynamically registered public one has no secret to send.
        if (config.OAuthClientSecret is { Length: > 0 } secret)
            form["client_secret"] = secret;
        using var response = await http.PostAsync(metadata.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        var bodyText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new McpException(
                $"Signing in to '{config.Name}' failed at the token endpoint: HTTP {(int)response.StatusCode} — " +
                (bodyText.Length > 300 ? bodyText[..300] : bodyText));
        var body = JsonNode.Parse(bodyText) as JsonObject
            ?? throw new McpException($"Signing in to '{config.Name}' failed: unreadable token response.");
        var access = Tools.JsonArgs.GetString(body, "access_token")
            ?? throw new McpException($"Signing in to '{config.Name}' failed: no access token returned.");

        tokens.Save(config, new McpToken(
            access,
            Tools.JsonArgs.GetString(body, "refresh_token"),
            DateTimeOffset.Now.AddSeconds(body["expires_in"]?.GetValue<double>() is { } seconds and > 0 ? seconds : 3600),
            clientId,
            metadata.TokenEndpoint,
            serverUrl));
    }

    internal sealed record AuthServerMetadata(
        string AuthorizationEndpoint, string TokenEndpoint, string? RegistrationEndpoint);

    /// <summary>RFC 9728 protected-resource metadata first, RFC 8414 at the server origin as fallback.</summary>
    internal static async Task<AuthServerMetadata> DiscoverAsync(
        string serverUrl, HttpClient http, CancellationToken cancellationToken)
    {
        var origin = new Uri(serverUrl).GetLeftPart(UriPartial.Authority);
        string? authServer = null;
        if (await TryGetJsonAsync(http, $"{origin}/.well-known/oauth-protected-resource", cancellationToken) is { } resource &&
            resource["authorization_servers"] is JsonArray servers && servers.Count > 0)
        {
            authServer = servers[0]?.GetValue<string>();
        }

        authServer ??= origin;
        var authOrigin = new Uri(authServer).GetLeftPart(UriPartial.Authority);
        var metadata =
            await TryGetJsonAsync(http, $"{authOrigin}/.well-known/oauth-authorization-server", cancellationToken) ??
            await TryGetJsonAsync(http, $"{authOrigin}/.well-known/openid-configuration", cancellationToken) ??
            throw new McpException(
                $"The server at {serverUrl} does not publish OAuth metadata — it may want a token in `headers` instead.");

        var authorize = Tools.JsonArgs.GetString(metadata, "authorization_endpoint");
        var token = Tools.JsonArgs.GetString(metadata, "token_endpoint");
        if (authorize is null || token is null)
            throw new McpException($"The OAuth metadata for {serverUrl} is missing its endpoints.");
        return new AuthServerMetadata(authorize, token, Tools.JsonArgs.GetString(metadata, "registration_endpoint"));
    }

    /// <summary>RFC 7591 dynamic registration; servers without it get the fixed public client id.</summary>
    internal static async Task<string> RegisterClientAsync(
        AuthServerMetadata metadata, string redirect, HttpClient http, CancellationToken cancellationToken)
    {
        if (metadata.RegistrationEndpoint is not { Length: > 0 } endpoint)
            return "jarvis-code";

        var registration = new JsonObject
        {
            ["client_name"] = "Jarvis Code",
            ["redirect_uris"] = new JsonArray(redirect),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        };
        try
        {
            using var response = await http.PostAsync(
                endpoint,
                new StringContent(registration.ToJsonString(), Encoding.UTF8, "application/json"),
                cancellationToken);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            return Tools.JsonArgs.GetString(body ?? [], "client_id") ?? "jarvis-code";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return "jarvis-code";
        }
    }

    internal static async Task<JsonObject?> TryGetJsonAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    internal static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
