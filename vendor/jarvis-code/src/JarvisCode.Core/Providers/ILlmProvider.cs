namespace JarvisCode.Core.Providers;

/// <summary>
/// Strategy interface for one LLM vendor. Implementations translate the neutral
/// request/message model into the vendor's wire format and stream events back.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Stable identifier used in settings and the model catalog (e.g. "anthropic").</summary>
    string Id { get; }

    /// <summary>Human-readable vendor name (e.g. "Anthropic Claude").</summary>
    string DisplayName { get; }

    /// <summary>False for local servers like Ollama, where a key is optional.</summary>
    bool RequiresApiKey => true;

    /// <summary>Streams one assistant response. Must end with a <see cref="ResponseCompletedEvent"/>.</summary>
    IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, CancellationToken cancellationToken);
}

/// <summary>A transparent observer/policy wrapper; capability checks inspect its underlying adapter.</summary>
public interface IDecoratedProvider
{
    ILlmProvider InnerProvider { get; }
}

public static class ProviderDecorators
{
    public static ILlmProvider Unwrap(ILlmProvider provider)
    {
        var seen = new HashSet<ILlmProvider>(ReferenceEqualityComparer.Instance);
        while (provider is IDecoratedProvider decorated && seen.Add(provider)) provider = decorated.InnerProvider;
        return provider;
    }
}

/// <summary>Raised when a provider call fails (HTTP error, malformed stream, missing key).</summary>
public sealed class ProviderException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>False when replay or model fallback could bypass a terminal safety boundary.</summary>
    public bool CanRetry { get; init; } = true;
}
