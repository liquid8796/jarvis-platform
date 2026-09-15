using JarvisCode.Core.Models;

namespace JarvisCode.App.Services;

/// <summary>
/// The adapters that connect the in-process MCP servers to the running window.
/// Each takes plain delegates rather than the window itself, so the servers stay
/// testable and the wiring in CodeWorkspace stays a list of lambdas.
/// </summary>
public sealed class TerminalPanelReader(
    Func<string?, string?> read,
    Func<string?, TimeSpan, CancellationToken, Task<TerminalWaitOutcome>> wait,
    Func<Func<object?>, Task<object?>> onUiThread) : ITerminalPanelReader
{
    public async Task<string?> ReadAsync(string? tabId) =>
        (string?)await onUiThread(() => read(tabId));

    public async Task<TerminalWaitOutcome> WaitForOutputAsync(
        string? tabId, int milliseconds, CancellationToken cancellationToken)
    {
        // The wait itself must not be marshalled — it blocks until output
        // arrives, and holding the UI thread for it would freeze the app.
        var window = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return await wait(tabId, window, cancellationToken);
    }
}

/// <summary>ccd_directory against the session's working directory and grant list.</summary>
public sealed class CcdDirectoryHost(
    Func<string?, CancellationToken, Task<FolderPickResult>> pick,
    Action<string> grant,
    Func<string, bool> moveWorkingDirectory) : ICcdDirectoryHost
{
    public Task<FolderPickResult> PickFolderAsync(string? providedPath, CancellationToken cancellationToken) =>
        pick(providedPath, cancellationToken);

    public Task<bool> AddDirectoriesAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            grant(path);
        }

        return Task.FromResult(true);
    }

    public bool RequestPendingCwd(string path) => moveWorkingDirectory(path);
}

/// <summary>ccd_session against the chip queue, the chapter list and the widget tile.</summary>
public sealed class CcdSessionHost(
    BackgroundTaskSuggestions suggestions,
    Action<string, string?> markChapter,
    Func<IReadOnlyList<WidgetToolState>> widgetStates) : ICcdSessionHost
{
    public BackgroundTaskSuggestions Suggestions => suggestions;

    public void MarkChapter(string title, string? summary) => markChapter(title, summary);

    public IReadOnlyList<WidgetToolState> WidgetStates => widgetStates();
}

/// <summary>ccd_session_mgmt against the session store, the sidebar and the delivery channel.</summary>
public sealed class CcdSessionMgmtHost(
    Func<string> currentSessionId,
    Func<string?> currentSessionTitle,
    Func<bool> canAskUser,
    Func<bool> hasCustomGroups,
    Func<CancellationToken, Task<IReadOnlyList<SessionMgmtEntry>>> list,
    Func<string, CancellationToken, Task<IReadOnlyList<ChatMessage>?>> transcript,
    Func<string, CancellationToken, Task<bool>> archive,
    Func<string, string, CancellationToken, Task<bool>> rename,
    Func<string, string, CancellationToken, Task<SessionSendResult>> send) : ICcdSessionMgmtHost
{
    public string CurrentSessionId => currentSessionId();

    public string? CurrentSessionTitle => currentSessionTitle();

    public bool CanAskUser => canAskUser();

    public bool HasCustomGroups => hasCustomGroups();

    public Task<IReadOnlyList<SessionMgmtEntry>> ListAsync(CancellationToken cancellationToken) =>
        list(cancellationToken);

    public async Task<SessionMgmtEntry?> GetAsync(string sessionId, CancellationToken cancellationToken) =>
        (await list(cancellationToken))
        .FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));

    public Task<IReadOnlyList<ChatMessage>?> TranscriptAsync(
        string sessionId, CancellationToken cancellationToken) =>
        transcript(sessionId, cancellationToken);

    /// <summary>
    /// Substring search over the rendered transcript, with the reference's
    /// snippet window around the first hit. Case-insensitive, as its doc says.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptSearchHit>> SearchAsync(
        string query, bool includeArchived, int limit, CancellationToken cancellationToken)
    {
        List<TranscriptSearchHit> hits = [];
        foreach (var session in await list(cancellationToken))
        {
            if (hits.Count >= limit)
            {
                break;
            }

            if (session.IsArchived && !includeArchived)
            {
                continue;
            }

            if (await transcript(session.SessionId, cancellationToken) is not { } messages)
            {
                continue;
            }

            var text = CcdSessionMgmtTools.RenderTranscript(messages);
            var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                continue;
            }

            hits.Add(new TranscriptSearchHit(
                session.SessionId,
                session.Title,
                session.Cwd,
                session.IsArchived,
                session.LastActivityAt ?? DateTimeOffset.MinValue,
                Snippet(text, at, query.Length)));
        }

        return hits;
    }

    public Task<bool> ArchiveAsync(string sessionId, CancellationToken cancellationToken) =>
        archive(sessionId, cancellationToken);

    public Task<bool> RenameAsync(string sessionId, string title, CancellationToken cancellationToken) =>
        rename(sessionId, title, cancellationToken);

    public Task<SessionSendResult> SendAsync(
        string sessionId, string body, CancellationToken cancellationToken) =>
        send(sessionId, body, cancellationToken);

    /// <summary>Roughly 80 characters either side of the match, on one line.</summary>
    internal const int SnippetRadius = 80;

    internal static string Snippet(string text, int at, int length)
    {
        var start = Math.Max(0, at - SnippetRadius);
        var end = Math.Min(text.Length, at + length + SnippetRadius);
        var slice = text[start..end].ReplaceLineEndings(" ");
        return (start > 0 ? "…" : "") + slice + (end < text.Length ? "…" : "");
    }
}
