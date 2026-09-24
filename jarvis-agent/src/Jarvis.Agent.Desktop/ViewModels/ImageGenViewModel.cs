using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Jarvis.Agent.Core.ImageGeneration;
using Jarvis.Agent.Desktop.Infrastructure;
using Jarvis.Agent.Windows;
using Jarvis.Agent.Windows.ImageGeneration;
using Jarvis.Protocol;
using JarvisCode.App.Services;

namespace Jarvis.Agent.Desktop.ViewModels;

public sealed record ImageGenBrowserChoice(string InstanceId, string Family, string Label);

/// <summary>Local operator settings only; never enables remote tool permissions or grants access to unrelated tabs.</summary>
public sealed class ImageGenViewModel : INotifyPropertyChanged
{
    private readonly ImageGenBrowserBindingStore _store;
    private readonly Func<IBrowserRuntimeClient?> _runtime;
    private ImageBrowserBinding? _binding;
    private ImageGenBrowserChoice? _selected;
    private string _status = "Refresh to see connected Chrome/Edge extension instances.", _error = "", _saved = "Not selected";
    public ObservableCollection<ImageGenBrowserChoice> Browsers { get; } = [];
    public ImageGenBrowserChoice? SelectedBrowser { get => _selected; set => Set(ref _selected, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string SavedBrowser { get => _saved; private set => Set(ref _saved, value); }
    public ICommand RefreshCommand { get; }
    public ICommand BindCommand { get; }
    public ICommand ClearCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ImageGenViewModel(string settingsRoot, Func<IBrowserRuntimeClient?> runtime)
    {
        _store = new(settingsRoot); _runtime = runtime;
        RefreshCommand = new AsyncCommand(RefreshAsync, Fail);
        BindCommand = new RelayCommand(Bind);
        ClearCommand = new RelayCommand(Clear);
        try { LoadBinding(); } catch (Exception ex) { Fail(ex); }
    }
    private void LoadBinding()
    {
        _binding = _store.Read();
        SavedBrowser = _binding is null ? "Not selected" : $"{_binding.BrowserFamily} · {_binding.ExtensionInstanceId}";
    }
    public async Task RefreshAsync()
    {
        Error = "";
        LoadBinding();
        Browsers.Clear(); SelectedBrowser = null;
        var runtime = _runtime();
        if (runtime is null)
        {
            Status = "Connect Jarvis Agent in Connection center, then refresh. No browser will be launched.";
            return;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await runtime.ExecuteAsync(new BrowserRuntimeRequest("imagegen.connections", WireJson.Element(new { }),
            new BrowserRuntimeContext(Guid.NewGuid().ToString("N"), null, "local-imagegen-settings", "", [], false, "extension")), timeout.Token);
        if (response.IsError) throw new InvalidOperationException(response.Text);
        var body = JsonNode.Parse(response.Text)?.AsObject() ?? throw new InvalidOperationException("Browser discovery returned no state.");
        var connections = body["connections"]?.Deserialize<BrowserConnectionInfo[]>(WireJson.Options) ?? [];
        foreach (var connection in connections.Where(c => c.Ready && c.ExtensionInstanceId is not null && c.Name is "Chrome" or "Edge"))
        {
            var choice = new ImageGenBrowserChoice(connection.ExtensionInstanceId!, connection.Name.ToLowerInvariant(),
                $"{connection.Name} · {connection.ExtensionInstanceId}");
            Browsers.Add(choice);
            if (choice.InstanceId == _binding?.ExtensionInstanceId) SelectedBrowser = choice;
        }
        Status = Browsers.Count == 0 ? "No compatible extension is connected. Reload Jarvis Agent Browser 1.4.0+ in your existing Chrome/Edge profile and allow its Downloads permission."
            : _binding is null ? "Choose a browser below or click Use this browser in the Jarvis extension. New image tabs will stay in that browser."
            : SelectedBrowser is null ? "The saved browser is disconnected. Open the same profile; Jarvis will not select a replacement automatically."
            : "The selected browser is connected. ImageGen tools still require normal local approval and Arm control.";
    }
    private void Bind()
    {
        try
        {
            Error = "";
            var selected = SelectedBrowser ?? throw new InvalidOperationException("Select a connected Chrome or Edge profile first.");
            if (!Browsers.Contains(selected)) throw new InvalidOperationException("Refresh the browser list before saving.");
            _binding = _store.Save(selected.InstanceId, selected.Family, _binding?.Revision);
            LoadBinding();
            Status = "Browser selected. Existing jobs are never moved to another profile. This does not enable tool permissions or submit a prompt.";
        }
        catch (Exception ex) { Fail(ex); }
    }
    private void Clear()
    {
        try
        {
            Error = ""; _store.Clear(_binding?.Revision); LoadBinding();
            Status = "Browser binding removed. ImageGen stops at its next checked step. Existing browser tabs, conversations and downloaded originals were not deleted.";
        }
        catch (Exception ex) { Fail(ex); }
    }
    private void Fail(Exception ex) => Error = ex.Message;
    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new(name)); }
}
