using JarvisCode.Core.Providers;

namespace JarvisCode.App.Services;

/// <summary>
/// The composer's effort ladder. The reference CLI's ladder on disk is
/// low / medium / high / xhigh / max plus ultracode (xhigh + orchestration) and
/// the desktop popover labels xhigh "Extra high"; per the user's ask the picker
/// always offers all six rungs. On the Anthropic wire the level travels as
/// output_config.effort ("low"…"max"); Ultracode travels as xhigh and flips the
/// session's orchestration harness instead (see <see cref="SystemReminders"/>).
/// There is still no "off" rung: a stored legacy "Off" (or anything
/// unrecognized) resolves to High, the recommended default.
/// </summary>
internal static class EffortLevels
{
    /// <summary>Faster → smarter, the order the slider lays them out.</summary>
    public static readonly string[] Names = ["Low", "Medium", "High", "Extra high", "Max", "Ultracode"];

    public const string Default = "High";
    public const string Ultracode = "Ultracode";

    // The reference popover's strings, verbatim.
    public const string Header = "Effort";
    public const string Faster = "Faster";
    public const string Smarter = "Smarter";
    public const string HelpTitle = "Effort";
    public const string HelpBody =
        "Higher effort means more thorough responses, but takes longer and uses your limits faster.";

    /// <summary>
    /// The CLI's wire spellings map onto the desktop labels, so "/effort xhigh"
    /// and a settings file written by hand both land on the right rung.
    /// </summary>
    private static readonly (string Alias, string Name)[] Aliases =
    [
        ("xhigh", "Extra high"),
        ("extra", "Extra high"),
        ("extra-high", "Extra high"),
        ("extrahigh", "Extra high"),
    ];

    /// <summary>Strict form: matches a rung or a wire alias, never falls back.</summary>
    public static bool TryResolve(string? name, out string resolved)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        resolved = Names.FirstOrDefault(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? Aliases.FirstOrDefault(a => a.Alias.Equals(trimmed, StringComparison.OrdinalIgnoreCase)).Name
            ?? string.Empty;
        return resolved.Length > 0;
    }

    public static string Resolve(string? name)
    {
        if (Names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) is { } match)
        {
            return match;
        }

        var trimmed = name?.Trim() ?? string.Empty;
        return Aliases.FirstOrDefault(a => a.Alias.Equals(trimmed, StringComparison.OrdinalIgnoreCase)).Name
            ?? Default;
    }

    public static int IndexOf(string? name) => Array.IndexOf(Names, Resolve(name));

    public static bool IsUltracode(string? name)
        => string.Equals(Resolve(name), Ultracode, StringComparison.Ordinal);

    public static ThinkingEffort ResolveEffort(string? name) => Resolve(name) switch
    {
        "Low" => ThinkingEffort.Low,
        "Medium" => ThinkingEffort.Medium,
        "Extra high" => ThinkingEffort.XHigh,
        "Max" => ThinkingEffort.Max,
        Ultracode => ThinkingEffort.XHigh, // ultracode runs at xhigh on the wire
        _ => ThinkingEffort.High,
    };

    /// <summary>
    /// The reference keywords. Neither changes the request payload — each rides
    /// the message as a system-reminder (the CLI's ultrathink stopped being a
    /// budget bump in 2.x; it asks for deeper reasoning in words).
    /// </summary>
    public static bool HasUltrathinkKeyword(string? prompt) =>
        prompt is not null && prompt.Contains("ultrathink", StringComparison.OrdinalIgnoreCase);

    public static bool HasUltracodeKeyword(string? prompt) =>
        prompt is not null && prompt.Contains("ultracode", StringComparison.OrdinalIgnoreCase);
}
