using System.IO;
using System.Text;
using JarvisCode.Core.Utilities;

namespace JarvisCode.App.Services;

/// <summary>
/// The switch <c>/debug</c> throws, and where it writes: the reference's
/// <c>enableDebugLogging()</c> (CLI 2.1.257 at 181419695) sets
/// <c>runtimeDebugEnabled</c> for the rest of the process and answers whether
/// debug logging was already on, so the command can tell the user that nothing
/// before this invocation was captured.
///
/// This build's hosts arm <see cref="DiagnosticLog"/> while composing their
/// service graph, so the answer is normally "already on" and the reference's
/// "Debug Logging Just Enabled" section is normally absent; a host that left the
/// sink detached gets it armed here instead, which is the state that section
/// describes.
/// </summary>
public static class SessionDebugLog
{
    private static Func<string>? _path;
    private static Action? _arm;

    /// <summary>Called by the host that owns the log file.</summary>
    public static void Register(Func<string> path, Action arm)
    {
        _path = path;
        _arm = arm;
    }

    /// <summary>The file this session's debug lines go to, or null when no host registered one.</summary>
    public static string? Path => _path?.Invoke();

    /// <summary>Arms the log if it is not armed; answers whether it already was.</summary>
    public static bool Enable()
    {
        var wasAlreadyOn = DiagnosticLog.IsEnabled;
        if (!wasAlreadyOn)
        {
            _arm?.Invoke();
        }

        return wasAlreadyOn;
    }
}

/// <summary>
/// <c>/debug</c>: the reference's Debug Skill, ported from CLI 2.1.257 (its
/// <c>Co()</c> registration at 194116424, the constants <c>he</c>/<c>wo</c>,
/// the tail block <c>vo</c> and the two read failures <c>Eo</c>).
///
/// The reference's command does two things: it turns session debug logging on
/// for the rest of the process — <c>enableDebugLogging()</c> sets
/// <c>runtimeDebugEnabled</c> and returns whether it was already on, with no
/// expiry — and then hands the model the tail of that log, the settings paths
/// and its five instructions. <see cref="SessionDebugLog"/> is the same switch
/// here.
///
/// Two measured deltas ride the prompt: its <c>## Daemon</c> section is dropped,
/// because this build runs no background daemon for it to describe (the
/// reference's covers <c>&amp; &lt;prompt&gt;</c> jobs and <c>claude agents</c>,
/// neither of which exists here), and the restart sentence names
/// <c>jarvis --debug</c>, the flag this build actually has.
/// </summary>
internal static class DebugCommand
{
    /// <summary>The reference's <c>he</c>: how many trailing lines the prompt shows.</summary>
    public const int TailLines = 20;

    /// <summary>The reference's <c>wo</c>: how much of the log's tail is read to find them.</summary>
    public const int TailBytes = 65536;

    /// <summary>The row the "/" menu shows.</summary>
    public const string MenuDescription = "Turn on debug logging and investigate problems";

    /// <summary>The description the command surface carries.</summary>
    public const string Description = "Enable debug logging for this session and help diagnose issues";

    public const string ArgumentHint = "[issue description]";

    /// <summary>The reference's own subagent for questions about the harness itself.</summary>
    private const string GuideAgent = "claude-code-guide";

    /// <summary>Where the session's settings live, in the order the reference lists them.</summary>
    /// <param name="User">This installation's own settings file.</param>
    /// <param name="Project">The checked-in project settings.</param>
    /// <param name="Local">The gitignored per-checkout overlay.</param>
    public readonly record struct SettingsPaths(string User, string Project, string Local);

    /// <summary>
    /// The reference's <c>Ut</c>, which is what its "Log size:" line is formatted
    /// with: bytes below a kilobyte, then one decimal place with a trailing
    /// <c>.0</c> dropped.
    /// </summary>
    public static string FormatSize(long bytes)
    {
        var kb = bytes / 1024d;
        if (kb < 1)
        {
            return $"{bytes} bytes";
        }

        if (kb < 1024)
        {
            return Trim(kb) + "KB";
        }

        var mb = kb / 1024d;
        return mb < 1024 ? Trim(mb) + "MB" : Trim(mb / 1024d) + "GB";
    }

    private static string Trim(double value)
    {
        var text = value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    /// <summary>
    /// The reference's <c>vo</c>: the log's size, then its last
    /// <see cref="TailLines"/> lines in a fence.
    /// </summary>
    public static string TailBlock(string content, long bytesTotal)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var tail = string.Join('\n', lines.Skip(Math.Max(0, lines.Length - TailLines)));
        return $"""
            Log size: {FormatSize(bytesTotal)}

            ### Last {TailLines} lines

            ```
            {tail}
            ```
            """;
    }

    /// <summary>Reads the tail of the log file, answering the reference's two failures.</summary>
    public static string ReadTail(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return "No log file exists yet.";
        }

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var total = stream.Length;
            var take = (int)Math.Min(total, TailBytes);
            stream.Seek(total - take, SeekOrigin.Begin);
            var buffer = new byte[take];
            stream.ReadExactly(buffer, 0, take);
            return TailBlock(Encoding.UTF8.GetString(buffer), total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Failed to read last {TailLines} lines: {ex.Message}";
        }
    }

    /// <summary>
    /// The reference's prompt for one invocation.
    /// </summary>
    /// <param name="issue">What the user typed after /debug, if anything.</param>
    /// <param name="logPath">The debug log this session writes to.</param>
    /// <param name="logTail">The block <see cref="ReadTail"/> produced.</param>
    /// <param name="settings">The three settings files.</param>
    /// <param name="wasAlreadyOn">
    /// What <see cref="SessionDebugLog.Enable"/> answered: false adds the
    /// reference's "Debug Logging Just Enabled" section.
    /// </param>
    public static string Prompt(
        string issue, string logPath, string logTail, SettingsPaths settings, bool wasAlreadyOn)
    {
        var justEnabled = wasAlreadyOn
            ? ""
            : $"""

                ## Debug Logging Just Enabled

                Debug logging was OFF for this session until now. Nothing prior to this /debug invocation was captured.

                Tell the user that debug logging is now active at `{logPath}`, ask them to reproduce the issue, then re-read the log. If they can't reproduce, they can also restart with `jarvis --debug` to capture logs from startup.

                """;

        var described = issue.Trim().Length > 0
            ? issue.Trim()
            : "The user did not describe a specific issue. Read the debug log and summarize any errors, warnings, or notable issues.";

        return $"""
            # Debug Skill

            Help the user debug an issue they're encountering in this current Jarvis Code session.
            {justEnabled}
            ## Session Debug Log

            The debug log for the current session is at: `{logPath}`

            {logTail}

            For additional context, grep for [ERROR] and [WARN] lines across the full file.

            ## Issue Description

            {described}

            ## Settings

            Remember that settings are in:
            * user - {settings.User}
            * project - {settings.Project}
            * local - {settings.Local}

            ## Instructions

            1. Review the user's issue description
            2. The last {TailLines} lines show the debug file format. Look for [ERROR] and [WARN] entries, stack traces, and failure patterns across the file
            3. Consider launching the {GuideAgent} subagent to understand the relevant Jarvis Code features
            4. Explain what you found in plain language
            5. Suggest concrete fixes or next steps
            """;
    }
}
