using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>What a model-discovery probe found.</summary>
public sealed record ModelDiscoveryResult(IReadOnlyList<string> Ids, string? Error);

/// <summary>
/// "Test model discovery" from the reference's third-party inference window
/// (ion-dist <c>c71860c77-DuPx-LoQ.js</c>: <c>ZRMqH+j2yz</c> for the button,
/// <c>jU4z+3Uk7+</c> for the "Model discovery" label, <c>afjdqvFrPn</c> for
/// "found {n} models", <c>sFbp0HrPGn</c> for the form that also counts the ids the
/// user added that discovery did not return, <c>DrfFB3GGAI</c> for the list of
/// those and <c>EauHnet1z9</c> for its "…and {n} more" tail).
/// </summary>
public static class ModelDiscovery
{
    /// <summary>How many missing ids the reference names before folding the rest into "…and {n} more".</summary>
    public const int MaxNamedMissing = 5;

    /// <summary>The reference's summary line when every added id came back.</summary>
    public static string Found(int found) => $"found {found} model{(found == 1 ? "" : "s")}";

    /// <summary>The reference's summary line when some added ids did not.</summary>
    public static string FoundWithMissing(int found, int missing) =>
        $"found {found} model{(found == 1 ? "" : "s")}; {missing} of yours not in the list";

    public static string NotReturned(IEnumerable<string> ids) => $"Not returned by discovery: {string.Join(", ", ids)}";

    public static string AndMore(int more) => $"…and {more} more";

    /// <summary>The ids the user added that discovery did not return, in the order they were added.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<string> configured, IReadOnlyList<string> discovered) =>
        [.. configured.Where(id => !discovered.Contains(id, StringComparer.OrdinalIgnoreCase))];

    /// <summary>
    /// Asks an endpoint what models it serves. OpenAI-compatible endpoints answer
    /// <c>GET {base}/models</c> with <c>{"data":[{"id":…}]}</c>; Ollama answers
    /// <c>GET {base}/api/tags</c> with <c>{"models":[{"name":…}]}</c>, and both
    /// shapes are read here because both are configurable on the Providers page.
    /// </summary>
    public static async Task<ModelDiscoveryResult> ProbeAsync(
        HttpClient http, string baseUrl, string? apiKey, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var root))
        {
            return new ModelDiscoveryResult([], "Enter a base URL to discover models");
        }

        foreach (var (path, read) in new (string, Func<JsonNode?, IReadOnlyList<string>>)[]
                 {
                     ("/models", OpenAiShape),
                     ("/api/tags", OllamaShape),
                 })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, root.AbsoluteUri.TrimEnd('/') + path);
                if (apiKey is { Length: > 0 })
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                }

                using var response = await http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var ids = read(JsonNode.Parse(body));
                if (ids.Count > 0)
                {
                    return new ModelDiscoveryResult(ids, null);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // Try the next shape; the last failure decides the message below.
            }
        }

        return new ModelDiscoveryResult([], "The endpoint returned no model list.");
    }

    private static IReadOnlyList<string> OpenAiShape(JsonNode? root) =>
        [.. (root?["data"] as JsonArray ?? [])
            .Select(entry => (string?)(entry as JsonObject)?["id"] ?? "")
            .Where(id => id.Length > 0)];

    private static IReadOnlyList<string> OllamaShape(JsonNode? root) =>
        [.. (root?["models"] as JsonArray ?? [])
            .Select(entry => (string?)(entry as JsonObject)?["name"] ?? "")
            .Where(id => id.Length > 0)];
}
