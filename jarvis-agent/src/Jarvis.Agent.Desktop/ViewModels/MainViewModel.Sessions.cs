using System.Windows;
using System.Windows.Threading;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;
using Jarvis.Agent.Desktop.Infrastructure;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;

namespace Jarvis.Agent.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private bool _sessionUiReady;
    private AgentOptions? _connectedOptions;
    private string? _connectedToken;
    private DispatcherTimer? _sessionRefreshTimer;
    public ExecutionSettingsViewModel Execution { get; private set; } = null!;
    public SessionsViewModel Sessions { get; private set; } = null!;
    public RelayCommand ClearWorkspaceCommand { get; private set; } = null!;
    public RelayCommand SaveWorkspaceDefaultsCommand { get; private set; } = null!;
    private string _workspaceStatus = "A default is optional. Each chat can select its own folder later.";
    public string WorkspaceStatus { get => _workspaceStatus; private set => Set(ref _workspaceStatus, value); }

    private void InitializeSessionUi()
    {
        Execution = new(new ExecutionSettingsStore(System.IO.Path.Combine(_settingsRoot, "execution-settings.json")),
            settings => _runtime?.ApplyExecutionSettings(settings));
        Sessions = new(() => _runtime?.Connection.GetLocalSessionOverview() ?? [],
            () => _runtime?.Connection.IsConnected == true,
            (identity, close) => _runtime!.Connection.StopSession(identity, close),
            row => MessageBox.Show(_owner,
                $"Close {row.Label}?\n\nIts queued work and owned processes will be cancelled. Its session handle cannot resume after closing. Other sessions will not be paused.",
                "Close this session", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        ClearWorkspaceCommand = new(() => { Workspace = ""; AdditionalDirectories.Clear(); SelectedDirectory = null; WorkspaceStatus = "No default workspace. Save or reconnect to apply this default to new sessions."; });
        SaveWorkspaceDefaultsCommand = new(() =>
        {
            try
            {
                if (_runtime is null || _connectedOptions is null || _connectedToken is null) { WorkspaceStatus = "This default will be saved when you connect. Existing sessions are never redirected."; return; }
                var folders = new WorkspaceDirectories(Workspace, AdditionalDirectories);
                var options = _connectedOptions with { Workspace = folders.Primary, AdditionalDirectories = folders.Additional };
                AgentProfile.Save(options, _connectedToken);
                _connectedOptions = options;
                _runtime.Connection.UpdateDefaultWorkspace(folders.Primary, folders.Additional);
                WorkspaceStatus = "Default saved for new sessions. Existing sessions and accepted requests keep their workspace.";
            }
            catch (Exception ex) { Fail(ex); }
        });
        _sessionRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _owner.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _sessionRefreshTimer.Tick += (_, _) => { if (_owner.IsVisible && SelectedTab >= 2) RefreshSessionUi(); };
        _sessionRefreshTimer.Start(); _sessionUiReady = true;
    }
    private void RefreshSessionUi()
    {
        var connection = _runtime?.Connection;
        Execution.UpdateRuntimeState(connection?.IsConnected == true, connection?.ExecutionSettings.Revision ?? 0,
            connection?.AcknowledgedExecutionSettingsRevision ?? 0, connection?.ServerSupportsExecutionSettings == true);
        if (SelectedTab == 3) Sessions.Refresh();
    }
}
