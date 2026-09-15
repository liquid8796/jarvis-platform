namespace JarvisCode.Core.Models;

/// <summary>
/// Token usage reported by a provider for one model response. Anthropic reports
/// cached prompt tokens separately from InputTokens; vendors whose input count
/// already includes cached tokens leave the cache fields at zero.
/// </summary>
public sealed record Usage(
    long InputTokens,
    long OutputTokens,
    long CacheReadInputTokens = 0,
    long CacheCreationInputTokens = 0)
{
    public static readonly Usage Zero = new(0, 0);

    /// <summary>True when these counts include estimates rather than provider measurements.</summary>
    public bool IsEstimated { get; init; }

    /// <summary>Full prompt size of the call, cached or not — the real context measure.</summary>
    public long TotalInputTokens => InputTokens + CacheReadInputTokens + CacheCreationInputTokens;

    public Usage Add(Usage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CacheReadInputTokens + other.CacheReadInputTokens,
        CacheCreationInputTokens + other.CacheCreationInputTokens)
    {
        IsEstimated = IsEstimated || other.IsEstimated,
    };
}
