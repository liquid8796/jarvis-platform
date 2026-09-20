using System.Collections.ObjectModel;
using Jarvis.Agent.Core.Prompts;
using Jarvis.Agent.Desktop.Infrastructure;
using Jarvis.Protocol;

namespace Jarvis.Agent.Desktop.ViewModels;

public sealed class PromptEntryViewModel : ObservableViewModel
{
    private readonly Action _changed;
    private string _title, _text;
    private bool _enabled;
    public string Id { get; }
    public string Title { get => _title; set { if (Set(ref _title, value ?? "")) _changed(); } }
    public string Text { get => _text; set { if (Set(ref _text, value ?? "")) _changed(); } }
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) _changed(); } }
    public PromptEntryViewModel(PromptInjectionEntry entry, Action changed)
    { Id = entry.Id; _title = entry.Title; _text = entry.Text; _enabled = entry.Enabled; _changed = changed; }
    public PromptInjectionEntry Snapshot() => new(Id, Title.Trim(), Text.Trim(), Enabled);
}

public sealed class PromptInjectionViewModel : ObservableViewModel
{
    private readonly PromptInjectionStore _store;
    private readonly Action<PromptInjectionSettings> _apply;
    private readonly Func<string, bool> _confirm;
    private PromptInjectionSettings _saved = new();
    private PromptEntryViewModel? _selected;
    private bool _enabled, _dirty, _loading, _loaded;
    private string _error = "", _status = "", _runtimeStatus = "Not connected. Changes can be saved locally.";
    public ObservableCollection<PromptEntryViewModel> Items { get; } = [];
    public PromptEntryViewModel? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { Changed(nameof(HasSelection)); DeleteCommand.Refresh(); } }
    }
    public bool HasSelection => Selected is not null;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) DraftChanged(); } }
    public bool IsDirty => _dirty;
    public bool IsLoaded => _loaded;
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string RuntimeStatus { get => _runtimeStatus; private set => Set(ref _runtimeStatus, value); }
    public string Summary => $"{Items.Count} prompts · {Items.Count(p => p.Enabled)} enabled · {(_dirty ? "unsaved changes" : "saved")}";
    public string SavedSummary => !_loaded ? "No valid settings loaded; no context will be sent."
        : !_saved.Enabled ? $"Saved revision {_saved.Revision}: injection off"
        : $"Saved revision {_saved.Revision}: {_saved.Entries.Count(p => p.Enabled)} prompts enabled";
    public string Preview
    {
        get
        {
            if (!Enabled) return "Injection is off. No prompt context will be sent after saving this draft.";
            try { return Draft().CreateContext()?.ToContextText() ?? "No prompts are enabled. No context will be sent."; }
            catch (ArgumentException ex) { return "Complete the draft before sending: " + ex.Message; }
        }
    }
    public RelayCommand AddCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }

    public PromptInjectionViewModel(PromptInjectionStore store, Action<PromptInjectionSettings>? apply = null,
        Func<string, bool>? confirm = null)
    {
        _store = store; _apply = apply ?? (_ => { }); _confirm = confirm ?? (_ => false);
        AddCommand = new(Add, () => _loaded && Items.Count < UserPromptContext.MaxPrompts);
        DeleteCommand = new(Delete, () => _loaded && Selected is not null);
        SaveCommand = new(Save, () => _loaded && _dirty);
        ReloadCommand = new(Reload);
        RestoreDefaultsCommand = new(RestoreDefaults, () => _loaded);
        Reload();
    }

    public void UpdateRuntimeState(bool connected, bool supportsContext)
    {
        RuntimeStatus = !connected ? "Not connected. Saved prompts apply when the agent connects."
            : !supportsContext ? "Server does not support prompt context. Upgrade the MCP server and reconnect; nothing is being sent."
            : "Connected: saved, enabled prompts accompany subsequent successful MCP replies. Existing chat history is not changed.";
    }

    private PromptInjectionSettings Draft() => new()
    { Revision = _saved.Revision, Enabled = Enabled, Entries = Items.Select(row => row.Snapshot()).ToArray() };

    private void DraftChanged()
    {
        if (_loading) return;
        _dirty = true; Error = "";
        Status = "Draft only. Click Save changes to apply additions, edits, deletions and toggles.";
        RefreshState();
    }

    private void RefreshState()
    {
        Changed(nameof(IsDirty)); Changed(nameof(IsLoaded)); Changed(nameof(Summary));
        Changed(nameof(SavedSummary)); Changed(nameof(Preview)); Changed(nameof(HasSelection));
        AddCommand.Refresh(); DeleteCommand.Refresh(); SaveCommand.Refresh(); RestoreDefaultsCommand.Refresh();
    }

    private void Populate(PromptInjectionSettings settings)
    {
        _loading = true;
        try
        {
            Selected = null;
            Items.Clear();
            foreach (var entry in settings.Entries) Items.Add(new(entry, DraftChanged));
            Enabled = settings.Enabled;
            Selected = Items.FirstOrDefault();
        }
        finally { _loading = false; }
    }

    private void Reload()
    {
        if (_dirty && !_confirm("Discard unsaved prompt changes and reload the saved settings?")) return;
        try
        {
            var settings = _store.Load();
            _saved = settings; _loaded = true; _dirty = false;
            Populate(settings);
            Error = ""; Status = "Loaded locally. All built-in examples start disabled.";
            ApplySaved();
        }
        catch (Exception ex) when (IsSettingsError(ex)) { Error = ex.Message; }
        RefreshState();
    }

    private void Add()
    {
        var row = new PromptEntryViewModel(new(Guid.NewGuid().ToString("N"), "New prompt", ""), DraftChanged);
        Items.Add(row); Selected = row; DraftChanged();
    }

    private void Delete()
    {
        if (Selected is not { } row || !_confirm($"Delete '{row.Title}' from the draft? Save changes to make this permanent.")) return;
        var at = Items.IndexOf(row);
        Items.Remove(row);
        Selected = Items.Count == 0 ? null : Items[Math.Min(at, Items.Count - 1)];
        DraftChanged();
    }

    private void RestoreDefaults()
    {
        if (!_confirm("Replace this draft with the seven disabled default prompts? Custom prompts will be removed only after Save changes.")) return;
        Populate(DefaultPromptPresets.Create()); DraftChanged();
    }

    private void Save()
    {
        try
        {
            var selectedId = Selected?.Id;
            _saved = _store.Save(Draft(), _saved.Revision);
            _dirty = false; Error = "";
            Populate(_saved);
            Selected = Items.FirstOrDefault(row => row.Id == selectedId) ?? Items.FirstOrDefault();
            Status = "Saved locally. Disabled or deleted prompts will not be attached to subsequent replies.";
            ApplySaved();
        }
        catch (Exception ex) when (IsSettingsError(ex)) { Error = ex.Message; }
        RefreshState();
    }

    private void ApplySaved()
    {
        try { _apply(_saved); }
        catch (Exception ex) when (IsSettingsError(ex))
        { Error = "Saved settings could not be applied to the running agent. Reconnect to apply them. " + ex.Message; }
    }

    private static bool IsSettingsError(Exception ex) => ex is ArgumentException or InvalidOperationException
        or System.IO.IOException or UnauthorizedAccessException or OverflowException;
}
