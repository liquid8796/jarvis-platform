using System.Collections.ObjectModel;
using System.Globalization;
using Jarvis.Agent.Desktop.Infrastructure;
using Jarvis.Protocol;

namespace Jarvis.Agent.Desktop.ViewModels;

public sealed class SessionRowViewModel : ObservableViewModel
{
    private LocalSessionOverview _value;
    public SessionRowViewModel(LocalSessionOverview value) => _value = value;
    public AgentSessionIdentity Identity => _value.Identity;
    public string SessionId => _value.Session.SessionId;
    public string Label => _value.Session.Label;
    public string DeviceId => _value.Session.DeviceId;
    public string Workspace => string.IsNullOrWhiteSpace(_value.Session.Workspace) ? "No workspace selected" : _value.Session.Workspace;
    public string WorkspaceRevision => $"Workspace revision {_value.Session.WorkspaceRevision}";
    public string Parent => _value.Session.ParentSessionId ?? "No parent session";
    public string Status => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(_value.Activity.State(_value.Session.ClosedAt));
    public bool IsClosed => _value.Session.ClosedAt is not null;
    public int Running => _value.Activity.RunningCalls;
    public int Queued => _value.Activity.QueuedCalls;
    public int Jobs => _value.Activity.RunningJobs;
    public int Tasks => _value.Activity.RunningTasks;
    public long Unread => _value.Session.UnreadEvents;
    public string WorkSummary => $"{Running} active calls · {Queued} queued · {Jobs} processes · {Tasks} tasks";
    public string TaskQueue => $"{_value.Activity.QueuedTasks} durable tasks queued";
    public string LastActive => _value.Session.LastActiveAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string HeldResources => ResourceText(_value.Activity.HeldResources, "No resources held");
    public string WaitingResources => ResourceText(_value.Activity.WaitingResources, "Not waiting for a resource");
    private static string ResourceText(IReadOnlyList<string> values, string empty) => values.Count == 0 ? empty
        : string.Join("\n", values.Select(value => value == "*" ? "Computer resources (unknown command effects)" : value));
    public void Update(LocalSessionOverview value) { _value = value; Changed(null); }
}

/// <summary>Operator-only projection. No session handles or transcript content are exposed in this view.</summary>
public sealed class SessionsViewModel : ObservableViewModel
{
    private readonly Func<IReadOnlyList<LocalSessionOverview>> _query;
    private readonly Func<bool> _connected;
    private readonly Action<AgentSessionIdentity, bool> _stop;
    private readonly Func<SessionRowViewModel, bool> _confirmClose;
    private readonly Dictionary<AgentSessionIdentity, SessionRowViewModel> _rows = new();
    private IReadOnlyList<LocalSessionOverview> _latest = [];
    private SessionRowViewModel? _selected;
    private string _search = "", _error = "", _actionMessage = "", _updated = "Not connected";
    private bool _showClosed;
    public ObservableCollection<SessionRowViewModel> Items { get; } = [];
    public SessionRowViewModel? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { StopCommand.Refresh(); CloseCommand.Refresh(); Changed(nameof(HasSelection)); } }
    }
    public string Search { get => _search; set { if (Set(ref _search, value ?? "")) UpdateVisible(); } }
    public bool ShowClosed { get => _showClosed; set { if (Set(ref _showClosed, value)) UpdateVisible(); } }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string ActionMessage { get => _actionMessage; private set => Set(ref _actionMessage, value); }
    public string Updated { get => _updated; private set => Set(ref _updated, value); }
    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Items.Count == 0;
    public int ActiveSessions => _latest.Count(s => s.Session.ClosedAt is null);
    public int RunningCount => _latest.Sum(s => s.Activity.RunningCalls);
    public int QueuedCount => _latest.Sum(s => s.Activity.QueuedCalls);
    public string Summary => $"{ActiveSessions} open sessions · {RunningCount} active calls · {QueuedCount} queued";
    public string EmptyMessage => !_connected() ? "Connect the agent, then open a session from each chat. No workspace is required."
        : !string.IsNullOrWhiteSpace(Search) ? "No sessions match this filter. Clear the search to see other sessions."
        : _latest.Count > 0 && !ShowClosed ? "No active sessions. Enable Show closed sessions to review previous sessions."
        : "No sessions yet. Call session__open in each chat; use workspace__set whenever you need a working folder.";
    public RelayCommand RefreshCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand CloseCommand { get; }

    public SessionsViewModel(Func<IReadOnlyList<LocalSessionOverview>> query, Func<bool> connected,
        Action<AgentSessionIdentity, bool> stop, Func<SessionRowViewModel, bool>? confirmClose = null)
    {
        _query = query; _connected = connected; _stop = stop; _confirmClose = confirmClose ?? (_ => false);
        RefreshCommand = new(Refresh);
        StopCommand = new(() => StopSelected(false), CanStop);
        CloseCommand = new(() => StopSelected(true), CanStop);
    }
    private bool CanStop() => _connected() && Selected is { IsClosed: false };
    public void ReportWarning(string text) => Error = text;
    public void Refresh()
    {
        try
        {
            _latest = _query();
            var retained = _latest.Select(s => s.Identity).ToHashSet();
            foreach (var obsolete in _rows.Keys.Where(id => !retained.Contains(id)).ToArray()) _rows.Remove(obsolete);
            foreach (var entry in _latest)
                if (_rows.TryGetValue(entry.Identity, out var row)) row.Update(entry);
                else _rows[entry.Identity] = new(entry);
            UpdateVisible();
            Updated = _connected() ? "Updated " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "Disconnected · no live activity";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        { Error = "Session metadata could not be refreshed. Reconnect or refresh to retry."; }
        StopCommand.Refresh(); CloseCommand.Refresh();
    }
    private void UpdateVisible()
    {
        var search = Search.Trim();
        var visible = _latest.Where(s => ShowClosed || s.Session.ClosedAt is null)
            .Select(s => _rows[s.Identity]).Where(row => search.Length == 0 ||
                row.Label.Contains(search, StringComparison.OrdinalIgnoreCase) || row.Workspace.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.SessionId.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        for (var i = Items.Count - 1; i >= 0; i--) if (!visible.Contains(Items[i])) Items.RemoveAt(i);
        for (var i = 0; i < visible.Length; i++)
        {
            var at = Items.IndexOf(visible[i]);
            if (at < 0) Items.Insert(i, visible[i]); else if (at != i) Items.Move(at, i);
        }
        if (Selected is not null && !Items.Contains(Selected)) Selected = null;
        Changed(nameof(IsEmpty)); Changed(nameof(EmptyMessage)); Changed(nameof(ActiveSessions));
        Changed(nameof(RunningCount)); Changed(nameof(QueuedCount)); Changed(nameof(Summary));
    }
    private void StopSelected(bool close)
    {
        if (Selected is not { } row || !CanStop()) return;
        if (close && !_confirmClose(row)) return;
        try
        {
            Error = ""; _stop(row.Identity, close);
            ActionMessage = $"{(close ? "Close" : "Stop")} requested for {row.Label}. Other sessions were not paused.";
            Refresh();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        { Error = "This session could not be stopped. Refresh its state before retrying."; }
    }
}
