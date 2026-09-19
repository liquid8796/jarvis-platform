using System.Collections.ObjectModel;
using System.Globalization;
using Jarvis.Agent.Desktop.Infrastructure;
using Jarvis.Protocol;

namespace Jarvis.Agent.Desktop.ViewModels;

public sealed class SessionRowViewModel : ObservableViewModel
{
    private LocalSessionOverview _value;
    private readonly Action? _bulkSelectionChanged;
    private bool _isBulkSelected;
    public SessionRowViewModel(LocalSessionOverview value, Action? bulkSelectionChanged = null)
    {
        _value = value;
        _bulkSelectionChanged = bulkSelectionChanged;
    }
    public AgentSessionIdentity Identity => _value.Identity;
    public string SessionId => _value.Session.SessionId;
    public string Label => _value.Session.Label;
    public string DeviceId => _value.Session.DeviceId;
    public string Workspace => string.IsNullOrWhiteSpace(_value.Session.Workspace) ? "No workspace selected" : _value.Session.Workspace;
    public string WorkspaceRevision => $"Workspace revision {_value.Session.WorkspaceRevision}";
    public string Parent => _value.Session.ParentSessionId ?? "No parent session";
    public string Status => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(_value.Activity.State(_value.Session.ClosedAt));
    public bool IsClosed => _value.Session.ClosedAt is not null;
    public bool CanBulkSelect => !IsClosed;
    public bool IsBulkSelected
    {
        get => _isBulkSelected;
        set
        {
            if (IsClosed && value) return;
            if (Set(ref _isBulkSelected, value)) _bulkSelectionChanged?.Invoke();
        }
    }
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
    public void Update(LocalSessionOverview value)
    {
        _value = value;
        if (IsClosed && _isBulkSelected)
        {
            _isBulkSelected = false;
            Changed(nameof(IsBulkSelected));
            _bulkSelectionChanged?.Invoke();
        }
        Changed(null);
    }
}

/// <summary>Operator-only projection. No session handles or transcript content are exposed in this view.</summary>
public sealed class SessionsViewModel : ObservableViewModel
{
    private readonly Func<IReadOnlyList<LocalSessionOverview>> _query;
    private readonly Func<bool> _connected;
    private readonly Action<AgentSessionIdentity, bool> _stop;
    private readonly Func<IReadOnlyList<SessionRowViewModel>, bool> _confirmClose;
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
    public int BulkSelectedCount => Items.Count(row => row.IsBulkSelected && !row.IsClosed);
    public string BulkSelectionSummary => $"{BulkSelectedCount} selected";
    public string Summary => $"{ActiveSessions} open sessions · {RunningCount} active calls · {QueuedCount} queued";
    public string EmptyMessage => !_connected() ? "Connect the agent, then open a session from each chat. No workspace is required."
        : !string.IsNullOrWhiteSpace(Search) ? "No sessions match this filter. Clear the search to see other sessions."
        : _latest.Count > 0 && !ShowClosed ? "No active sessions. Enable Show closed sessions to review previous sessions."
        : "No sessions yet. Call session__open in each chat; use workspace__set whenever you need a working folder.";
    public RelayCommand RefreshCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand StopSelectedSessionsCommand { get; }
    public RelayCommand CloseSelectedSessionsCommand { get; }

    public SessionsViewModel(Func<IReadOnlyList<LocalSessionOverview>> query, Func<bool> connected,
        Action<AgentSessionIdentity, bool> stop, Func<IReadOnlyList<SessionRowViewModel>, bool>? confirmClose = null)
    {
        _query = query; _connected = connected; _stop = stop; _confirmClose = confirmClose ?? (_ => false);
        RefreshCommand = new(Refresh);
        StopCommand = new(() => StopSelected(false), CanStop);
        CloseCommand = new(() => StopSelected(true), CanStop);
        SelectAllCommand = new(SelectAllVisible, CanSelectAll);
        ClearSelectionCommand = new(ClearBulkSelection, CanClearSelection);
        StopSelectedSessionsCommand = new(() => StopBulkSelected(false), CanBulkAction);
        CloseSelectedSessionsCommand = new(() => StopBulkSelected(true), CanBulkAction);
    }
    private bool CanStop() => _connected() && Selected is { IsClosed: false };
    private bool CanSelectAll() => _connected() && Items.Any(row => row.CanBulkSelect && !row.IsBulkSelected);
    private bool CanClearSelection() => _rows.Values.Any(row => row.IsBulkSelected);
    private bool CanBulkAction() => _connected() && BulkSelectedRows().Length > 0;
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
                else _rows[entry.Identity] = new(entry, RefreshBulkSelectionState);
            UpdateVisible();
            Updated = _connected() ? "Updated " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "Disconnected · no live activity";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        { Error = "Session metadata could not be refreshed. Reconnect or refresh to retry."; }
        StopCommand.Refresh(); CloseCommand.Refresh(); RefreshBulkSelectionState();
    }
    private void UpdateVisible()
    {
        var search = Search.Trim();
        var visible = _latest.Where(s => ShowClosed || s.Session.ClosedAt is null)
            .Select(s => _rows[s.Identity]).Where(row => search.Length == 0 ||
                row.Label.Contains(search, StringComparison.OrdinalIgnoreCase) || row.Workspace.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.SessionId.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (visible.Contains(Items[i])) continue;
            Items[i].IsBulkSelected = false;
            Items.RemoveAt(i);
        }
        for (var i = 0; i < visible.Length; i++)
        {
            var at = Items.IndexOf(visible[i]);
            if (at < 0) Items.Insert(i, visible[i]); else if (at != i) Items.Move(at, i);
        }
        if (Selected is not null && !Items.Contains(Selected)) Selected = null;
        Changed(nameof(IsEmpty)); Changed(nameof(EmptyMessage)); Changed(nameof(ActiveSessions));
        Changed(nameof(RunningCount)); Changed(nameof(QueuedCount)); Changed(nameof(Summary));
        RefreshBulkSelectionState();
    }
    private void SelectAllVisible()
    {
        foreach (var row in Items.Where(row => row.CanBulkSelect)) row.IsBulkSelected = true;
        RefreshBulkSelectionState();
    }
    private void ClearBulkSelection()
    {
        foreach (var row in _rows.Values.Where(row => row.IsBulkSelected).ToArray()) row.IsBulkSelected = false;
        RefreshBulkSelectionState();
    }
    private SessionRowViewModel[] BulkSelectedRows() => Items.Where(row => row.IsBulkSelected && !row.IsClosed).ToArray();
    private void RefreshBulkSelectionState()
    {
        Changed(nameof(BulkSelectedCount));
        Changed(nameof(BulkSelectionSummary));
        SelectAllCommand.Refresh();
        ClearSelectionCommand.Refresh();
        StopSelectedSessionsCommand.Refresh();
        CloseSelectedSessionsCommand.Refresh();
    }
    private void StopSelected(bool close)
    {
        if (Selected is not { } row || !CanStop()) return;
        StopRows([row], close, row.Label);
    }
    private void StopBulkSelected(bool close)
    {
        var rows = BulkSelectedRows();
        if (rows.Length == 0 || !_connected()) return;
        StopRows(rows, close, null);
    }
    private void StopRows(IReadOnlyList<SessionRowViewModel> rows, bool close, string? singleLabel)
    {
        if (close && !_confirmClose(rows)) return;
        Error = "";
        var requested = 0;
        var failed = 0;
        foreach (var row in rows)
        {
            try
            {
                _stop(row.Identity, close);
                requested++;
                row.IsBulkSelected = false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }
        if (requested > 0)
        {
            ActionMessage = singleLabel is not null
                ? $"{(close ? "Close" : "Stop")} requested for {singleLabel}. Other sessions were not paused."
                : $"{(close ? "Close" : "Stop")} requested for {requested} selected {(requested == 1 ? "session" : "sessions")}. Other sessions were not paused.";
        }
        if (failed > 0)
            Error = singleLabel is not null
                ? $"This session could not be {(close ? "closed" : "stopped")}. Refresh its state before retrying."
                : $"{failed} selected {(failed == 1 ? "session" : "sessions")} could not be {(close ? "closed" : "stopped")}. Refresh state before retrying.";
        Refresh();
    }
}
