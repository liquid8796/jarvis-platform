using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Utilities;

namespace JarvisCode.Providers.Http;

/// <summary>Shared HTTP plumbing for the streaming provider implementations.</summary>
internal static class ProviderHttp
{
    private const int MaxRotationAttempts = 10;
    private const int MaxRetries = 4;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>Base for exponential backoff; tests shrink it so retries stay fast.</summary>
    internal static TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Resolves a key, sends the request built by <paramref name="requestFactory"/>
    /// and returns the response stream once headers arrive. A 429/529 response first
    /// rotates to the next configured key (no delay — a fresh key is not rate
    /// limited); transient failures (408/409/429/5xx, network errors, timeouts) then
    /// retry with exponential backoff plus jitter, honoring Retry-After. Anything
    /// else surfaces as <see cref="ProviderException"/> with the vendor's message.
    /// </summary>
    public static async Task<Stream> SendForStreamAsync(
        HttpClient http,
        IApiKeySource keys,
        string providerId,
        string providerName,
        bool requiresApiKey,
        Func<string?, HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        var attemptedKeys = new HashSet<string>(StringComparer.Ordinal);
        int rotationAttempt = 0;
        int retry = 0;
        while (true)
        {
            var key = keys.GetKey(providerId);
            if (key is null && requiresApiKey)
                throw new ProviderException($"No API key configured for {providerName}. Add one in Settings.");

            using var request = requestFactory(key);
            DiagnosticLog.Write($"http {providerId}: POST {Describe(request.RequestUri)} attempt={retry + 1}");
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                DiagnosticLog.Write($"http {providerId}: network error {ex.GetType().Name} after {retry} retries");
                if (retry >= MaxRetries)
                    throw new ProviderException($"{providerName}: network error — {ex.Message}", ex);
                await DelayForRetry(ProviderRetryCause.RequestFailed, ++retry, retryAfter: null, cancellationToken);
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                DiagnosticLog.Write($"http {providerId}: request timed out after {retry} retries");
                if (retry >= MaxRetries)
                    throw new ProviderException($"{providerName}: the request timed out.");
                await DelayForRetry(ProviderRetryCause.RequestFailed, ++retry, retryAfter: null, cancellationToken);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                DiagnosticLog.Write($"http {providerId}: {(int)response.StatusCode} streaming");
                return await response.Content.ReadAsStreamAsync(cancellationToken);
            }

            string body = await response.Content.ReadAsStringAsync(CancellationToken.None);
            int statusCode = (int)response.StatusCode;
            var retryAfter = ParseRetryAfter(response);
            response.Dispose();

            DiagnosticLog.Write($"http {providerId}: HTTP {statusCode}");

            bool rateLimited = statusCode is 429 or 529;
            if (rateLimited && key is not null && rotationAttempt < MaxRotationAttempts)
            {
                rotationAttempt++;
                attemptedKeys.Add(key);
                if (keys.TryRotate(providerId, key) &&
                    keys.GetKey(providerId) is { } nextKey && !attemptedKeys.Contains(nextKey))
                {
                    DiagnosticLog.Write($"http {providerId}: rotated to another key (rotation {rotationAttempt})");
                    continue;
                }
            }

            bool retryable = statusCode is 408 or 409 or 429 or 500 or 502 or 503 or 504 or 529;
            if (retryable && retry < MaxRetries)
            {
                var delay = await DelayForRetry(CauseFor(statusCode), ++retry, retryAfter, cancellationToken);
                DiagnosticLog.Write($"http {providerId}: retry {retry} in {delay.TotalSeconds:0.0}s");
                continue;
            }

            DiagnosticLog.Write($"http {providerId}: giving up on HTTP {statusCode} after {retry} retries");
            throw new ProviderException($"{providerName}: HTTP {statusCode} — {ExtractErrorMessage(body)}");
        }
    }

    /// <summary>
    /// Host and path only. Query strings are never logged: some vendors accept the
    /// API key there, and the path alone is enough to identify the endpoint.
    /// </summary>
    private static string Describe(Uri? uri) => uri is null ? "(no uri)" : $"{uri.Host}{uri.AbsolutePath}";

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
            return null;
        if (header.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;
        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }
        return null;
    }

    /// <summary>
    /// Waits out one retry and, additively, tells whatever is observing this async
    /// flow that it is happening: the desktop renders that as the reference's
    /// "{cause}. Retrying in {seconds}s (attempt {n} of {max})" line instead of
    /// leaving a silent gap where the answer should be.
    /// </summary>
    private static async Task<TimeSpan> DelayForRetry(
        ProviderRetryCause cause, int retry, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var delay = BackoffDelay(retry, retryAfter);
        ProviderRetries.Report(new ProviderRetryNotice(
            cause, retry, MaxRetries, delay, DateTimeOffset.Now));
        await Task.Delay(delay, cancellationToken);
        return delay;
    }

    /// <summary>The reference's own four causes, mapped onto the statuses this transport retries.</summary>
    private static ProviderRetryCause CauseFor(int statusCode) => statusCode switch
    {
        429 => ProviderRetryCause.RateLimit,
        500 or 502 or 503 or 504 or 529 => ProviderRetryCause.Overloaded,
        401 or 403 => ProviderRetryCause.AuthenticationFailed,
        _ => ProviderRetryCause.RequestFailed,
    };

    /// <summary>Exponential backoff with additive jitter in [0, base], capped; Retry-After wins when longer.</summary>
    private static TimeSpan BackoffDelay(int retry, TimeSpan? retryAfter)
    {
        var baseDelay = TimeSpan.FromTicks(RetryBaseDelay.Ticks << Math.Min(retry - 1, 6));
        if (baseDelay > MaxRetryDelay)
            baseDelay = MaxRetryDelay;
        var jitter = TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * baseDelay.Ticks));
        var delay = baseDelay + jitter;
        if (retryAfter is { } serverWait)
        {
            if (serverWait > MaxRetryAfter)
                serverWait = MaxRetryAfter;
            if (serverWait > delay)
                delay = serverWait;
        }
        return delay;
    }

    /// <summary>
    /// Header rows for a request inspector preview, for the adapters that
    /// authenticate with "Authorization: Bearer". The bearer row appears only when a
    /// key is actually configured, matching what those adapters send; the key is
    /// tested for presence and never read into the preview.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> BearerPreviewHeaders(
        IApiKeySource keys, string providerId)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("content-type", "application/json"),
        };
        if (keys.GetKey(providerId) is not null)
        {
            headers.Add(new("Authorization", $"Bearer {RequestBodyOverride.RedactedValue}"));
        }

        return headers;
    }

    public static HttpRequestMessage CreateJsonPost(string url, JsonObject body)
    {
        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// Renders a vendor error node: most nest {"error":{"message":...}} while
    /// Ollama returns {"error":"..."}; anything else falls back to raw JSON.
    /// </summary>
    public static string DescribeError(JsonNode error) => error switch
    {
        JsonObject obj when obj["message"] is JsonValue m && m.TryGetValue<string>(out var nested) => nested,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => error.ToJsonString(),
    };

    public static string ExtractErrorMessage(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is { } node && node["error"] is { } error)
            {
                var message = DescribeError(error);
                if (!string.IsNullOrWhiteSpace(message))
                    return message;
            }
        }
        catch (Exception)
        {
            // Not JSON — fall through to the raw body.
        }
        var trimmed = body.Trim();
        return trimmed.Length == 0 ? "no response body" : trimmed.Length > 500 ? trimmed[..500] : trimmed;
    }

}
