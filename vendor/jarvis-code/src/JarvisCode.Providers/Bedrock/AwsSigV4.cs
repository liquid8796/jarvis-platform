using System.Security.Cryptography;
using System.Text;

namespace JarvisCode.Providers.Bedrock;

/// <summary>
/// AWS Signature Version 4 request signing — the hand-rolled minimum Bedrock
/// needs, kept dependency-free. The canonical-request → string-to-sign → HMAC
/// chain follows the SigV4 specification; <c>Compute</c> exposes each stage so
/// tests can pin the official example vector.
/// </summary>
internal static class AwsSigV4
{
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// Signs <paramref name="request"/> in place: sets x-amz-date (and the session
    /// token when given) and the Authorization header. <paramref name="canonicalPath"/>
    /// is the SigV4 canonical URI — for non-S3 services each path segment is
    /// URI-encoded twice, which is why it travels separately from the request URI.
    /// </summary>
    public static void Sign(
        HttpRequestMessage request,
        string canonicalPath,
        string accessKeyId,
        string secretKey,
        string? sessionToken,
        string region,
        string service,
        string payloadHash,
        DateTimeOffset utcNow)
    {
        var amzDate = utcNow.UtcDateTime.ToString("yyyyMMddTHHmmssZ");
        request.Headers.Remove("x-amz-date");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        if (!string.IsNullOrEmpty(sessionToken))
        {
            request.Headers.Remove("x-amz-security-token");
            request.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);
        }

        var headers = new List<(string Name, string Value)>
        {
            ("host", request.RequestUri!.Host),
            ("x-amz-date", amzDate),
        };
        if (request.Content?.Headers.ContentType is { } contentType)
            headers.Add(("content-type", contentType.ToString()));
        if (!string.IsNullOrEmpty(sessionToken))
            headers.Add(("x-amz-security-token", sessionToken));

        var (_, _, _, authorization) = Compute(
            request.Method.Method,
            canonicalPath,
            request.RequestUri.Query.TrimStart('?'),
            headers,
            payloadHash,
            accessKeyId,
            secretKey,
            region,
            service,
            utcNow);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    /// <summary>All four stages, so the official worked example can be asserted stage by stage.</summary>
    internal static (string CanonicalRequest, string StringToSign, string Signature, string Authorization) Compute(
        string method,
        string canonicalPath,
        string canonicalQuery,
        IReadOnlyList<(string Name, string Value)> headers,
        string payloadHash,
        string accessKeyId,
        string secretKey,
        string region,
        string service,
        DateTimeOffset utcNow)
    {
        var sorted = headers
            .Select(h => (Name: h.Name.ToLowerInvariant(), Value: h.Value.Trim()))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();
        var canonicalHeaders = string.Concat(sorted.Select(h => $"{h.Name}:{h.Value}\n"));
        var signedHeaders = string.Join(';', sorted.Select(h => h.Name));

        var canonicalRequest =
            $"{method}\n{canonicalPath}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        var amzDate = utcNow.UtcDateTime.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = utcNow.UtcDateTime.ToString("yyyyMMdd");
        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        var signingKey = HmacSha256(
            HmacSha256(
                HmacSha256(
                    HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp),
                    region),
                service),
            "aws4_request");
        var signature = Hex(HmacSha256(signingKey, stringToSign));

        var authorization =
            $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        return (canonicalRequest, stringToSign, signature, authorization);
    }

    /// <summary>SHA-256 of a request body, in the lowercase hex SigV4 wants.</summary>
    public static string HashPayload(byte[] payload) => Hex(SHA256.HashData(payload));

    /// <summary>
    /// The canonical URI for a non-S3 service: each path segment URI-encoded twice
    /// (the on-the-wire URL carries the single encoding).
    /// </summary>
    public static string CanonicalPath(IEnumerable<string> rawSegments) =>
        "/" + string.Join('/', rawSegments.Select(s => Uri.EscapeDataString(Uri.EscapeDataString(s))));

    private static byte[] HmacSha256(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
