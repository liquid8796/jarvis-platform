using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>What a <c>jarvis-code://</c> link asks the app to do.</summary>
public enum DeepLinkKind
{
    /// <summary>The link named a host or a path this build does not route.</summary>
    Unrecognized,

    /// <summary>claude.ai/new?surface=chat — open the Chat composer.</summary>
    NewChat,

    /// <summary>code/new — a Code session, optionally in a folder and with a prompt.</summary>
    NewCodeSession,

    /// <summary>code/continue?session=last|local_… — reopen a Code session.</summary>
    ContinueCodeSession,

    /// <summary>code/needs-input[?session=local_…] — open a session waiting for an answer.</summary>
    NeedsInput,

    /// <summary>resume?session=&lt;uuid&gt; — import a Claude Code CLI transcript and open it.</summary>
    ResumeCliSession,

    /// <summary>session/{id} — reopen a session by the link "Copy session link" writes.</summary>
    Session,
}

/// <summary>
/// A parsed <c>jarvis-code://</c> link.
/// </summary>
/// <param name="Kind">Which route the link named.</param>
/// <param name="Session">
/// The session the link named: "last" or a <c>local_…</c> id for continue,
/// a <c>local_…</c> id for needs-input, a uuid for resume, null when absent.
/// </param>
/// <param name="Folders">The folders a <c>code/new</c> link asked to open.</param>
/// <param name="Prompt">The prompt a <c>code/new</c> link carried, capped like the reference's.</param>
/// <param name="Source">The link's <c>source</c> parameter (the OS surface it came from).</param>
public sealed record DeepLink(
    DeepLinkKind Kind,
    string? Session = null,
    IReadOnlyList<string>? Folders = null,
    string? Prompt = null,
    string? Source = null);

/// <summary>
/// The <c>jarvis-code://</c> protocol, ported from the reference's
/// <c>claudeURLHandler</c> (app.asar <c>index.chunk-DnlgCaT3.js</c>, its <c>iI</c>)
/// and the link shapes its OS entry points build (<c>UF</c>/<c>_Zt</c>). Parsing is
/// separate from routing so the shapes can be tested without a window.
///
/// The scheme is this app's own rather than the reference's rebranded
/// <c>jarvis://</c>: the session links "Copy session link" writes were already
/// shipped under <c>jarvis-code://</c>, and one registered protocol that opens
/// everything beats two that each open half.
///
/// Deliberately not routed, each declared in the parity suite's surface manifest:
/// the <c>cowork</c>, <c>login</c>, <c>preview</c>, <c>hotkey</c> and
/// <c>debug-handoff</c> hosts, which are a cloud surface, an account sign-in
/// callback and two nest-build-only handlers.
/// </summary>
public static partial class DeepLinks
{
    /// <summary>The protocol this app registers, where the reference registers "claude".</summary>
    public const string Scheme = "jarvis-code";

    private const string SessionHost = "session";

    /// <summary>The link "Copy session link" puts on the clipboard.</summary>
    public static string ForSession(string sessionId) => $"{Scheme}://{SessionHost}/{sessionId}";

    /// <summary>
    /// The session id in a session link, or null when the argument is not one.
    /// Anything else is refused rather than guessed at: the argument arrives from
    /// the shell, and this decides what opens.
    /// </summary>
    public static string? SessionIdIn(string argument) =>
        Parse(argument) is { Kind: DeepLinkKind.Session, Session: { } id } ? id : null;

    /// <summary>The reference's own cap on a deep link's prompt (its <c>vj</c>).</summary>
    public const int MaxPromptLength = 14336;

    /// <summary>The reference's <c>fQt</c>: what a local session id may look like.</summary>
    [GeneratedRegex("^local_[A-Za-z0-9-]{1,64}$")]
    private static partial Regex LocalSessionId();

    /// <summary>The reference's <c>JF</c>: the uuid a resume link carries.</summary>
    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Uuid();

    /// <summary>True when the argument is a link this app's protocol owns.</summary>
    public static bool IsDeepLink(string argument) =>
        argument.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a link. An unroutable one comes back as
    /// <see cref="DeepLinkKind.Unrecognized"/> rather than throwing, which is what
    /// the reference does with every path it does not know.
    /// </summary>
    public static DeepLink Parse(string link)
    {
        // Uri normalises a traversal away before it can be looked at, so the raw
        // argument is checked first: a link names an id, not a path.
        if (string.IsNullOrWhiteSpace(link) ||
            link.Contains("..", StringComparison.Ordinal) ||
            !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return new DeepLink(DeepLinkKind.Unrecognized);
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var source = query["source"];
        // A URI with a host keeps the host out of AbsolutePath; one written with a
        // single slash ("jarvis:/resume") puts everything there, so both are read.
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/";
        }

        switch (uri.Host.ToLowerInvariant())
        {
            case "claude.ai" or "jarvis.ai":
                // The reference's own New Chat entry is claude.ai/new?surface=chat.
                return path == "/new" && query["surface"] is "chat" or null
                    ? new DeepLink(DeepLinkKind.NewChat, Source: source)
                    : new DeepLink(DeepLinkKind.Unrecognized);

            case "code":
                return ParseCode(path, query, source);

            case "resume":
                var resumeSession = query["session"];
                return resumeSession is not null && Uuid().IsMatch(resumeSession)
                    ? new DeepLink(DeepLinkKind.ResumeCliSession, resumeSession, Source: source)
                    : new DeepLink(DeepLinkKind.Unrecognized);

            case SessionHost:
                // The id has to survive being handed to a session store, so it is
                // held to the characters a session id is made of.
                var id = uri.AbsolutePath.Trim('/');
                return id.Length > 0 &&
                    id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                    ? new DeepLink(DeepLinkKind.Session, id, Source: source)
                    : new DeepLink(DeepLinkKind.Unrecognized);

            default:
                return new DeepLink(DeepLinkKind.Unrecognized);
        }
    }

    private static DeepLink ParseCode(
        string path,
        System.Collections.Specialized.NameValueCollection query,
        string? source)
    {
        var session = query["session"];
        switch (path)
        {
            case "/continue":
                // The reference's hQt: "last", or a local session id. A missing
                // session is a malformed continue link, not "the latest one".
                return session is not null && (session == "last" || LocalSessionId().IsMatch(session))
                    ? new DeepLink(DeepLinkKind.ContinueCodeSession, session, Source: source)
                    : new DeepLink(DeepLinkKind.Unrecognized);

            case "/needs-input":
                // Here the session is optional: with none, the reference opens
                // whichever session has waited longest.
                return session is null || LocalSessionId().IsMatch(session)
                    ? new DeepLink(DeepLinkKind.NeedsInput, session, Source: source)
                    : new DeepLink(DeepLinkKind.Unrecognized);

            case "/new":
                var prompt = query["q"] ?? query["prompt"];
                if (prompt is { Length: > MaxPromptLength })
                {
                    prompt = prompt[..MaxPromptLength];
                }

                var folders = query.GetValues("folder") ?? [];
                return new DeepLink(
                    DeepLinkKind.NewCodeSession,
                    Folders: folders,
                    Prompt: string.IsNullOrEmpty(prompt) ? null : prompt,
                    Source: source);

            default:
                return new DeepLink(DeepLinkKind.Unrecognized);
        }
    }

    /// <summary>Builds a link the way the reference's <c>UF</c> does, for the jump list.</summary>
    public static string Url(
        string host,
        string path,
        string source,
        params (string Key, string Value)[] parameters)
    {
        var query = new List<string>();
        foreach (var (key, value) in parameters)
        {
            query.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }

        query.Add($"source={Uri.EscapeDataString(source)}");
        return $"{Scheme}://{host}{path}?{string.Join('&', query)}";
    }
}

/// <summary>
/// Why a <c>jarvis://resume</c> link could not open the session it named. The
/// reference has one toast per category (its <c>XZt</c>), so the reason the user
/// reads says what to try rather than that something went wrong.
/// </summary>
public enum ResumeFailure
{
    AuthExpired,
    Network,
    TranscriptMissing,
    Other,
}

/// <summary>The reference's four resume-failure toasts, verbatim but for the brand.</summary>
public static class ResumeFailureMessages
{
    public static string For(ResumeFailure failure) => failure switch
    {
        ResumeFailure.AuthExpired =>
            "Couldn't open that session. Sign in to the desktop app and try again.",
        ResumeFailure.Network =>
            "Couldn't open that session. Check your network connection and try again.",
        ResumeFailure.TranscriptMissing =>
            "Couldn't open that session. Its transcript may have been removed.",
        _ => "Couldn't open that session from Jarvis Code.",
    };
}
