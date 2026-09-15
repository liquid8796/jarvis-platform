using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using Jarvis.Agent.Core;
using Jarvis.Agent.Desktop.Infrastructure;
using Jarvis.Protocol;

namespace Jarvis.Agent.Desktop.ViewModels;

public sealed class ToolPermissionItem(ToolDescriptor descriptor) : INotifyPropertyChanged
{
    private bool _fullPermission;
    public string Id => descriptor.Id;
    public string Name => descriptor.Name;
    public string Category => descriptor.Category;
    public string Description => descriptor.Description;
    public string DefaultBehavior => descriptor.ReadOnly && !descriptor.Sensitive ? "Default: read-only, no prompt" : "Default: ask before each action";
    public bool FullPermission
    {
        get => _fullPermission;
        set
        {
            if (_fullPermission == value) return;
            _fullPermission = value;
            PropertyChanged?.Invoke(this, new(nameof(FullPermission)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Edits a draft selection; only Save persists and activates standing local consent.</summary>
public sealed class ToolPermissionsViewModel : INotifyPropertyChanged
{
    private readonly ToolPermissionPolicy _policy;
    private readonly ToolPermissionStore _store;
    private string _search = "", _status = "", _error = "";
    public ObservableCollection<ToolPermissionItem> Items { get; }
    public ICollectionView FilteredTools { get; }
    public string Search
    {
        get => _search;
        set { _search = value; FilteredTools.Refresh(); Changed(); }
    }
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string Error { get => _error; private set { _error = value; Changed(); } }
    public string SelectionSummary => $"{Items.Count(i => i.FullPermission)} of {Items.Count} tools selected";
    public string SavedSummary => $"{_policy.FullPermissionTools.Count} tools preapproved";
    public bool HasChanges => !Items.Where(i => i.FullPermission).Select(i => i.Id).ToHashSet(StringComparer.Ordinal)
        .SetEquals(_policy.FullPermissionTools);
    public ICommand SelectAllCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public ToolPermissionsViewModel(IEnumerable<ToolDescriptor> descriptors, ToolPermissionPolicy policy, ToolPermissionStore store)
    {
        _policy = policy; _store = store;
        Items = new(descriptors.OrderBy(t => t.Category).ThenBy(t => t.Name).Select(t => new ToolPermissionItem(t)));
        FilteredTools = CollectionViewSource.GetDefaultView(Items);
        FilteredTools.Filter = item => item is ToolPermissionItem tool &&
            (tool.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             tool.Category.Contains(Search, StringComparison.OrdinalIgnoreCase) || tool.Id.Contains(Search, StringComparison.OrdinalIgnoreCase));
        foreach (var row in Items) row.PropertyChanged += (_, _) => SelectionChanged();
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        ClearAllCommand = new RelayCommand(() => SetAll(false));
        ResetCommand = new RelayCommand(Reset);
        SaveCommand = new RelayCommand(Save);
        try
        {
            var installed = Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            _policy.Replace(_store.Load().Where(installed.Contains));
            Reset();
            Status = "Changes take effect only after Save permissions.";
        }
        catch (Exception ex)
        {
            _policy.Replace([]);
            Error = "Permissions were not loaded; no tools were preapproved. " + ex.Message;
        }
    }
    private void SetAll(bool value)
    {
        // Deliberately the entire installed inventory, not just the current search results.
        foreach (var item in Items) item.FullPermission = value;
        Status = "Selection changed. Save permissions to apply.";
    }
    private void Reset()
    {
        foreach (var item in Items) item.FullPermission = _policy.HasFullPermission(item.Id);
        Status = "Restored the active permission selection."; Error = "";
        SelectionChanged();
    }
    private void Save()
    {
        try
        {
            var selected = Items.Where(i => i.FullPermission).Select(i => i.Id).ToArray();
            // Persist first. A failed write must not leave an unsaved permission active in memory.
            _store.Save(selected);
            _policy.Replace(selected);
            Error = "";
            Status = "Saved. Selected tools run without another Jarvis permission prompt while control is armed.";
            Changed(nameof(SavedSummary)); SelectionChanged();
        }
        catch (Exception ex) { Error = "Could not save tool permissions: " + ex.Message; }
    }
    private void SelectionChanged() { Changed(nameof(SelectionSummary)); Changed(nameof(HasChanges)); }
    private void Changed([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
}
