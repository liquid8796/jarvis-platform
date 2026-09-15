using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Http;

namespace JarvisCode.Providers.Ollama;

/// <summary>
/// Ollama adapter for the native /api/chat endpoint — the API the official docs
/// cover for both a local server and the cloud host (https://ollama.com). The API
/// key is optional: local servers need none, while cloud requests carry it as
/// "Authorization: Bearer &lt;key&gt;" (docs.ollama.com/api/authentication).
/// Responses stream as newline-delimited JSON rather than SSE, and tool calls
/// arrive complete and without ids, so ids are synthesized for the neutral model
/// and tool results are correlated back by function name.
/// </summary>
public sealed class OllamaProvider(HttpClient http, IApiKeySource keys, IProviderEndpoints endpoints)
    : ILlmProvider, IRequestInspector
{
    public const string ProviderId = "ollama";

    /// <summary>Ollama's hosted service; requests there must carry an API key.</summary>
    public const string CloudBaseUrl = "https://ollama.com";

    /// <summary>A local server, which serves unauthenticated requests.</summary>
    public const string LocalBaseUrl = "http://localhost:11434";

    /// <summary>
    /// Unconfigured installs talk to the official server; point the base URL at
    /// <see cref="LocalBaseUrl"/> (or any host) to use a self-hosted one instead.
    /// </summary>
    public const string DefaultBaseUrl = CloudBaseUrl;

    private const string CloudHost = "ollama.com";

    /// <summary>Reasoning traces stream in their own block; tool calls start after it.</summary>
    private const int ThinkingBlockIndex = 0;
    private const int FirstToolCallIndex = 1;

    public string Id => ProviderId;

    public string DisplayName => "Ollama";

    /// <summary>
    /// Local servers need no key, so requests stay keyless; pointing the base URL
    /// at the official cloud makes the key mandatory and surfaces the usual
    /// missing-key error instead of a bare 401 from the server.
    /// </summary>
    public bool RequiresApiKey => IsOfficialCloud(endpoints.GetBaseUrl(ProviderId));

    /// <summary>True when the configured base URL points at Ollama's hosted service.</summary>
    public static bool IsOfficialCloud(string? baseUrl)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri))
            return false;
        return uri.Host.Equals(CloudHost, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith($".{CloudHost}", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public ProviderRequestPreview PreviewRequest(LlmRequest request) =>
        new("POST", ResolveChatEndpoint(endpoints.GetBaseUrl(ProviderId)),
            ProviderHttp.BearerPreviewHeaders(keys, ProviderId),
            BuildBody(request));

    private static JsonObject BuildBody(LlmRequest request) =>
        RequestBodyOverride.Apply(BuildRequestBody(request), request.BodyOverride);

    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = ResolveChatEndpoint(endpoints.GetBaseUrl(ProviderId));
        var body = BuildBody(request);
        await using var stream = await ProviderHttp.SendForStreamAsync(
            http, keys, ProviderId, DisplayName, RequiresApiKey,
            key =>
            {
                var httpRequest = ProviderHttp.CreateJsonPost(url, body);
                if (key is not null)
                    httpRequest.Headers.Add("Authorization", $"Bearer {key}");
                return httpRequest;
            },
            cancellationToken);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var thinking = new StringBuilder();
        bool thinkingCompleted = false;
        bool sawToolCall = false;
        int nextToolCallIndex = FirstToolCallIndex;
        long inputTokens = 0;
        long outputTokens = 0;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
                continue;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node?["error"] is { } error)
                throw new ProviderException($"{DisplayName}: {ProviderHttp.DescribeError(error)}");

            var message = node?["message"];
            if (message?["thinking"].AsText() is { Length: > 0 } thinkingDelta)
            {
                thinking.Append(thinkingDelta);
                yield return new ThinkingDeltaEvent(ThinkingBlockIndex, thinkingDelta);
            }

            var text = message?["content"].AsText();
            var toolCalls = message?["tool_calls"] as JsonArray;
            bool hasPayload = !string.IsNullOrEmpty(text) || toolCalls is { Count: > 0 };
            // The reasoning block closes as soon as visible output starts, so it keeps
            // its place ahead of the text and tool-use blocks of the same message.
            if (hasPayload && thinking.Length > 0 && !thinkingCompleted)
            {
                thinkingCompleted = true;
                yield return new ThinkingCompletedEvent(ThinkingBlockIndex, thinking.ToString(), Signature: null);
            }

            if (!string.IsNullOrEmpty(text))
                yield return new TextDeltaEvent(text);

            foreach (var call in (toolCalls ?? []).OfType<JsonObject>())
            {
                var function = call["function"] as JsonObject;
                var name = function?["name"].AsText();
                if (string.IsNullOrEmpty(name))
                    continue;
                sawToolCall = true;
                int index = nextToolCallIndex++;
                yield return new ToolCallStartedEvent(index, $"ollama_call_{index}_{name}", name);
                yield return new ToolCallArgumentsDeltaEvent(
                    index, function?["arguments"]?.ToJsonString() ?? "{}");
            }

            if (node?["done"]?.GetValue<bool>() == true)
            {
                inputTokens = node["prompt_eval_count"]?.GetValue<long>() ?? inputTokens;
                outputTokens = node["eval_count"]?.GetValue<long>() ?? outputTokens;
            }
        }

        // A response that produced only reasoning still carries it to the caller.
        if (thinking.Length > 0 && !thinkingCompleted)
            yield return new ThinkingCompletedEvent(ThinkingBlockIndex, thinking.ToString(), Signature: null);

        yield return new ResponseCompletedEvent(sawToolCall, new Usage(inputTokens, outputTokens));
    }

    internal static string ResolveChatEndpoint(string? baseUrl)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        return $"{root.TrimEnd('/')}/api/chat";
    }

    internal static JsonObject BuildRequestBody(LlmRequest request)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
        };
        foreach (var message in request.Messages)
        {
            foreach (var node in BuildMessages(message))
                messages.Add(node);
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["stream"] = true,
            ["messages"] = messages,
            ["options"] = new JsonObject { ["num_predict"] = request.MaxOutputTokens },
        };
        // Ollama has one switch, not a budget. Models are user-entered so there is no
        // capability catalog to gate on; a non-thinking model returns a clear error
        // and the user can set effort back to Off.
        if (request.ThinkingEffort != ThinkingEffort.Off)
            body["think"] = true;
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray([.. request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.InputSchema.DeepClone(),
                },
            })]);
        }
        return body;
    }

    /// <summary>
    /// One neutral message can expand to several wire messages: tool results become
    /// individual role:"tool" messages; assistant text and tool calls merge into one.
    /// </summary>
    private static IEnumerable<JsonNode> BuildMessages(ChatMessage message)
    {
        if (message.Role == Role.User)
        {
            var toolResults = message.Content.OfType<ToolResultBlock>().ToList();
            foreach (var result in toolResults)
            {
                yield return new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_name"] = result.ToolName,
                    ["content"] = result.IsError ? $"ERROR: {result.Content}" : result.Content,
                };
            }

            var text = message.GetText();
            var images = message.Content.OfType<ImageBlock>().ToList();
            var resultImages = ToolResultImages.Collect(toolResults);
            if (text.Length == 0 && images.Count == 0 && resultImages.Count == 0 && toolResults.Count > 0)
                yield break;

            // A bare image with no words reads as noise; say which call produced it.
            var content = text.Length == 0 && resultImages.Count > 0 ? ToolResultImages.Caption(toolResults) : text;
            var user = new JsonObject { ["role"] = "user", ["content"] = content };
            if (images.Count > 0 || resultImages.Count > 0)
                user["images"] = new JsonArray(
                    [.. images.Concat(resultImages).Select(i => (JsonNode)JsonValue.Create(i.Base64Data))]);
            yield return user;
            yield break;
        }

        var assistant = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = message.GetText(),
        };
        var calls = message.ToolCalls.ToList();
        if (calls.Count > 0)
        {
            assistant["tool_calls"] = new JsonArray([.. calls.Select(call => (JsonNode)new JsonObject
            {
                ["function"] = new JsonObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = ParseArguments(call.ArgumentsJson),
                },
            })]);
        }
        yield return assistant;
    }

    /// <summary>Ollama expects tool arguments as an object, not a JSON string.</summary>
    private static JsonNode ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
