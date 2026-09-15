using System.Net;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers;

namespace JarvisCode.Providers.Tests;

public sealed class FakeKeySource(string? key = "test-key") : IApiKeySource
{
    public string? GetKey(string providerId) => key;
}

public static class ProviderTestHelpers
{
    static ProviderTestHelpers() =>
        // Retry backoff must not slow the suite down; 1 ms keeps the policy observable.
        JarvisCode.Providers.Http.ProviderHttp.RetryBaseDelay = TimeSpan.FromMilliseconds(1);

    public static LlmRequest SampleRequest() => new()
    {
        ModelId = "test-model",
        SystemPrompt = "system prompt",
        Messages =
        [
            ChatMessage.FromUserText("hello"),
            new ChatMessage(Role.Assistant,
            [
                new TextBlock("let me check"),
                new ToolCallBlock("call-1", "Read", """{"file_path":"a.txt"}"""),
            ]),
            new ChatMessage(Role.User, [new ToolResultBlock("call-1", "Read", "file body", IsError: false)]),
        ],
        Tools =
        [
            new ToolDefinition("Read", "Reads a file", new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "object",
                ["properties"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["file_path"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "path",
                    },
                },
                ["required"] = new System.Text.Json.Nodes.JsonArray("file_path"),
            }),
        ],
    };

    public static HttpClient ClientFor(string sseBody, out StubHttpHandler handler, HttpStatusCode status = HttpStatusCode.OK)
    {
        handler = new StubHttpHandler(status, sseBody);
        return new HttpClient(handler);
    }

    public static async Task<List<ProviderEvent>> CollectAsync(ILlmProvider provider, LlmRequest request)
    {
        var events = new List<ProviderEvent>();
        await foreach (var providerEvent in provider.StreamChatAsync(request, CancellationToken.None))
            events.Add(providerEvent);
        return events;
    }
}
