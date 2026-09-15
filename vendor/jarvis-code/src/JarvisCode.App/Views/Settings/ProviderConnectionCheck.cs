using System.Globalization;
using System.Text;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.ChatGptWeb;
using ChatMessage = JarvisCode.Core.Models.ChatMessage;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// What one card's connection test needs that another's does not: how long to wait, and what to
/// say when the answer is empty or never arrives. A keyed provider is pointed at its key; the
/// browser session is pointed at its window, which is the only place its trouble is visible.
/// </summary>
public sealed record ProviderConnectionPlan(
    string ProviderId,
    string DisplayName,
    string ModelId,
    TimeSpan Timeout,
    string WhenEmpty,
    string WhenTimedOut)
{
    /// <summary>A hosted provider that has not answered within a minute is not going to.</summary>
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The browser session boots a browser, loads chatgpt.com and waits for the model
    /// picker to hydrate. It does not submit an inference request.
    /// </summary>
    public static readonly TimeSpan BrowserTimeout = TimeSpan.FromMinutes(3);

    public static ProviderConnectionPlan ForApiKey(string providerId, string displayName, string modelId) =>
        new(providerId,
            displayName,
            modelId,
            ApiTimeout,
            $"{displayName} answered with nothing. Check the key and the endpoint.",
            $"The test timed out after {ApiTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.");

    public static ProviderConnectionPlan ForBrowserSession() =>
        new(ChatGptWebProvider.ProviderId,
            "ChatGPT",
            ChatGptWebProvider.AutoModelSlug,
            BrowserTimeout,
            "ChatGPT did not expose any composer models. Open the ChatGPT window to check sign-in.",
            "The test timed out. Use “Show ChatGPT window” to see what the session is showing.");
}

/// <summary>
/// Tests API credentials with one real turn and browser availability by reading the model
/// picker without inference. Picker availability does not prove authentication or model execution.
/// </summary>
public static class ProviderConnectionCheck
{
    /// <summary>Short enough that a healthy provider answers it in one breath.</summary>
    public const string Prompt = "Reply with the single word: ready";

    private const int PreviewChars = 80;

    public static async Task<string> RunAsync(IProviderRegistry providers, ProviderConnectionPlan plan)
    {
        try
        {
            var provider = providers.Get(plan.ProviderId);
            using var timeout = new CancellationTokenSource(plan.Timeout);
            if (!ProviderCapabilities.For(provider).SupportsToolFreeInference)
            {
                if (ProviderDecorators.Unwrap(provider) is not ChatGptWebProvider browser)
                    return "This provider cannot guarantee a tool-free connection test. No prompt was sent.";
                var controls = await browser.ReadComposerControlsAsync(
                    scopeId: "connection-check", modelId: null, includeEfforts: false,
                    cancellationToken: timeout.Token);
                return controls.Models.Any(IsDiscoveredModel)
                    ? "ChatGPT model picker is available. No prompt was sent; model inference was not tested."
                    : "ChatGPT did not expose any composer models. Open the ChatGPT window to check sign-in; no prompt was sent.";
            }
            var request = new LlmRequest
            {
                ModelId = plan.ModelId,
                SystemPrompt = "",
                Messages = [ChatMessage.FromUserText(Prompt)],
            };

            var reply = new StringBuilder();
            await foreach (var providerEvent in provider.StreamChatAsync(request, timeout.Token))
            {
                if (providerEvent is TextDeltaEvent delta)
                {
                    reply.Append(delta.Delta);
                }
            }

            var answer = reply.ToString().Trim();
            return answer.Length > 0
                ? $"Works — {plan.DisplayName} answered: “{Shorten(answer)}”"
                : plan.WhenEmpty;
        }
        catch (ProviderException ex)
        {
            return ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Transport failures already arrive wrapped as ProviderException; this is our own clock.
            return plan.WhenTimedOut;
        }
        catch (Exception ex)
        {
            // Anything a provider throws unwrapped would otherwise vanish into a dropped task and
            // leave the card saying "Testing…" for as long as it stays open.
            return $"The test failed unexpectedly: {ex.GetType().Name} — {ex.Message}";
        }
    }

    private static string Shorten(string text) =>
        text is { Length: > PreviewChars } overlong ? overlong[..PreviewChars] + "…" : text;

    private static bool IsDiscoveredModel(ChatGptPickerOption option)
    {
        var key = option.Key.StartsWith(ChatGptWebProvider.DynamicModelPrefix, StringComparison.OrdinalIgnoreCase)
            ? option.Key[ChatGptWebProvider.DynamicModelPrefix.Length..] : option.Key;
        return !string.IsNullOrWhiteSpace(key)
            && !key.Equals(ChatGptWebProvider.AutoModelSlug, StringComparison.OrdinalIgnoreCase);
    }
}
