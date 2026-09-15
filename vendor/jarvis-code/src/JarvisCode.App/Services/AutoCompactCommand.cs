using JarvisCode.Core.Agent;

namespace JarvisCode.App.Services;

/// <summary>
/// What /autocompact says, ported from the reference CLI 2.1.251's two halves:
/// <c>g</c> renders the status with no argument, <c>qje</c> answers an argument.
/// The command itself reads and saves the setting; every sentence lives here so
/// it can be exercised without a window.
/// </summary>
public static class AutoCompactCommand
{
    public const string EnvironmentTakesPrecedence =
        "CLAUDE_CODE_AUTO_COMPACT_WINDOW is set and takes precedence. Unset it to change this setting.";

    private const string ThresholdAdvice =
        "Auto-compact summarizes the conversation when context usage approaches this limit. " +
        "The actual threshold is the minimum of this setting and your model's maximum context window.";

    private const string AutoAdvice =
        "The auto setting picks a window tuned for your model and is strongly recommended for the " +
        "best cost and performance.";

    private const string OverrideWarning =
        "Overriding auto may result in high token usage, especially when resuming long sessions.";

    /// <summary>The reference's <c>g</c>: /autocompact with no argument.</summary>
    public static string Status(AutoCompactWindow window, bool autoCompactEnabled)
    {
        string tokens = ContextWindows.FormatTokens(window.Configured);
        string capped = window.Configured > window.Window
            ? $" · capped to {ContextWindows.FormatTokens(window.Window)} by model"
            : "";
        List<string> lines =
        [
            "Auto-compact window: " + window.Source switch
            {
                AutoCompactWindowSource.Auto => "auto",
                AutoCompactWindowSource.Experiment or
                    AutoCompactWindowSource.ClientData => $"auto ({tokens} tokens){capped}",
                AutoCompactWindowSource.Env =>
                    $"{tokens} tokens (from CLAUDE_CODE_AUTO_COMPACT_WINDOW){capped}",
                AutoCompactWindowSource.UnknownModel =>
                    $"{tokens} tokens (default for an unrecognized model){capped}",
                AutoCompactWindowSource.ModelDefault =>
                    $"{tokens} tokens (default for this model){capped}",
                _ => $"{tokens} tokens (from settings){capped}",
            },
        ];
        if (!autoCompactEnabled)
        {
            lines.Add("Auto-compact is currently disabled (see /config)");
        }

        lines.Add(ThresholdAdvice);
        lines.Add(AutoAdvice);
        if (window.Source is AutoCompactWindowSource.Env or AutoCompactWindowSource.Settings)
        {
            lines.Add(OverrideWarning);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The reference's own reading of the argument: reset/unset/default mean
    /// "auto", and everything else goes through <c>fqe</c>.
    /// </summary>
    public static bool TryReadArgument(string argument, out int? window)
    {
        var lowered = argument.Trim().ToLowerInvariant();
        return ContextWindows.TryParseWindow(
            lowered is "reset" or "unset" or "default" ? "auto" : lowered, out window);
    }

    public static string ParseFailure(string argument) =>
        $"Couldn't parse '{argument}'. Expected 'auto' or 100k–1M tokens " +
        "(e.g. 500k, 200000, or 200 as shorthand)";

    /// <summary>
    /// What the reference reports once the setting is saved: which window is
    /// actually in force, and whether something outranked what was asked for.
    /// </summary>
    public static string Applied(int? chosen, AutoCompactWindow inForce, bool overridden)
    {
        string active = ContextWindows.FormatTokens(inForce.Window);
        if (chosen is null)
        {
            return overridden
                ? "Auto-compact window set to auto in settings, but a higher-priority override is active " +
                  $"({active} tokens)"
                : "Auto-compact window set to auto";
        }

        string suffix = overridden
            ? $", but a higher-priority override is active ({active} tokens)"
            : inForce.Window < chosen.Value ? $" (capped to model limit of {active})" : "";
        return $"Auto-compact window set to {ContextWindows.FormatTokens(chosen.Value)} tokens{suffix}";
    }
}
