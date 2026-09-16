using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Threads;
using Jarvis.Protocol;
using Jarvis.Agent.Desktop.Infrastructure;
using Jarvis.Agent.Desktop.Services;
using Jarvis.Agent.Windows;
namespace Jarvis.Agent.Desktop.ViewModels;
public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private AgentRuntime? _runtime;
    private readonly Window _owner;
    private readonly string _settingsRoot;
    private readonly ToolPermissionPolicy _permissions = new();
    private readonly ToolPermissionStore _permissionStore;
    private int _selectedTab;
    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }
    public ToolPermissionsViewModel Permissions { get; }
    private string _server = "https://jarvis.example.com", _device = "", _workspace = "", _status = "Not connected", _control = "Control paused", _error = "";
    private bool _loopback;
    public string ServerUrl { get => _server; set => Set(ref _server, value); }
    public string DeviceId { get => _device; set => Set(ref _device, value); }
    public string Workspace { get => _workspace; set => Set(ref _workspace, value); }
    private string? _selectedDirectory;
    public ObservableCollection<string> AdditionalDirectories { get; } = [];
    public string? SelectedDirectory { get => _selectedDirectory; set => Set(ref _selectedDirectory, value); }
    public bool AllowLoopbackHttp { get => _loopback; set => Set(ref _loopback, value); }
    public string Token { private get; set; } = ""; // PasswordBox supplies this directly; never written to activity/logs.
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Control { get => _control; private set => Set(ref _control, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string ConnectLabel => _runtime is null ? "Save & connect" : "Disconnect";
    public string ToolCount => Tools.Count.ToString();
    public string DesktopVersion => $"Windows desktop · v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "unknown"}";
    public ObservableCollection<AgentEvent> Events { get; } = [];
    public ObservableCollection<string> Tools { get; } = [];
    public ICommand ConnectCommand { get; }
    public ICommand ArmCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand RemoveDirectoryCommand { get; }
    public ICommand MakePrimaryDirectoryCommand { get; }
    public ICommand BrowserCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public SavedProfile? InitialProfile { get; }
    public MainViewModel(Window owner, string? settingsRoot = null, bool loadProfile = true)
    {
        _owner = owner;
        _settingsRoot = settingsRoot ?? AgentProfile.Root;
        _permissionStore = new ToolPermissionStore(System.IO.Path.Combine(_settingsRoot, "tool-permissions.json"));
        try
        {
            var prompts = new LocalPrompts(owner, _permissions, _permissionStore);
            using var inventory = new ToolInventory(prompts, new DesktopArtifactSink(owner), () => owner, _settingsRoot);
            using var processes = new ProcessToolSet();
            using var threads = new ThreadRuntimeToolSet(System.IO.Path.Combine(_settingsRoot, "thread-runtime.db"));
            var descriptors = inventory.Tools.Concat(processes.Tools).Concat(threads.Tools).Select(t => t.Descriptor)
                .Concat(AgentCoreHostTools.Descriptors).DistinctBy(t => t.Id).ToArray();
            foreach (var tool in descriptors) Tools.Add(tool.Name);
            Permissions = new ToolPermissionsViewModel(descriptors, _permissions, _permissionStore);
        }
        catch (Exception ex)
        {
            Permissions = new ToolPermissionsViewModel([], _permissions, _permissionStore);
            Fail(ex);
        }
        ConnectCommand = new AsyncCommand(ToggleConnection, Fail);
        ArmCommand = new RelayCommand(Arm); PauseCommand = new RelayCommand(Pause);
        BrowseCommand = new RelayCommand(() =>
        {
            try
            {
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Select project directories", Multiselect = true };
                if (picker.ShowDialog(owner) != true) return;
                var selected = picker.FolderNames.Select(WorkspaceDirectories.Normalize).ToArray();
                if (string.IsNullOrWhiteSpace(Workspace) && selected.Length > 0) Workspace = selected[0];
                var folders = new WorkspaceDirectories(Workspace, AdditionalDirectories.Concat(selected));
                Workspace = folders.Primary;
                AdditionalDirectories.Clear(); foreach (var folder in folders.Additional) AdditionalDirectories.Add(folder);
                Error = "";
            }
            catch (Exception ex) { Fail(ex); }
        });
        RemoveDirectoryCommand = new RelayCommand(() =>
        {
            if (SelectedDirectory is { } directory) AdditionalDirectories.Remove(directory);
            SelectedDirectory = AdditionalDirectories.FirstOrDefault();
        });
        MakePrimaryDirectoryCommand = new RelayCommand(() =>
        {
            if (SelectedDirectory is not { } directory) return;
            AdditionalDirectories.Remove(directory);
            var previous = Workspace; Workspace = directory;
            if (!string.IsNullOrWhiteSpace(previous) && !AdditionalDirectories.Contains(previous, StringComparer.OrdinalIgnoreCase))
                AdditionalDirectories.Insert(0, previous);
            SelectedDirectory = AdditionalDirectories.FirstOrDefault();
        });
        BrowserCommand = new RelayCommand(() =>
        {
            try
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Title = "Select published jarvis-agent.exe", Filter = "Jarvis Agent CLI|jarvis-agent.exe" };
                if (picker.ShowDialog(owner) != true) return;
                var folder = BrowserIntegration.Install(picker.FileName);
                MessageBox.Show(owner, "Native host registered for this Windows user.\n\nIn Chrome or Edge: open Extensions, enable Developer mode, choose Load unpacked and select:\n\n" + folder,
                    "Browser integration", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { Fail(ex); }
        });
        try
        {
            InitialProfile = loadProfile ? AgentProfile.Load() : null;
            if (InitialProfile is { } saved) { ServerUrl = saved.ServerUrl; DeviceId = saved.DeviceId; Workspace = saved.Workspace; foreach (var directory in saved.AdditionalDirectories ?? []) AdditionalDirectories.Add(directory); AllowLoopbackHttp = saved.AllowLoopbackHttp; Token = AgentProfile.GetToken(saved); }
        }
        catch (Exception ex) { Fail(ex); }
    }
    private async Task ToggleConnection()
    {
        Error = "";
        if (_runtime is not null) { await _runtime.DisposeAsync(); _runtime = null; Status = "Not connected"; Control = "Control paused"; Changed(); return; }
        var folders = new WorkspaceDirectories(Workspace, AdditionalDirectories);
        var options = new AgentOptions(ServerUrl, DeviceId, folders.Primary, AllowLoopbackHttp, folders.Additional);
        AgentProfile.Save(options, Token);
        var prompts = new LocalPrompts(_owner, _permissions, _permissionStore);
        _runtime = new AgentRuntime(prompts, prompts, new DesktopArtifactSink(_owner), () => _owner, _permissions, _settingsRoot);
        _runtime.Gate.Changed += armed => _owner.Dispatcher.InvokeAsync(() =>
            Control = armed ? "Armed · until you pause" : "Control paused");
        _runtime.Connection.Activity += e => _owner.Dispatcher.InvokeAsync(() => { Events.Insert(0, e); while (Events.Count > 150) Events.RemoveAt(Events.Count - 1); });
        _runtime.Connection.ConnectionChanged += connected => _owner.Dispatcher.InvokeAsync(() => Status = connected ? "Connected securely" : "Disconnected · retrying");
        Tools.Clear(); foreach (var descriptor in _runtime.Connection.Descriptors) Tools.Add(descriptor.Name);
        Status = "Connecting…"; Changed();
        _ = ObserveAsync(_runtime.StartAsync(options, Token));
    }
    private async Task ObserveAsync(Task connection)
    { try { await connection; } catch (Exception ex) { await _owner.Dispatcher.InvokeAsync(() => Fail(ex)); } }
    private void Arm()
    {
        if (_runtime?.Connection.IsConnected != true) { Error = "Connect the agent before arming control."; return; }
        if (MessageBox.Show(_owner, "Allow your authorized MCP client to request actions until you pause or disconnect?\n\nThere is no automatic expiry. Temporary network reconnects keep this choice; restarting the app starts paused. Tools selected in Tool permissions run without asking again, except process.start and process.spawn unless you explicitly choose Always approve for that tool. Other sensitive actions still require approval. Project directories are working context, not a sandbox. Windows permissions still apply.\n\nPause any time with Ctrl + Alt + Pause.",
            "Arm local control", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _runtime.Arm(); Control = "Armed · until you pause"; Error = "";
    }
    public void Pause() { _runtime?.Pause(); Control = "Control paused"; }
    private void Fail(Exception ex) { Error = ex.Message; }
    private void Changed() { PropertyChanged?.Invoke(this, new(nameof(ConnectLabel))); PropertyChanged?.Invoke(this, new(nameof(ToolCount))); }
    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new(name)); }
    public async ValueTask DisposeAsync() { if (_runtime is not null) await _runtime.DisposeAsync(); }
}
