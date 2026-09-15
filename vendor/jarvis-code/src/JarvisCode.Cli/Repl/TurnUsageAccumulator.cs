using JarvisCode.Core.Models;

namespace JarvisCode.Cli.Repl;

/// <summary>
/// Converts cumulative reports within one turn into additions to session totals. A cancelled or
/// failed turn still keeps the completed calls it reported, and repeated reports add nothing.
/// </summary>
internal sealed class TurnUsageAccumulator
{
    private Usage _reported = Usage.Zero;

    public Usage Observe(Usage total)
    {
        var delta = new Usage(
            Math.Max(0, total.InputTokens - _reported.InputTokens),
            Math.Max(0, total.OutputTokens - _reported.OutputTokens),
            Math.Max(0, total.CacheReadInputTokens - _reported.CacheReadInputTokens),
            Math.Max(0, total.CacheCreationInputTokens - _reported.CacheCreationInputTokens))
        {
            IsEstimated = total.IsEstimated,
        };
        _reported = _reported.Add(delta);
        return delta;
    }
}
