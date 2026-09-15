using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Anthropic;
using JarvisCode.Providers.Http;

namespace JarvisCode.Providers.Vertex;

/// <summary>The Vertex knobs beyond the credential (which lives in the normal key slot).</summary>
public interface IVertexOptions
{
    /// <summary>The Google Cloud project the models are enabled in.</summary>
    string VertexProjectId { get; }

    /// <summary>Vertex location, e.g. us-east5 — or "global" for the global endpoint.</summary>
    string VertexRegion { get; }
}

/// <summary>
/// Claude over Google Vertex AI: the Anthropic Messages body (anthropic_version
/// instead of model) POSTed to the publisher model's streamRawPredict, which
/// answers in the same SSE grammar as Anthropic itself — so the shared parser
/// does the rest. The key slot stores either a service-account JSON (exchanged
/// for a bearer token, cached) or a ready access token; project and region come
/// from <see cref="IVertexOptions"/>. Model ids are the Vertex form
/// (claude-…@YYYYMMDD) and per project, so none ship built-in. Anthropic's
/// server-side web search does not exist on Vertex and is stripped.
/// </summary>
public sealed class VertexProvider(
    HttpClient http,
    IApiKeySource keys,
    IVertexOptions options) : ILlmProvider, IRequestInspector
{
    public const string ProviderId = "vertex";

    internal const string AnthropicVersion = "vertex-2023-10-16";

    public string Id => ProviderId;

    public string DisplayName => "Google Vertex AI (Claude)";

    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var project = options.VertexProjectId.Trim();
        var region = options.VertexRegion.Trim();
        if (project.Length == 0 || region.Length == 0)
            throw new ProviderException(
                $"{DisplayName}: set the project ID and region on the provider card in Settings.");

        // The credential is resolved before the send so the token exchange can be
        // async; SendForStreamAsync's key rotation is a no-op here (Vertex quotas
        // are per project — a second credential would not help a 429).
        var credential = keys.GetKey(Id)
            ?? throw new ProviderException($"No API key configured for {DisplayName}. Add one in Settings.");
        var token = GoogleServiceAccount.LooksLikeServiceAccountJson(credential)
            ? await GoogleServiceAccount.GetTokenAsync(credential, http, DisplayName, cancellationToken)
            : credential.Trim();

        var body = BuildBody(request);
        var url = RequestUrl(region, project, request.ModelId);
        await using var stream = await ProviderHttp.SendForStreamAsync(
            http, keys, Id, DisplayName, requiresApiKey: true,
            _ =>
            {
                var httpRequest = ProviderHttp.CreateJsonPost(url, body);
                httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                return httpRequest;
            },
            cancellationToken);

        await foreach (var providerEvent in AnthropicStream.ParseSseAsync(stream, Id, DisplayName, cancellationToken))
            yield return providerEvent;
    }

    internal static string RequestUrl(string region, string project, string modelId)
    {
        var host = region.Equals("global", StringComparison.OrdinalIgnoreCase)
            ? "aiplatform.googleapis.com"
            : $"{region}-aiplatform.googleapis.com";
        return $"https://{host}/v1/projects/{Uri.EscapeDataString(project)}/locations/{region}" +
               $"/publishers/anthropic/models/{Uri.EscapeDataString(modelId)}:streamRawPredict";
    }

    internal JsonObject BuildBody(LlmRequest request)
    {
        // Vertex names the model in the URL and has no server-side web_search tool.
        var body = AnthropicProvider.BuildRequestBody(request with { EnableWebSearch = false }, Id, effortExtras: false);
        body.Remove("model");
        body["anthropic_version"] = AnthropicVersion;
        return RequestBodyOverride.Apply(body, request.BodyOverride);
    }

    /// <inheritdoc />
    public ProviderRequestPreview PreviewRequest(LlmRequest request)
    {
        return new ProviderRequestPreview(
            "POST",
            RequestUrl(options.VertexRegion.Trim(), options.VertexProjectId.Trim(), request.ModelId),
            ProviderHttp.BearerPreviewHeaders(keys, Id),
            BuildBody(request));
    }
}
