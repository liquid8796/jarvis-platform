namespace JarvisCode.App.Services;

/// <summary>One of the reference card's three buttons, in its order.</summary>
public enum SessionNotFoundAction
{
    ImportCliSessions,
    Archive,
    Delete,
}

/// <summary>
/// The reference's "Session not found on disk" card (the ccd chunk
/// <c>c11959232-DM8o5ho4.js</c>: its <c>wN</c> over the transcript's
/// <c>transcriptUnavailable</c> flag). The desktop raises it when a local
/// session's own transcript file has gone from disk while its message buffer is
/// empty — the session row is still listed, so the card is what the transcript
/// area shows instead of nothing.
///
/// This port's transcript for a session <em>is</em> its stored session file, so
/// the same condition is "the store no longer holds the session the row names".
/// The pure half lives here so the wording and the button set are unit-tested.
/// </summary>
public static class SessionNotFound
{
    public const string Title = SessionDialogs.SessionNotFoundTitle;

    public const string Body = SessionDialogs.SessionNotFoundBody;

    /// <summary>The reference's own accessible names, which differ from the button text.</summary>
    public static string AccessibleName(SessionNotFoundAction action) => action switch
    {
        SessionNotFoundAction.ImportCliSessions => SessionDialogs.ImportCliSessions,
        SessionNotFoundAction.Archive => "Archive session",
        _ => "Delete session",
    };

    public static string Label(SessionNotFoundAction action) => action switch
    {
        SessionNotFoundAction.ImportCliSessions => SessionDialogs.ImportCliSessions,
        SessionNotFoundAction.Archive => SessionDialogs.Archive,
        _ => SessionDialogs.Delete,
    };

    /// <summary>
    /// The card's buttons. The reference offers Import CLI sessions only where its
    /// desktop bridge exposes the importer, so a build without one shows the other
    /// two — here that is a machine with no Claude Code CLI transcripts to import.
    /// </summary>
    public static IReadOnlyList<SessionNotFoundAction> Actions(bool canImportCliSessions) =>
        canImportCliSessions
            ? [SessionNotFoundAction.ImportCliSessions, SessionNotFoundAction.Archive, SessionNotFoundAction.Delete]
            : [SessionNotFoundAction.Archive, SessionNotFoundAction.Delete];

    /// <summary>
    /// Whether opening the session should show the card: the row
    /// exists in the list the sidebar drew, and the store cannot load it.
    /// </summary>
    public static bool ShouldShow(bool listed, bool loaded) => listed && !loaded;

    /// <summary>Whether the machine has a Claude Code CLI transcript directory to import from.</summary>
    public static bool CanImportCliSessions() =>
        System.IO.Directory.Exists(CliSessionImporter.DefaultRoot);
}
