namespace JarvisCode.Core.Utilities;

/// <summary>
/// "Did you mean …" suggestions for mistyped command names (ported from claw-code's
/// Levenshtein-based slash-command suggestions).
/// </summary>
public static class DidYouMean
{
    public static IReadOnlyList<string> Suggest(string input, IEnumerable<string> candidates, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];
        var scored = new List<(string Name, int Distance)>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (candidate.Length == 0)
                continue;
            if (candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            {
                scored.Add((candidate, 0));
                continue;
            }
            int threshold = input.Length >= 8 ? 3 : 2;
            int distance = Levenshtein(input.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance <= threshold)
                scored.Add((candidate, distance));
        }
        return [.. scored
            .OrderBy(s => s.Distance)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Name)
            .Take(max)];
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
