using System.Globalization;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>A running preview server, as the port check sees it.</summary>
public sealed record PreviewServerRef(string ServerId, string Name, int Port, string? SessionId);

/// <summary>What binding a port answered.</summary>
public enum PortBindOutcome
{
    /// <summary>The port was bound; the result carries it (an OS-assigned one when 0 was asked for).</summary>
    Bound,

    /// <summary>Something already holds it.</summary>
    InUse,

    /// <summary>The OS refuses it outright — a privileged or excluded port.</summary>
    Reserved,
}

/// <summary>The answer to "can this configuration have this port".</summary>
public sealed record PortBindResult(PortBindOutcome Outcome, int Port)
{
    public static readonly PortBindResult InUse = new(PortBindOutcome.InUse, 0);
    public static readonly PortBindResult Reserved = new(PortBindOutcome.Reserved, 0);

    public static PortBindResult Bound(int port) => new(PortBindOutcome.Bound, port);
}

/// <summary>Either the port to start on, or the reason the caller cannot have one.</summary>
public sealed record PreviewPortResult(int Port, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The reference's launch.json <c>autoPort</c> field and the port-in-use ladder
/// around it (app.asar <c>index.chunk-BHbE7U4N.js</c>, its <c>Jor</c> over
/// <c>qor</c> / <c>Gor</c> / <c>Kor</c>, measured on desktop 1.44121.2.0).
///
/// It is a tri-state and each arm is a different answer, which is the whole point
/// of porting it rather than reading the field as a boolean: <c>true</c> takes a
/// fresh OS-assigned port and hands it to the server through <c>PORT</c>,
/// <c>false</c> says the port is required and names what to stop, and an absent
/// field asks the user to choose between those two. A port held by *another
/// session's* preview server is its own arm again, because <c>preview_stop</c>
/// cannot reach another session's server.
///
/// The one input this port cannot always compute is the external occupant's
/// process name: the reference reads it from a native <c>listTcpListeners</c>
/// binding and takes the unnamed branch whenever that is unavailable, which is the
/// branch taken here when <see cref="PreviewPortProbe"/> cannot name the holder.
/// </summary>
public static class PreviewPorts
{
    /// <summary>The reference's default cap on a name it quotes back (its <c>jM</c>).</summary>
    private const int NameLimit = 120;

    /// <summary>
    /// The reference's <c>jM</c>. A launch.json name is not the app's own text and it
    /// is about to be interpolated inside quotes, so every quote-like character becomes
    /// a plain apostrophe and every control, format or lone-surrogate character becomes
    /// U+FFFD. Only the first line is kept, the result is capped at 120 code points,
    /// and — the reference's own rule — an ellipsis is appended whenever the result
    /// differs from the input at all, not only when it was cut.
    ///
    /// A code-point scan rather than a regex, for the reason <see cref="DisplayText"/>
    /// gives: the reference's classes run under the <c>u</c> flag, where <c>\p{Cs}</c>
    /// means a lone surrogate, while .NET's matches each half of a valid pair and would
    /// replace every astral character.
    /// </summary>
    public static string SanitizeName(string? name)
    {
        var text = name ?? "";
        var builder = new StringBuilder(text.Length);
        var kept = 0;
        var index = 0;
        var cut = false;
        while (index < text.Length)
        {
            if (IsLineSeparator(text[index]))
            {
                // The reference keeps the first line only.
                cut = true;
                break;
            }

            if (kept == NameLimit)
            {
                cut = true;
                break;
            }

            var ch = text[index];
            var isPair = char.IsHighSurrogate(ch) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
            if (!isPair && char.IsSurrogate(ch))
            {
                // A half with no partner is what the reference's \p{Cs} means.
                builder.Append('�');
                kept++;
                index++;
                continue;
            }

            var width = isPair ? 2 : 1;
            var codePoint = isPair ? char.ConvertToUtf32(ch, text[index + 1]) : ch;
            if (IsQuoteLike(codePoint))
                builder.Append('\'');
            else if (IsControlOrFormat(codePoint))
                builder.Append('�');
            else
                builder.Append(text, index, width);

            kept++;
            index += width;
        }

        var result = builder.ToString();
        return !cut && result == text ? text : result + "…";
    }

    /// <summary>The line separators the reference's <c>split</c> cuts the name at.</summary>
    private static bool IsLineSeparator(char ch) =>
        ch is '\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029';

    private static bool IsControlOrFormat(int codePoint) =>
        CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codePoint), 0)
            is UnicodeCategory.Control or UnicodeCategory.Format;

    /// <summary>
    /// The reference's quote-like class: its explicit list, plus the initial and final
    /// quote-punctuation categories its <c>\p{Pi}</c> and <c>\p{Pf}</c> name.
    /// </summary>
    private static bool IsQuoteLike(int c)
    {
        if (c is '`' or '"' or 0x00A8 or 0x00B4 or 0x0374 or 0x0384 or 0x0385
            or 0x02CA or 0x02CB or 0x02DD or 0x02EE
            or 0x05F3 or 0x05F4 or 0x07F4 or 0x07F5
            or 0x1FBD or 0x1FBF or 0x1FFD or 0x1FFE
            or 0x201A or 0x201E or 0x2057 or 0x276E or 0x276F or 0x2E42 or 0x3003
            or 0xFF02 or 0xFF07 or 0xFF40)
        {
            return true;
        }

        if (c is (>= 0x02B9 and <= 0x02BD) or (>= 0x02F4 and <= 0x02F6)
            or (>= 0x1FCD and <= 0x1FCF) or (>= 0x1FDD and <= 0x1FDF) or (>= 0x1FED and <= 0x1FEF)
            or (>= 0x2032 and <= 0x2037) or (>= 0x275B and <= 0x2760)
            or (>= 0x301D and <= 0x301F) or (>= 0x1F676 and <= 0x1F678))
        {
            return true;
        }

        return CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(c), 0)
            is UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation;
    }

    /// <summary>
    /// The reference's <c>Gor</c>: what to tell the user when the field is absent and
    /// the port is taken. The launch file is named rather than hardcoded to
    /// <c>.claude/launch.json</c>, because this build resolves <c>.jarvis/launch.json</c>
    /// first and falls back to <c>.claude/launch.json</c> — advice naming the wrong one
    /// would be an edit that changes nothing.
    /// </summary>
    public static string Advice(int port, string launchFile) =>
        $"Ask the user: does this server need port {port} specifically (e.g. for OAuth callbacks, webhooks, " +
        $"or CORS)? If yes, set \"autoPort\": false in {launchFile} and free port {port}. If no, set " +
        $"\"autoPort\": true in {launchFile} AND check the start command for hardcoded port flags (e.g. " +
        "--port, -p) — remove them so the server uses the assigned port via the PORT environment " +
        "variable. Then retry.";

    /// <summary>The reference's <c>Kor</c>: the port is required and something else has it.</summary>
    public static string Required(int port, PreviewServerRef? occupant, string? process)
    {
        if (occupant is not null)
        {
            var name = SanitizeName(occupant.Name);
            return $"Port {port} is required by this server (autoPort is false) but is in use by preview " +
                $"server \"{name}\" ({occupant.ServerId}). Ask the user if they want to stop \"{name}\" to " +
                $"free port {port}. If yes, call preview_stop with serverId \"{occupant.ServerId}\" and retry.";
        }

        return process is not null
            ? $"Port {port} is required by this server but is in use by {process}. Stop that process to free " +
                $"port {port} and try again."
            : $"Port {port} is required by this server but is in use by another process. Run `lsof -i :{port}` " +
                $"to find what's using it, then free port {port} and try again.";
    }

    /// <summary>The reference's EACCES arm, whose parenthesis differs per platform.</summary>
    public static string Reserved(int port, string launchFile) =>
        $"Port {port} is reserved by the OS (" +
        (OperatingSystem.IsWindows()
            ? "a Windows excluded port range, or a privileged port"
            : "a privileged port below 1024") +
        $") and cannot be bound. Pick a different port in {launchFile}, or set \"autoPort\": true to use an " +
        "OS-assigned port.";

    /// <summary>
    /// The reference's <c>Jor</c>. <paramref name="autoPort"/> is the tri-state read off
    /// the configuration; <paramref name="bind"/> answers whether a port can be taken
    /// (asked for 0 when a fresh one is wanted); <paramref name="occupantProcess"/> names
    /// the external holder, or nothing, which is the reference's own fallback.
    /// </summary>
    public static PreviewPortResult Resolve(
        int port,
        bool? autoPort,
        IReadOnlyList<PreviewServerRef> running,
        string? sessionId,
        string launchFile,
        Func<int, PortBindResult> bind,
        Func<int, string?> occupantProcess)
    {
        // A configuration with no port of its own has nothing to collide with.
        if (port <= 0)
            return new PreviewPortResult(port, null);

        var occupant = running.FirstOrDefault(s => s.Port == port);
        if (occupant is not null)
        {
            var otherSession = sessionId is not null && occupant.SessionId is not null
                && !string.Equals(occupant.SessionId, sessionId, StringComparison.Ordinal);

            // A server this session cannot stop is not offered as one to stop.
            if (autoPort == true)
                return Reassign(port, otherSession ? null : occupant, launchFile, bind, occupantProcess);

            if (otherSession)
            {
                return Failed(
                    $"Port {port} is in use by another chat's dev server \"{SanitizeName(occupant.Name)}\". " +
                    "preview_stop won't stop another chat's server. " +
                    (autoPort == false
                        ? "Ask the user to stop it from that chat, or to change \"autoPort\" in " +
                            $"{launchFile} so this session can use a different port."
                        : Advice(port, launchFile)));
            }

            return autoPort == false
                ? Failed(Required(port, occupant, null))
                : Failed(
                    $"Port {port} is in use by preview server \"{SanitizeName(occupant.Name)}\" " +
                    $"({occupant.ServerId}). " + Advice(port, launchFile));
        }

        var bound = bind(port);
        if (bound.Outcome == PortBindOutcome.Bound)
            return new PreviewPortResult(bound.Port, null);

        if (autoPort == true)
            return Reassign(port, null, launchFile, bind, occupantProcess);

        if (bound.Outcome == PortBindOutcome.Reserved)
            return Failed(Reserved(port, launchFile));

        var process = occupantProcess(port);
        if (autoPort == false)
            return Failed(Required(port, null, process));

        return Failed(
            (process is not null
                ? $"Port {port} is in use by {process} (not a preview server). "
                : $"Port {port} is in use by another process (not a preview server). Run `lsof -i :{port}` " +
                    "to identify what's using it. ")
            + Advice(port, launchFile));
    }

    /// <summary>The reference's <c>qor</c>: take a fresh OS-assigned port, or say why not.</summary>
    private static PreviewPortResult Reassign(
        int port,
        PreviewServerRef? occupant,
        string launchFile,
        Func<int, PortBindResult> bind,
        Func<int, string?> occupantProcess)
    {
        var fresh = bind(0);
        if (fresh.Outcome == PortBindOutcome.Bound)
            return new PreviewPortResult(fresh.Port, null);

        if (occupant is not null)
        {
            return Failed(
                $"Port {port} is in use by preview server \"{SanitizeName(occupant.Name)}\" " +
                $"({occupant.ServerId}) and automatic reassignment to a fresh port failed. Retry in a moment, " +
                $"or call preview_stop with serverId \"{occupant.ServerId}\" to free port {port} and retry.");
        }

        var process = occupantProcess(port);
        return Failed(
            $"Port {port} is in use{(process is not null ? $" by {process}" : "")} and automatic port " +
            $"reassignment failed. Find and stop whatever is using port {port}, then try again.");
    }

    private static PreviewPortResult Failed(string message) => new(0, message);
}
