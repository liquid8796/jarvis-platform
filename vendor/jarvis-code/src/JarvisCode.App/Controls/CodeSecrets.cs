using System.Text.RegularExpressions;

namespace JarvisCode.App.Controls;

/// <summary>
/// Finds credential-shaped spans in a fenced block so the transcript can cover
/// them until the user asks to see them, the way the reference's Code renderer
/// does. The reference's own detector is not readable from the bundle, so the
/// rule set here is this port's: vendor key prefixes with a fixed shape, and
/// assignments whose name says the value is a secret.
/// </summary>
internal static partial class CodeSecrets
{
    /// <summary>A credential-shaped span, as an offset and length into the block.</summary>
    internal readonly record struct Span(int Start, int Length);

    /// <summary>Below this, a value is too short to be a real credential.</summary>
    private const int MinimumSecretLength = 8;

    [GeneratedRegex(
        @"\b(?:sk-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})",
        RegexOptions.ExplicitCapture)]
    private static partial Regex VendorKey { get; }

    /// <summary>
    /// An assignment whose name marks the value as a secret. The value is
    /// captured, not the name, so the reader still sees which setting it is.
    /// </summary>
    [GeneratedRegex(
        @"(?i)\b(?:password|passwd|secret|token|api[_-]?key|apikey|access[_-]?key|client[_-]?secret|private[_-]?key|auth)\b\s*[:=]\s*[""']?(?<value>[^\s""'`,;]{8,})[""']?",
        RegexOptions.ExplicitCapture)]
    private static partial Regex SecretAssignment { get; }

    /// <summary>An HTTP Authorization value, whose scheme stays readable.</summary>
    [GeneratedRegex(
        @"(?i)\b(?:Bearer|Basic|Token)\s+(?<value>[A-Za-z0-9._~+/=-]{12,})",
        RegexOptions.ExplicitCapture)]
    private static partial Regex AuthorizationValue { get; }

    /// <summary>
    /// The credential spans in <paramref name="code"/>, ordered and never
    /// overlapping. Empty when there is nothing to cover.
    /// </summary>
    internal static IReadOnlyList<Span> Find(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return [];
        }

        var spans = new List<Span>();
        foreach (Match match in VendorKey.Matches(code))
        {
            spans.Add(new Span(match.Index, match.Length));
        }

        foreach (var regex in new[] { SecretAssignment, AuthorizationValue })
        {
            foreach (Match match in regex.Matches(code))
            {
                var group = match.Groups["value"];
                if (group.Success && group.Length >= MinimumSecretLength)
                {
                    spans.Add(new Span(group.Index, group.Length));
                }
            }
        }

        if (spans.Count == 0)
        {
            return [];
        }

        spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<Span> { spans[0] };
        foreach (var span in spans.Skip(1))
        {
            var last = merged[^1];
            if (span.Start <= last.Start + last.Length)
            {
                var end = Math.Max(last.Start + last.Length, span.Start + span.Length);
                merged[^1] = new Span(last.Start, end - last.Start);
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    /// <summary>Replaces every credential span with bullets of the same length.</summary>
    internal static string Mask(string code, IReadOnlyList<Span> spans)
    {
        if (spans.Count == 0)
        {
            return code;
        }

        var builder = new System.Text.StringBuilder(code.Length);
        int at = 0;
        foreach (var span in spans)
        {
            builder.Append(code, at, span.Start - at);
            builder.Append('•', span.Length);
            at = span.Start + span.Length;
        }

        builder.Append(code, at, code.Length - at);
        return builder.ToString();
    }
}
