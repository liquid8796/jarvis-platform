namespace JarvisCode.Core.Providers;

/// <summary>Controls a host may expose for an adapter. Defaults preserve existing host behavior.</summary>
public sealed record ProviderCapabilities(
    bool SupportsThinkingEffort = true,
    bool SupportsMaxOutputTokens = true,
    bool SupportsWebSearchControl = true,
    bool ReportsExactUsage = true)
{
    /// <summary>Whether a delayed first event is a stalled request rather than expected queue/startup time.</summary>
    public bool SupportsPostToolStallRetry { get; init; } = true;

    /// <summary>
    /// Whether a request without declared tools can be used for tool-free auxiliary processing.
    /// Browser-backed assistants may retain native tools even when the local tool list is empty.
    /// </summary>
    public bool SupportsToolFreeInference { get; init; } = true;

    public static ProviderCapabilities For(ILlmProvider provider) =>
        ProviderDecorators.Unwrap(provider) is IProviderCapabilities declared
            ? declared.Capabilities : new();
}

public interface IProviderCapabilities
{
    ProviderCapabilities Capabilities { get; }
}
