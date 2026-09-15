using System;

namespace JarvisCode.App.Services;

/// <summary>
/// Which shells a session offers, and what the prompt's Shell line says about them.
/// </summary>
/// <remarks>
/// Measured on CLI 2.1.257: launched from Git Bash, the reference registers a
/// single <c>Bash</c> tool and writes <c>Shell: bash</c>; launched from PowerShell
/// (and in the desktop app) it registers both shells and writes the two-shell
/// sentence. The desktop front-end here always has both; the headless CLI
/// follows the shell it was started from, read off the environment Git Bash
/// leaves behind (<c>MSYSTEM</c>, or a <c>SHELL</c> naming bash).
/// </remarks>
internal static class ShellEnvironment
{
    /// <summary>The reference's Shell line where both shells are registered, verbatim.</summary>
    internal const string TwoShells =
        "PowerShell (primary); Bash tool also available for POSIX scripts — each takes its own syntax.";

    /// <summary>True when this process was started from a Git Bash (MSYS) shell.</summary>
    internal static bool LaunchedFromBash =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSYSTEM")) ||
        (Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell &&
         shell.Contains("bash", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the PowerShell tool is registered for this front-end.</summary>
    internal static bool OffersPowerShell(bool headless) => !(headless && LaunchedFromBash);

    /// <summary>What follows "Shell: " in the environment block.</summary>
    internal static string PromptLine(bool headless) => OffersPowerShell(headless) ? TwoShells : "bash";
}
