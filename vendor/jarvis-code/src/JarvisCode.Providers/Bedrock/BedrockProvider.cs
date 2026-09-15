using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Anthropic;
using JarvisCode.Providers.Http;

namespace JarvisCode.Providers.Bedrock;

/// <summary>The Bedrock knobs beyond the secret key (which lives in the normal key slot).</summary>
public interface IBedrockOptions
{
    /// <summary>AWS region of the Bedrock runtime, e.g. us-east-1.</summary>
    string BedrockRegion { get; }

    /// <summary>The access key ID paired with the stored secret access key.</summary>
    string BedrockAccessKeyId { get; }
}

/// <summary>
/// Claude over AWS Bedrock: the Anthropic Messages body (anthropic_version
/// instead of model/stream) POSTed to invoke-with-response-stream under a SigV4
/// signature, with the response's eventstream chunks unwrapped back into the
/// Messages event grammar and fed through the shared Anthropic parser.
/// The API-key slot stores the secret access key; region and access key ID come
/// from <see cref="IBedrockOptions"/>. Model ids are per account/region
/// (us.anthropic.…-v1:0 inference profiles), so none ship built-in — they are
/// added on the provider card. Anthropic's server-side web search does not
/// exist on Bedrock and is stripped from the request.
/// </summary>
public sealed class BedrockProvider(
    HttpClient http,
    IApiKeySource keys,
    IBedrockOptions options) : ILlmProvider, IRequestInspector
{
    public const string ProviderId = "bedrock";

    internal const string AnthropicVersion = "bedrock-2023-05-31";

    public string Id => ProviderId;

    public string DisplayName => "AWS Bedrock (Claude)";

    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var region = options.BedrockRegion.Trim();
        var accessKeyId = options.BedrockAccessKeyId.Trim();
        if (region.Length == 0 || accessKeyId.Length == 0)
            throw new ProviderException(
                $"{DisplayName}: set the region and access key ID on the provider card in Settings " +
                "(the secret access key goes in the key slot).");

        var payload = Encoding.UTF8.GetBytes(BuildBody(request).ToJsonString());
        var payloadHash = AwsSigV4.HashPayload(payload);
        var url = RequestUrl(region, request.ModelId);
        var canonicalPath = AwsSigV4.CanonicalPath(["model", request.ModelId, "invoke-with-response-stream"]);

        await using var stream = await ProviderHttp.SendForStreamAsync(
            http, keys, Id, DisplayName, requiresApiKey: true,
            secretKey =>
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new ByteArrayContent(payload),
                };
                httpRequest.Content.Headers.ContentType = new("application/json");
                httpRequest.Headers.TryAddWithoutValidation("accept", "application/vnd.amazon.eventstream");
                AwsSigV4.Sign(
                    httpRequest, canonicalPath, accessKeyId, secretKey!, sessionToken: null,
                    region, service: "bedrock", payloadHash, DateTimeOffset.UtcNow);
                return httpRequest;
            },
            cancellationToken);

        await foreach (var providerEvent in AnthropicStream.ParseEventsAsync(
            UnwrapChunks(stream, cancellationToken), DisplayName, cancellationToken, Id))
        {
            yield return providerEvent;
        }
    }

    /// <summary>Eventstream frames → the Anthropic Messages events their chunks carry.</summary>
    private async IAsyncEnumerable<(string? EventName, JsonNode Node)> UnwrapChunks(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in EventStreamReader.ReadMessagesAsync(stream, DisplayName, cancellationToken))
        {
            if (message.MessageType is "exception" or "error")
            {
                var detail = Encoding.UTF8.GetString(message.Payload);
                throw new ProviderException(
                    $"{DisplayName}: {message.Headers.GetValueOrDefault(":exception-type") ?? "stream error"} — " +
                    ProviderHttp.ExtractErrorMessage(detail));
            }

            if (message.EventType != "chunk")
                continue;

            var envelope = AnthropicStream.TryParse(Encoding.UTF8.GetString(message.Payload));
            if (envelope?["bytes"].AsText() is not { Length: > 0 } encoded)
                continue;
            JsonNode? node;
            try
            {
                node = AnthropicStream.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            }
            catch (FormatException)
            {
                continue;
            }

            if (node is not null)
                yield return (node["type"].AsText(), node);
        }
    }

    internal static string RequestUrl(string region, string modelId) =>
        $"https://bedrock-runtime.{region}.amazonaws.com/model/{Uri.EscapeDataString(modelId)}/invoke-with-response-stream";

    internal JsonObject BuildBody(LlmRequest request)
    {
        // Bedrock has no server-side web_search tool; never let it into the body.
        var body = AnthropicProvider.BuildRequestBody(request with { EnableWebSearch = false }, Id, effortExtras: false);
        body.Remove("model");
        body.Remove("stream");
        body["anthropic_version"] = AnthropicVersion;
        if (request.ThinkingEffort != ThinkingEffort.Off)
        {
            // Bedrock takes beta flags in the body; there is no anthropic-beta header.
            body["anthropic_beta"] = new JsonArray(AnthropicProvider.InterleavedThinkingBeta);
        }

        return RequestBodyOverride.Apply(body, request.BodyOverride);
    }

    /// <inheritdoc />
    public ProviderRequestPreview PreviewRequest(LlmRequest request)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("content-type", "application/json"),
            new("accept", "application/vnd.amazon.eventstream"),
            new("Authorization", $"AWS4-HMAC-SHA256 Credential={RequestBodyOverride.RedactedValue}, …"),
        };
        return new ProviderRequestPreview(
            "POST",
            RequestUrl(options.BedrockRegion.Trim(), request.ModelId),
            headers,
            BuildBody(request));
    }
}
