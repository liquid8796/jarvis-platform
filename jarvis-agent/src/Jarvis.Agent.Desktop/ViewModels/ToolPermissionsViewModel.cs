using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using Jarvis.Agent.Core;
using Jarvis.Agent.Desktop.Infrastructure;
using Jarvis.Protocol;

namespace Jarvis.Agent.Desktop.ViewModels;

public sealed class ToolPermissionItem : INotifyPropertyChanged
{
    private readonly ToolDescriptor _descriptor;
    private bool _fullPermission;
    private bool _alwaysApproved;

    public ToolPermissionItem(ToolDescriptor descriptor, Action<ToolPermissionItem> revokeAlwaysApproval)
    {
        _descriptor = descriptor;
        RevokeAlwaysApprovalCommand = new RelayCommand(() => revokeAlwaysApproval(this));
    }

    public string Id => _descriptor.Id;
    public string Name => _descriptor.Name;
    public string Category => _descriptor.Category;
    public string Description => _descriptor.Description;
    public string DefaultBehavior => _descriptor.ReadOnly && !_descriptor.Sensitive ? "Default: read-only, no prompt" : "Default: ask before each action";
    public bool SupportsAlwaysApproval => ToolPermissionPolicy.SupportsPermanentApproval(Id);
    public string AlwaysApprovalStatus => AlwaysApproved ? "Always approved" : "";
    public ICommand RevokeAlwaysApprovalCommand { get; }

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

    public bool AlwaysApproved
    {
        get => _alwaysApproved;
        set
        {
            if (_alwaysApproved == value) return;
            _alwaysApproved = value;
            PropertyChanged?.Invoke(this, new(nameof(AlwaysApproved)));
            PropertyChanged?.Invoke(this, new(nameof(AlwaysApprovalStatus)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Edits a draft selection; only Save persists and activates standing local consent.</summary>
public sealed class ToolPermissionsViewModel : INotifyPropertyChanged
{
    private readonly ToolPermissionPolicy _policy;
    private readonly ToolPermissionStore _store;
    private ToolPermissionSettings _savedSettings = new([], []);
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
    public string SavedSummary => $"{_policy.FullPermissionTools.Count} tools preapproved · {_policy.AlwaysApprovedConstrainedTools.Count} process tools always approved";
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
        Items = new(descriptors.OrderBy(t => t.Category).ThenBy(t => t.Name).Select(t => new ToolPermissionItem(t, RevokeAlwaysApproval)));
        FilteredTools = CollectionViewSource.GetDefaultView(Items);
        FilteredTools.Filter = item => item is ToolPermissionItem tool &&
            (tool.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             tool.Category.Contains(Search, StringComparison.OrdinalIgnoreCase) || tool.Id.Contains(Search, StringComparison.OrdinalIgnoreCase));
        foreach (var row in Items) row.PropertyChanged += ItemPropertyChanged;
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        ClearAllCommand = new RelayCommand(() => SetAll(false));
        ResetCommand = new RelayCommand(Reset);
        SaveCommand = new RelayCommand(Save);
        try
        {
            var installed = Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            var settings = _store.LoadSettings();
            _savedSettings = settings;
            _policy.Replace(settings.FullPermissionTools.Where(installed.Contains));
            _policy.ReplaceAlwaysApprovedConstrainedTools(settings.AlwaysApprovedConstrainedTools.Where(installed.Contains));
            Reset();
            Status = "Changes take effect only after Save permissions. Always-approved process tools can be revoked immediately below.";
        }
        catch (Exception ex)
        {
            _policy.Replace([]);
            _policy.ReplaceAlwaysApprovedConstrainedTools([]);
            _savedSettings = new([], []);
            Error = "Permissions were not loaded; no tools were preapproved. " + ex.Message;
        }
        _policy.PermissionsChanged += RefreshPermissionIndicators;
    }
    public void ReplaceDescriptors(IEnumerable<ToolDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var next = descriptors.DistinctBy(tool => tool.Id).OrderBy(tool => tool.Category)
            .ThenBy(tool => tool.Name).ToArray();
        var draft = Items.ToDictionary(item => item.Id, item => item.FullPermission, StringComparer.Ordinal);
        var installed = next.Select(tool => tool.Id).ToHashSet(StringComparer.Ordinal);

        _policy.Replace(_savedSettings.FullPermissionTools.Where(installed.Contains));
        _policy.ReplaceAlwaysApprovedConstrainedTools(
            _savedSettings.AlwaysApprovedConstrainedTools.Where(installed.Contains));

        foreach (var item in Items) item.PropertyChanged -= ItemPropertyChanged;
        Items.Clear();
        foreach (var descriptor in next)
        {
            var item = new ToolPermissionItem(descriptor, RevokeAlwaysApproval)
            {
                FullPermission = draft.TryGetValue(descriptor.Id, out var selected)
                    ? selected
                    : _policy.HasFullPermission(descriptor.Id),
                AlwaysApproved = _policy.HasAlwaysApprovedConstrainedTool(descriptor.Id)
            };
            item.PropertyChanged += ItemPropertyChanged;
            Items.Add(item);
        }

        FilteredTools.Refresh();
        Status = $"Tool catalog refreshed. {Items.Count} tools available; unsaved choices for retained tools were preserved.";
        Error = "";
        Changed(nameof(SavedSummary));
        SelectionChanged();
    }
    private void SetAll(bool value)
    {
        // Deliberately the entire installed inventory, not just the current search results.
        foreach (var item in Items) item.FullPermission = value;
        Status = "Selection changed. Save permissions to apply.";
    }
    private void Reset()
    {
        foreach (var item in Items)
        {
            item.FullPermission = _policy.HasFullPermission(item.Id);
            item.AlwaysApproved = _policy.HasAlwaysApprovedConstrainedTool(item.Id);
        }
        Status = "Restored the active permission selection."; Error = "";
        SelectionChanged();
    }
    private void Save()
    {
        try
        {
            var selected = Items.Where(i => i.FullPermission).Select(i => i.Id).ToArray();
            var settings = new ToolPermissionSettings(selected, _policy.AlwaysApprovedConstrainedTools);
            // Persist first. A failed write must not leave an unsaved permission active in memory.
            _store.Save(settings);
            _savedSettings = settings;
            _policy.Replace(selected);
            Error = "";
            Status = "Saved. Selected tools run without another Jarvis permission prompt while control is armed. Constrained process tools still require approval unless separately marked Always approved.";
            Changed(nameof(SavedSummary)); SelectionChanged();
        }
        catch (Exception ex) { Error = "Could not save tool permissions: " + ex.Message; }
    }
    private void RevokeAlwaysApproval(ToolPermissionItem item)
    {
        if (!item.SupportsAlwaysApproval || !_policy.HasAlwaysApprovedConstrainedTool(item.Id)) return;
        try
        {
            var next = _savedSettings.AlwaysApprovedConstrainedTools
                .Where(id => !StringComparer.Ordinal.Equals(id, item.Id)).ToArray();
            var settings = new ToolPermissionSettings(_savedSettings.FullPermissionTools, next);
            _store.Save(settings);
            _savedSettings = settings;
            _policy.RevokeAlwaysApprovedConstrainedTool(item.Id);
            item.AlwaysApproved = false;
            Error = "";
            Status = $"{item.Name} will require approval again.";
            Changed(nameof(SavedSummary)); SelectionChanged();
        }
        catch (Exception ex) { Error = "Could not revoke permanent approval: " + ex.Message; }
    }
    private void RefreshPermissionIndicators()
    {
        foreach (var item in Items) item.AlwaysApproved = _policy.HasAlwaysApprovedConstrainedTool(item.Id);
        Changed(nameof(SavedSummary));
    }
    private void ItemPropertyChanged(object? sender, PropertyChangedEventArgs args) => SelectionChanged();
    private void SelectionChanged() { Changed(nameof(SelectionSummary)); Changed(nameof(HasChanges)); }
    private void Changed([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
}
