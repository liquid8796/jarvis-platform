using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// The branch-prefix rules the reference's Pull requests settings apply
/// (ion-dist <c>shared-3-uZZTMVWj.js</c>: <c>rb</c> for the format and <c>ub</c> for
/// the reserved names; <c>c71860c77-D1Dqt2F3.js</c>'s <c>Fa</c> for the order they run in).
/// </summary>
public static partial class BranchPrefixes
{
    public const string FormatMessage = "Use 1-40 letters, numbers, or . _ -";
    public const string ReservedMessage = "This prefix is reserved for protected branches";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,39}$")]
    private static partial Regex Format();

    private static readonly HashSet<string> ReservedNames =
        new(["main", "master", "staging", "warm"], StringComparer.Ordinal);

    private static readonly string[] ReservedPrefixes = ["prod-", "release"];

    /// <summary>The reference's <c>rb</c>: git-safe, at most 40 characters, no "..", no trailing "." or ".lock".</summary>
    public static bool IsValidFormat(string prefix) =>
        Format().IsMatch(prefix) && !prefix.Contains("..") && !prefix.EndsWith('.') && !prefix.EndsWith(".lock");

    /// <summary>The reference's <c>ub</c>: names that belong to protected branches.</summary>
    public static bool IsReserved(string prefix)
    {
        var lower = prefix.ToLowerInvariant();
        return ReservedNames.Contains(lower) || ReservedPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>The reference's <c>Fa</c>: null when the prefix passes (an empty one falls back to the default), else which rule failed.</summary>
    public static string? Problem(string prefix) =>
        prefix.Length == 0 ? null : !IsValidFormat(prefix) ? "format" : IsReserved(prefix) ? "reserved" : null;

    /// <summary>The message the page shows under the box, or null.</summary>
    public static string? ProblemMessage(string prefix) => Problem(prefix) switch
    {
        "format" => FormatMessage,
        "reserved" => ReservedMessage,
        _ => null,
    };

    /// <summary>What the page commits on blur: an empty box means the default, a bad value is not saved.</summary>
    public static string? Commit(string text, string fallback)
    {
        var trimmed = text.Trim();
        if (Problem(trimmed) is not null)
        {
            return null;
        }

        return trimmed.Length == 0 ? fallback : trimmed;
    }
}
