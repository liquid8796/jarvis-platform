using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Jarvis.Agent.Core.Execution;
using Jarvis.Agent.Desktop.Infrastructure;
using Jarvis.Protocol;

namespace Jarvis.Agent.Desktop.ViewModels;

public sealed class NumericSettingViewModel : ObservableViewModel, INotifyDataErrorInfo
{
    private string _text;
    private string _error = "";
    public string Label { get; }
    public string Hint { get; }
    public int Minimum { get; }
    public int Maximum { get; }
    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? "")) return;
            Validate(); Changed(nameof(Number));
        }
    }
    public int? Number => int.TryParse(_text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= Minimum && n <= Maximum ? n : null;
    public string Error => _error;
    public bool HasErrors => _error.Length != 0;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public IEnumerable GetErrors(string? propertyName) => HasErrors && (string.IsNullOrEmpty(propertyName) || propertyName == nameof(Text)) ? new[] { _error } : Array.Empty<string>();

    public NumericSettingViewModel(string label, string hint, int minimum, int maximum, int value)
    {
        Label = label; Hint = hint; Minimum = minimum; Maximum = maximum;
        _text = value.ToString(CultureInfo.InvariantCulture); Validate();
    }
    private void Validate()
    {
        var next = Number is not null ? "" : Maximum == int.MaxValue
            ? "Enter a whole number greater than zero." : $"Enter a whole number from {Minimum} to {Maximum}.";
        if (_error == next) return;
        _error = next; Changed(nameof(Error)); Changed(nameof(HasErrors)); ErrorsChanged?.Invoke(this, new(nameof(Text)));
    }
}

/// <summary>Local settings draft. Persistence, execution and server acknowledgement are separate visible states.</summary>
public sealed class ExecutionSettingsViewModel : ObservableViewModel
{
    private readonly ExecutionSettingsStore _store;
    private readonly Action<AgentExecutionSettings>? _apply;
    private AgentExecutionSettings _saved = new();
    private bool _needsReload;
    private string _error = "", _status = "Changes apply only after you save.";
    private bool _connected, _serverSupports;
    private long _runtimeRevision, _acknowledgedRevision;
    public NumericSettingViewModel ConcurrentCalls { get; }
    public NumericSettingViewModel ProcessJobs { get; }
    public NumericSettingViewModel DurableTasks { get; }
    public NumericSettingViewModel QueuedCalls { get; }
    public NumericSettingViewModel QueueTimeout { get; }
    public IReadOnlyList<NumericSettingViewModel> AdvancedFields { get; }
    private IEnumerable<NumericSettingViewModel> Fields => new[] { ConcurrentCalls }.Concat(AdvancedFields);
    public AgentExecutionSettings Saved => _saved;
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public bool HasErrors => Fields.Any(f => f.HasErrors);
    public bool IsDirty => Fields.All(f => f.Number is not null) && Draft() != _saved;
    public string DraftSummary => HasErrors ? "Correct the highlighted values before saving." : IsDirty ? "Unsaved changes" : $"Saved revision {_saved.Revision}";
    public string HighLoadWarning => new[] { ConcurrentCalls, ProcessJobs, DurableTasks }.Any(f => f.Number > 32)
        ? "High concurrency can increase memory, CPU and disk pressure. Resource locks still prevent conflicting work; no limit is silently clamped." : "";
    public string SyncSummary => !_connected
        ? $"Saved locally · revision {_saved.Revision}. Server synchronization resumes on connection."
        : _runtimeRevision != _saved.Revision
            ? $"Saved revision {_saved.Revision}; agent revision {_runtimeRevision}. Reload settings or reconnect to reconcile."
            : !_serverSupports
                ? $"Applied locally · revision {_runtimeRevision}. This server has not negotiated live settings synchronization."
                : _acknowledgedRevision < _runtimeRevision
                    ? $"Applied locally · revision {_runtimeRevision}. Server acknowledgement pending."
                    : $"Applied locally and acknowledged by server · revision {_runtimeRevision}.";

    public ExecutionSettingsViewModel(ExecutionSettingsStore store, Action<AgentExecutionSettings>? apply = null)
    {
        _store = store; _apply = apply;
        ConcurrentCalls = new("Concurrent tool calls", "Execution slots shared by all chats on this agent. This is not a limit on open chats.", 1, int.MaxValue, 5);
        ProcessJobs = new("Background process jobs", "A started process keeps a process slot until it exits, even after the starting request returns.", 1, int.MaxValue, 5);
        DurableTasks = new("Running durable tasks", "Task steps still pass through the same permissions and tool-call scheduler.", 1, int.MaxValue, 5);
        QueuedCalls = new("Queued requests", "A bounded waiting room shared fairly between sessions. Zero means do not queue.", 0, 100000, 100);
        QueueTimeout = new("Queue timeout (seconds)", "Expired waiting work is rejected before its tool starts. The overall request deadline still applies.", 1, 240, 60);
        AdvancedFields = new[] { ProcessJobs, DurableTasks, QueuedCalls, QueueTimeout };
        SaveCommand = new(Save, () => !_needsReload && !HasErrors && IsDirty);
        ReloadCommand = new(Reload);
        foreach (var field in Fields) field.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(NumericSettingViewModel.Text) or nameof(NumericSettingViewModel.Error)) RefreshDraft();
        };
        Reload();
    }
    public void UpdateRuntimeState(bool connected, long runtimeRevision, long acknowledgedRevision, bool serverSupports)
    {
        if ((_connected, _runtimeRevision, _acknowledgedRevision, _serverSupports) == (connected, runtimeRevision, acknowledgedRevision, serverSupports)) return;
        _connected = connected; _runtimeRevision = runtimeRevision; _acknowledgedRevision = acknowledgedRevision; _serverSupports = serverSupports;
        Changed(nameof(SyncSummary));
    }
    private AgentExecutionSettings Draft() => new()
    {
        MaxConcurrentCalls = ConcurrentCalls.Number ?? 0, MaxProcessJobs = ProcessJobs.Number ?? 0,
        MaxDurableTasks = DurableTasks.Number ?? 0, MaxQueuedCalls = QueuedCalls.Number ?? -1,
        QueueTimeoutSeconds = QueueTimeout.Number ?? 0, Revision = _saved.Revision
    };
    private void Save()
    {
        try
        {
            var saved = _store.Save(Draft(), _saved.Revision);
            _saved = saved; Error = ""; Status = "Saved on this computer. Running work is not interrupted by a lower limit.";
            try { _apply?.Invoke(saved); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
            { Error = "Saved locally, but the running agent could not apply this revision. Reconnect to apply the saved limits."; }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            _needsReload = true;
            Error = ex is AgentRequestException ? "Settings changed on disk. Reload before saving again." : "Settings could not be saved. Existing settings were not intentionally overwritten; reload before retrying.";
        }
        RefreshDraft(); Changed(nameof(Saved)); Changed(nameof(SyncSummary));
    }
    private void Reload()
    {
        try
        {
            _saved = _store.Load(); _needsReload = false; Error = "";
            ConcurrentCalls.Text = _saved.MaxConcurrentCalls.ToString(CultureInfo.InvariantCulture);
            ProcessJobs.Text = _saved.MaxProcessJobs.ToString(CultureInfo.InvariantCulture);
            DurableTasks.Text = _saved.MaxDurableTasks.ToString(CultureInfo.InvariantCulture);
            QueuedCalls.Text = _saved.MaxQueuedCalls.ToString(CultureInfo.InvariantCulture);
            QueueTimeout.Text = _saved.QueueTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            Status = "Loaded saved settings. No permissions or active work were changed.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            _needsReload = true;
            Error = "Settings could not be read. Repair the local settings file and reload; it has not been replaced with defaults.";
        }
        RefreshDraft(); Changed(nameof(Saved)); Changed(nameof(SyncSummary));
    }
    private void RefreshDraft()
    {
        Changed(nameof(HasErrors)); Changed(nameof(IsDirty)); Changed(nameof(DraftSummary)); Changed(nameof(HighLoadWarning)); SaveCommand.Refresh();
    }
}
