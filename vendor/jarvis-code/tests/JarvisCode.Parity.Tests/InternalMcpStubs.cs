using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Models;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Hosts that answer nothing. The surface tests name the tools a server
/// registers, which does not depend on what its host does, so these keep the
/// check free of a running window.
/// </summary>
internal sealed class NullTerminalReader : ITerminalPanelReader
{
    public Task<string?> ReadAsync(string? tabId) => Task.FromResult<string?>(null);

    public Task<TerminalWaitOutcome> WaitForOutputAsync(
        string? tabId, int milliseconds, CancellationToken cancellationToken) =>
        Task.FromResult(TerminalWaitOutcome.NoShell);
}

internal sealed class NullDirectoryHost : ICcdDirectoryHost
{
    public Task<FolderPickResult> PickFolderAsync(string? providedPath, CancellationToken cancellationToken) =>
        Task.FromResult(FolderPickResult.Cancelled());

    public Task<bool> AddDirectoriesAsync(IReadOnlyList<string> paths) => Task.FromResult(false);

    public bool RequestPendingCwd(string path) => false;
}

internal sealed class NullSessionHost : ICcdSessionHost
{
    public BackgroundTaskSuggestions Suggestions { get; } = new();

    public void MarkChapter(string title, string? summary)
    {
    }

    public IReadOnlyList<WidgetToolState> WidgetStates => [];
}

internal sealed class NullSessionMgmtHost : ICcdSessionMgmtHost
{
    public string CurrentSessionId => "parity";

    public string? CurrentSessionTitle => null;

    public bool CanAskUser => false;

    public bool HasCustomGroups => false;

    public Task<IReadOnlyList<SessionMgmtEntry>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SessionMgmtEntry>>([]);

    public Task<SessionMgmtEntry?> GetAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<SessionMgmtEntry?>(null);

    public Task<IReadOnlyList<ChatMessage>?> TranscriptAsync(
        string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChatMessage>?>(null);

    public Task<IReadOnlyList<TranscriptSearchHit>> SearchAsync(
        string query, bool includeArchived, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TranscriptSearchHit>>([]);

    public Task<bool> ArchiveAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> RenameAsync(string sessionId, string title, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<SessionSendResult> SendAsync(string sessionId, string body, CancellationToken cancellationToken) =>
        Task.FromResult(new SessionSendResult(false));
}

/// <summary>
/// A pane driver that refuses every call. The surface tests only read the tool
/// names a server registers, and BrowserPaneTools.Create never touches the
/// driver while building them — so a throwing stub is the honest one: if a
/// future change starts driving during construction, this fails loudly rather
/// than quietly measuring a half-built surface.
/// </summary>
internal sealed class NullPaneDriver : IBrowserPaneDriver
{
    private const string Reason = "the parity surface test names tools; it does not drive the pane";

    public Task<string> ResolveTabIdAsync(string? tabId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task<PaneContext> TabsContextAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task<string> NavigationMarkAsync(string? tabId, CancellationToken cancellationToken) =>
        Task.FromResult("0:");

    public Task<(bool TargetIsPopup, bool AnyPopup)> PopupStateAsync(
        string? tabId, CancellationToken cancellationToken) =>
        Task.FromResult((false, false));

    public Task<string?> LastExternalOriginAsync(string? tabId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<string?> HistoryTargetAsync(
        string? tabId, string direction, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<PaneTabNotes> TabNotesAsync(string tabId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task<JsonNode?> EvaluateAsync(string? tabId, string expression, bool replMode, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task<PaneScreenshot> ScreenshotAsync(string? tabId, double scale, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task ClickAsync(string? tabId, double x, double y, string button, int clickCount, int modifiers, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task HoverAsync(string? tabId, double x, double y, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task WheelAsync(string? tabId, double x, double y, double deltaX, double deltaY, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task DragAsync(string? tabId, double startX, double startY, double endX, double endY, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task InsertTextAsync(string? tabId, string text, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task PressKeyAsync(string? tabId, string key, int modifiers, string? text, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task SetViewportAsync(string? tabId, int width, int height, bool? mobile, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task ClearViewportAsync(string? tabId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task SetColorSchemeAsync(string? tabId, string scheme, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task<PaneNavigation> NavigateAsync(string? tabId, string url, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task<string> CreateTabAsync(bool foreground, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task<bool> SelectTabAsync(string tabId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task<(bool Found, bool WasLast)> CloseTabAsync(string tabId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task<IReadOnlyList<JsonObject>> ConsoleEntriesAsync(string? tabId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task<IReadOnlyList<JsonObject>> NetworkListAsync(string? tabId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);

    public Task<(string Body, bool Base64Encoded)?> NetworkBodyAsync(string? tabId, string requestId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);
}
