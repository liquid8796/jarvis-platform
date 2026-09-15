using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace JarvisCode.Providers.ChatGptWeb;

/// <summary>
/// The plain-HTTP transport: the user's cookies as a <c>Cookie:</c> header straight to
/// chatgpt.com, with the headers the backend expects.
///
/// It is the fallback, not the main path. ChatGPT's Cloudflare protection fingerprints the
/// caller's TLS handshake, which no header can imitate, so this is refused on many machines even
/// with perfectly good cookies. When that happens the response says so in words the settings page
/// can show, instead of handing back a page of challenge HTML.
/// </summary>
public sealed class ChatGptHttpTransport(string cookieHeader, TimeSpan timeout) : IChatGptTransport
{
    /// <summary>
    /// Pinned to a current desktop Chrome. It has to stay consistent with the browser the cookies
    /// came from, and it is also hashed into the proof-of-work config.
    /// </summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public const string Origin = "https://chatgpt.com";

    public const string CloudflareMarker = "CLOUDFLARE_CHALLENGE";

    // No cookie container: the chunked NextAuth token must be sent back exactly as exported.
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseCookies = false,
        AllowAutoRedirect = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly TimeSpan _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(3) : timeout;

    public async Task<ChatGptResponse> SendAsync(
        HttpMethod method,
        string path,
        string? jsonBody,
        string? bearerToken,
        string accept,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        try
        {
            using var request = BuildRequest(method, path, jsonBody, bearerToken, accept, extraHeaders);
            using var response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (IsCloudflareChallenge(response, status))
            {
                return new ChatGptResponse(status,
                    $"{CloudflareMarker}: Cloudflare turned this request away. The plain HTTP path cannot pass its " +
                    "check, which is tied to a real browser's TLS fingerprint — use the browser engine, " +
                    "and export the cookies from the same machine and network you are running on.");
            }

            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            return new ChatGptResponse(status, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ChatGptResponse(ChatGptResponse.TransportFailure, "CANCELLED: the request was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return new ChatGptResponse(ChatGptResponse.TransportFailure, "TIMEOUT: ChatGPT did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            return new ChatGptResponse(ChatGptResponse.TransportFailure, $"TRANSPORT_ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// Nothing to drive: asking goes through ChatGPT's own page, which needs the embedded browser.
    /// </summary>
    public Task<ChatGptUiReply> AskAsync(ChatGptAsk ask, CancellationToken cancellationToken) =>
        Task.FromResult(ChatGptUiReply.Failed(
            "ChatGPT's browser session sends a message by driving the real page, which needs the "
            + "desktop app's embedded browser. This front-end has none, so the session can read the "
            + "account but not talk to it — use the desktop app for ChatGPT sessions."));

    private HttpRequestMessage BuildRequest(
        HttpMethod method,
        string path,
        string? jsonBody,
        string? bearerToken,
        string accept,
        IReadOnlyDictionary<string, string>? extraHeaders)
    {
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : Origin + path;
        var request = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", accept.Length > 0 ? accept : "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("Referer", Origin + "/");
        request.Headers.TryAddWithoutValidation("oai-language", "en-US");
        request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        if (cookieHeader.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        if (!string.IsNullOrEmpty(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    /// <summary>A "cf-mitigated: challenge" header, or an HTML body behind 403/503, is a block.</summary>
    private static bool IsCloudflareChallenge(HttpResponseMessage response, int status)
    {
        if (response.Headers.TryGetValues("cf-mitigated", out var mitigated)
            && mitigated.Any(v => string.Equals(v, "challenge", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return status is 403 or 503
            && response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
    }
}
