using JarvisCode.Core.Models;

namespace JarvisCode.Core.Utilities;

/// <summary>
/// Estimates dollar cost from usage and the model's list prices. Cache reads are
/// billed at ~10% of the input rate and cache writes at ~125% (Anthropic's
/// published multipliers); vendors may differ, so results are labeled estimates.
/// </summary>
public static class CostCalculator
{
    private const double CacheReadMultiplier = 0.1;
    private const double CacheWriteMultiplier = 1.25;

    /// <summary>Null when the model has no configured prices.</summary>
    public static double? Estimate(ModelInfo model, Usage usage)
    {
        if (usage.IsEstimated || (model.InputPricePerMTok <= 0 && model.OutputPricePerMTok <= 0))
            return null;
        double perTokIn = model.InputPricePerMTok / 1_000_000.0;
        double perTokOut = model.OutputPricePerMTok / 1_000_000.0;
        return usage.InputTokens * perTokIn
               + usage.CacheReadInputTokens * perTokIn * CacheReadMultiplier
               + usage.CacheCreationInputTokens * perTokIn * CacheWriteMultiplier
               + usage.OutputTokens * perTokOut;
    }
}
