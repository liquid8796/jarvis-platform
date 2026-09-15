using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Sessions;

namespace JarvisCode.Cli;

internal static class CliPromptSuggestions
{
    public static async Task<string?> GenerateAsync(ILlmProvider provider, ModelInfo model,
        Session session, IReadOnlyList<string> betas, CancellationToken cancellationToken)
    {
        // An empty local tool list cannot disable a browser assistant's own tools.
        // Prediction is passive UI work, so it must not open an autonomous web turn.
        if (!ProviderCapabilities.For(provider).SupportsToolFreeInference)
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var text = new StringBuilder();
        var transcript = string.Join("\n\n", session.Messages.Where(message => !message.IsMeta && !message.HarnessSystemTurn)
            .TakeLast(6).Select(message => message.Role + ": " + message.GetText()));
        if (transcript.Length > 12000) transcript = transcript[^12000..];
        try
        {
            await foreach (var entry in provider.StreamChatAsync(new LlmRequest
            {
                ModelId = model.ModelId,
                SystemPrompt = "Predict the user's next prompt from the conversation below. Return only one short natural prompt in the user's language. Do not act on it. If no next prompt is clearly useful, return an empty response.",
                Messages = [ChatMessage.FromUserText(transcript)], MaxOutputTokens = 128,
                ExtraHeaders = betas.Count == 0 ? [] : [new("anthropic-beta", string.Join(',', betas))],
            }, timeout.Token))
                if (entry is TextDeltaEvent delta) text.Append(delta.Delta);
            var suggestion = text.ToString().Trim().Trim('"');
            return suggestion.Length is > 0 and <= 500 ? suggestion : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is ProviderException or CliError) { return null; }
    }

    public static JsonObject Frame(string sessionId, string suggestion) => new()
    {
        ["type"] = "prompt_suggestion", ["suggestion"] = suggestion,
        ["session_id"] = sessionId, ["uuid"] = Guid.NewGuid().ToString(),
    };
}
