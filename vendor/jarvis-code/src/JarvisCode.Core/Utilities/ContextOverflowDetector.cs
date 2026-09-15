using System.Text.RegularExpressions;

namespace JarvisCode.Core.Utilities;

/// <summary>
/// Classifies provider errors as context-window overflows and learns the server's
/// real window from the error text (ported from claw-code's progressive
/// context-overflow recovery). Providers word this differently — Anthropic
/// ("prompt is too long: N tokens > M maximum"), OpenAI ("maximum context length is
/// M tokens"), llama.cpp ("exceeds the available context size"), gateways — so
/// detection is a substring sniff and parsing tries known shapes in order.
/// </summary>
public static class ContextOverflowDetector
{
    private static readonly string[] OverflowFragments =
    [
        "context_window",
        "context window",
        "context_length_exceeded",
        "maximum context length",
        "exceed_context_size",
        "exceeds the available context size",
        "context size has been exceeded",
        "prompt is too long",
        "input length and `max_tokens` exceed",
        "too many total tokens",
    ];

    private static readonly Regex[] WindowPatterns =
    [
        // Anthropic: "prompt is too long: 213462 tokens > 200000 maximum"
        new(@">\s*([\d][\d,_]*)\s*maximum", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // OpenAI: "This model's maximum context length is 128000 tokens"
        new(@"maximum context length is\s*([\d][\d,_]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Gateways: "configured limit of 81920 tokens"
        new(@"configured limit of\s*([\d][\d,_]*)\s*tokens", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // llama.cpp style: "(81920 tokens)"
        new(@"\(([\d][\d,_]*)\s*tokens\)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static bool IsContextOverflow(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
            return false;
        return OverflowFragments.Any(fragment =>
            errorMessage.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The reference's <c>qj</c>/<c>iL</c>: how many tokens over its limit the
    /// request was, read from the provider's own arithmetic ("prompt is too
    /// long: 213462 tokens &gt; 200000 maximum"). A compaction retry drops at
    /// least this much rather than guessing at a fraction.
    /// </summary>
    public static bool TryParseOverflowGap(string? errorMessage, out int gapTokens)
    {
        gapTokens = 0;
        if (string.IsNullOrEmpty(errorMessage))
            return false;

        var match = OverflowGap.Match(errorMessage);
        if (!match.Success)
            return false;

        if (!int.TryParse(Digits(match.Groups[1].Value), out int actual) ||
            !int.TryParse(Digits(match.Groups[2].Value), out int limit))
        {
            return false;
        }

        gapTokens = actual - limit;
        return gapTokens > 0;
    }

    private static string Digits(string value) => value.Replace(",", "").Replace("_", "");

    private static readonly Regex OverflowGap = new(
        @"([\d][\d,_]*)\s*tokens\s*>\s*([\d][\d,_]*)\s*maximum",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the server-reported context window so thresholds can adapt to the
    /// real limit. Returns false when no plausible number is present.
    /// </summary>
    public static bool TryParseWindowTokens(string? errorMessage, out int tokens)
    {
        tokens = 0;
        if (string.IsNullOrEmpty(errorMessage))
            return false;
        foreach (var pattern in WindowPatterns)
        {
            var match = pattern.Match(errorMessage);
            if (!match.Success)
                continue;
            var digits = match.Groups[1].Value.Replace(",", "").Replace("_", "");
            if (int.TryParse(digits, out int parsed) && parsed >= 1_000)
            {
                tokens = parsed;
                return true;
            }
        }
        return false;
    }
}
