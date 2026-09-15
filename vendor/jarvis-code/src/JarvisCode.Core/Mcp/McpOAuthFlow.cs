using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace JarvisCode.Core.Mcp;

/// <summary>
/// The sign-in to a remote MCP server as a UI can drive it, one step at a time:
/// discovery and client registration happen up front, the authorization URL is
/// handed back for the host to open, the loopback listener waits for the
/// browser to come back, and — because a browser does not always come back —
/// the callback URL the user copied out of it can be submitted by hand. The
/// one-shot <see cref="McpOAuth.AuthorizeInteractivelyAsync"/> is the same flow
/// with the host's part done here, kept for the CLI and the /mcp-auth command.
/// </summary>
public sealed class McpOAuthFlow : IAsyncDisposable
{
    private readonly McpServerConfig _config;
    private readonly HttpClient _http;
    private readonly McpTokenStore _tokens;
    private readonly McpOAuth.AuthServerMetadata _metadata;
    private readonly string _clientId;
    private readonly string _verifier;
    private readonly string _state;
    private readonly HttpListener _listener;
    private bool _completed;

    private McpOAuthFlow(
        McpServerConfig config, HttpClient http, McpTokenStore tokens, McpOAuth.AuthServerMetadata metadata,
        string clientId, string redirect, string verifier, string state, string authorizeUrl, HttpListener listener)
    {
        _config = config;
        _http = http;
        _tokens = tokens;
        _metadata = metadata;
        _clientId = clientId;
        RedirectUri = redirect;
        _verifier = verifier;
        _state = state;
        AuthorizeUrl = authorizeUrl;
        _listener = listener;
    }

    /// <summary>The URL the host opens in the user's browser.</summary>
    public string AuthorizeUrl { get; }

    /// <summary>The loopback address the authorization server sends the browser back to.</summary>
    public string RedirectUri { get; }

    /// <summary>The server the flow signs in to.</summary>
    public McpServerConfig Config => _config;

    /// <summary>
    /// Discovers the authorization server, registers a client and opens the
    /// loopback listener. Nothing is shown to the user yet: the host decides
    /// whether and how to open <see cref="AuthorizeUrl"/>.
    /// </summary>
    public static async Task<McpOAuthFlow> StartAsync(
        McpServerConfig config, HttpClient http, McpTokenStore tokens, CancellationToken cancellationToken)
    {
        if (config.Url is not { Length: > 0 } serverUrl)
            throw new McpException($"MCP server '{config.Name}' is not a remote server.");

        var metadata = await McpOAuth.DiscoverAsync(serverUrl, http, cancellationToken);
        var redirect = $"http://127.0.0.1:{McpOAuth.FreePort()}/callback/";
        // A client id the user configured under Advanced replaces the dynamic
        // registration; a server that issued one usually refuses to register another.
        var clientId = config.OAuthClientId is { Length: > 0 } configured
            ? configured
            : await McpOAuth.RegisterClientAsync(metadata, redirect, http, cancellationToken);

        var verifier = McpOAuth.Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = McpOAuth.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = McpOAuth.Base64Url(RandomNumberGenerator.GetBytes(16));
        var authorizeUrl = metadata.AuthorizationEndpoint +
            (metadata.AuthorizationEndpoint.Contains('?') ? "&" : "?") +
            "response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&code_challenge={challenge}" +
            "&code_challenge_method=S256" +
            $"&state={state}" +
            $"&resource={Uri.EscapeDataString(serverUrl)}";

        var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        return new McpOAuthFlow(config, http, tokens, metadata, clientId, redirect, verifier, state, authorizeUrl, listener);
    }

    /// <summary>
    /// Waits for the browser to land on the loopback redirect and returns the
    /// callback URL it arrived with, or null when <paramref name="timeout"/>
    /// passed first — the moment the host offers the paste box instead.
    /// </summary>
    public async Task<string?> WaitForCallbackAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        HttpListenerContext context;
        try
        {
            context = await _listener.GetContextAsync().WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            return null;
        }

        var url = context.Request.Url?.ToString() ?? "";
        var query = context.Request.QueryString;
        var answered = query["error"] is null && (query["code"] ?? "").Length > 0 && query["state"] == _state;
        var page = answered
            ? "<html><body style=\"font-family:sans-serif\"><h3>Signed in.</h3>You can close this tab and return to Jarvis Code.</body></html>"
            : $"<html><body style=\"font-family:sans-serif\"><h3>Sign-in failed.</h3>{WebUtility.HtmlEncode(query["error"] ?? "missing code or state mismatch")}</body></html>";
        var bytes = Encoding.UTF8.GetBytes(page);
        try
        {
            context.Response.ContentType = "text/html";
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The browser already went away; the URL is what matters.
        }
        return url;
    }

    /// <summary>What a submitted callback URL turned out to be.</summary>
    public enum CallbackVerdict
    {
        /// <summary>A URL carrying an authorization code the server can be asked to exchange.</summary>
        Code,

        /// <summary>Not a URL at all.</summary>
        NotAUrl,

        /// <summary>A URL, but with neither a code nor an error in its query.</summary>
        MissingCode,

        /// <summary>The authorization server sent the browser back with an error.</summary>
        Error,

        /// <summary>The state does not match this flow: another sign-in's callback, or a forgery.</summary>
        StateMismatch,
    }

    /// <summary>
    /// Reads a callback URL — from the listener or from the user's clipboard —
    /// without acting on it. The reference checks the same two things before it
    /// submits a pasted address: that it parses, and that it carries a
    /// <c>code</c> or an <c>error</c>.
    /// </summary>
    public CallbackVerdict Inspect(string callbackUrl, out string? code, out string? error)
    {
        code = null;
        error = null;
        if (!Uri.TryCreate(callbackUrl.Trim(), UriKind.Absolute, out var uri))
            return CallbackVerdict.NotAUrl;
        var query = HttpUtility.ParseQueryString(uri.Query);
        code = query["code"];
        error = query["error"];
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error))
            return CallbackVerdict.MissingCode;
        if (!string.IsNullOrEmpty(error))
            return CallbackVerdict.Error;
        if (query["state"] != _state)
            return CallbackVerdict.StateMismatch;
        return CallbackVerdict.Code;
    }

    /// <summary>
    /// Exchanges the code a callback URL carries for a token and stores it. The
    /// URL may have come from the listener or been pasted; either way it is
    /// checked with <see cref="Inspect"/> first and refused with the reason.
    /// </summary>
    public async Task CompleteAsync(string callbackUrl, CancellationToken cancellationToken)
    {
        var verdict = Inspect(callbackUrl, out var code, out var error);
        switch (verdict)
        {
            case CallbackVerdict.NotAUrl:
                throw new McpException("That doesn’t look like a URL.");
            case CallbackVerdict.MissingCode:
                throw new McpException(
                    "That URL is missing an authorization code. Copy the full address the browser was redirected to.");
            case CallbackVerdict.Error:
                throw new McpException($"Signing in to '{_config.Name}' failed: {error}.");
            case CallbackVerdict.StateMismatch:
                throw new McpException(
                    $"Signing in to '{_config.Name}' failed: the callback belongs to a different sign-in.");
        }

        await McpOAuth.ExchangeCodeAsync(
            _config, _http, _tokens, _metadata, _clientId, RedirectUri, _verifier, code!, cancellationToken);
        _completed = true;
    }

    /// <summary>True once a token was stored.</summary>
    public bool Completed => _completed;

    public ValueTask DisposeAsync()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        return ValueTask.CompletedTask;
    }
}
