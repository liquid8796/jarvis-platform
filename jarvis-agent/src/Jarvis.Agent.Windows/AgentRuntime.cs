using System.Reflection;
using System.IO;
using System.Collections.Concurrent;
using Jarvis.Agent.Core.Execution;
using System.Windows;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Plugins;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Agent.Core.Threads;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows;

/// <summary>Owns only this agent's transport/tools/processes; explicit local disconnect revokes the control grant.</summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly ToolInventory _inventory;
    private readonly ProcessToolSet _processes;
    private readonly ThreadRuntimeToolSet _threads;
    private readonly PluginRuntimeBootstrap _plugins;
    private readonly IDisposable _pluginLifecycleBinding;
    private readonly CancellationTokenSource _stop = new();
    private Task? _connectionTask;
    private readonly ConcurrentDictionary<string, Task> _sessionCleanup = new();
    public event Action<string>? SessionCleanupWarning;
    private readonly ToolPermissionPolicy _permissions;
    public LocalControlGate Gate { get; } = new();
    public AgentConnection Connection { get; }
    public IBrowserRuntimeClient BrowserRuntime => _inventory.BrowserRuntime;
    public PluginCatalogSnapshot PluginCatalog => _plugins.Snapshot;
    public PluginRuntimeBootstrap PluginRuntime => _plugins;

    public AgentRuntime(IApprovalService approvals, IUserQuestions questions, IArtifactSink artifacts,
        Func<Window?>? mainWindow = null, ToolPermissionPolicy? permissions = null, string? settingsRoot = null,
        string? pluginDirectory = null, IEnumerable<IAgentTool>? pluginTools = null,
        IRemoteTaskAdaptiveCoordinator? adaptiveCoordinator = null, IEnumerable<IPluginLifecycleHook>? pluginHooks = null)
    {
        var root = settingsRoot ?? AgentProfile.Root;
        var execution = new ExecutionSettingsStore(System.IO.Path.Combine(root, "execution-settings.json")).Load();
        var promptSettings = new Jarvis.Agent.Core.Prompts.PromptInjectionStore(System.IO.Path.Combine(root, "prompt-injection.json")).Load();
        _processes = new ProcessToolSet(() => Connection?.ExecutionSettings ?? new AgentExecutionSettings());
        if (permissions is not null) _permissions = permissions;
        else
        {
            var permissionSettings = new ToolPermissionStore(System.IO.Path.Combine(root, "tool-permissions.json")).LoadSettings();
            _permissions = new ToolPermissionPolicy(permissionSettings.FullPermissionTools, permissionSettings.AlwaysApprovedConstrainedTools);
        }
        _inventory = new ToolInventory(questions, artifacts, mainWindow, settingsRoot);
        _threads = new ThreadRuntimeToolSet(System.IO.Path.Combine(root, "thread-runtime.db"));

        var registry = new DynamicToolRegistry(_inventory.Tools.Concat(_processes.Tools).Concat(_threads.Tools));
        var lifecycle = new AgentLifecycleHub();
        var adaptive = adaptiveCoordinator ?? new DefaultRemoteTaskAdaptiveCoordinator(registry);
        Connection = new AgentConnection(registry, approvals, Gate, _permissions,
            taskStorageRoot: System.IO.Path.Combine(root, "TaskRuns"), lifecycle: lifecycle, adaptiveCoordinator: adaptive);
        Connection.ApplyExecutionSettings(execution);
        Connection.ConfigurePromptContext(promptSettings.CreateContext());
        Connection.SessionProcessCountProvider = _processes.RunningForSession;
        Connection.SessionStopped += StopSessionOwnedActivity;
        _inventory.BrowserSessionStopRequested += StopBrowserSession;

        var agentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);
        _plugins = new PluginRuntimeBootstrap(pluginDirectory ?? System.IO.Path.Combine(root, "plugins"),
            pluginTools ?? [], agentVersion, pluginHooks);
        Connection.ApplyPluginCatalog(_plugins.Snapshot);
        _plugins.Changed += ApplyPluginCatalog;
        _pluginLifecycleBinding = _plugins.BindLifecycle(lifecycle);
        _plugins.StartWatching();

        _permissions.PermissionsRevoked += StopOwnedActivity;
        // A temporary transport loss cancels jobs, never replays them, but preserves
        // the user's arm choice for this process. Explicit Disconnect still disarms.
        Connection.ConnectionChanged += connected => { if (!connected) StopOwnedActivity(); };
    }

    public Task StartAsync(AgentOptions options, string token)
    {
        if (_connectionTask is not null) throw new InvalidOperationException("Agent already started.");
        return _connectionTask = Connection.RunAsync(options with { ExecutionSettings = Connection.ExecutionSettings }, token, _stop.Token);
    }

    public void ApplyExecutionSettings(AgentExecutionSettings settings) => Connection.ApplyExecutionSettings(settings);
    public int RunningProcessJobs => _processes.RunningCount;
    public int RunningSessionProcessJobs(AgentSessionIdentity identity) => _processes.RunningForSession(identity);
    private void StopBrowserSession(string sessionId)
    {
        try
        {
            var sessions = Connection.Sessions.ListLocal().Where(entry => entry.Identity.SessionId == sessionId && entry.Snapshot.ClosedAt is null).Take(2).ToArray();
            if (sessions.Length == 1) Connection.StopSession(sessions[0].Identity);
        }
        catch (InvalidOperationException) { }
    }
    private void StopSessionOwnedActivity(AgentSessionIdentity identity)
    {
        _processes.StopSession(identity);
        var close = Connection.Sessions.Get(identity, allowClosed: true).ClosedAt is not null;
        var key = Guid.NewGuid().ToString("N");
        var work = CleanSessionAsync(identity, close);
        _sessionCleanup[key] = work;
        _ = RemoveCleanupAsync(key, work);
    }
    private async Task CleanSessionAsync(AgentSessionIdentity identity, bool close)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _inventory.StopSessionAsync(identity, close, timeout.Token); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        { SessionCleanupWarning?.Invoke("Browser cleanup could not complete. The session is stopped; its tabs may remain open."); }
    }
    private async Task RemoveCleanupAsync(string key, Task task)
    {
        try { await task; } finally { _sessionCleanup.TryRemove(key, out _); }
    }
    public void Arm() => Gate.Arm();
    public void Pause() { Connection.Pause(); StopOwnedActivity(); }
    private void StopOwnedActivity() { _processes.StopAll(); _inventory.Pause(); }
    private void ApplyPluginCatalog(PluginCatalogSnapshot snapshot) => Connection.ApplyPluginCatalog(snapshot);

    public async ValueTask DisposeAsync()
    {
        _permissions.PermissionsRevoked -= StopOwnedActivity;
        _plugins.Changed -= ApplyPluginCatalog;
        Pause(); _stop.Cancel(); await Connection.DisposeAsync();
        if (_connectionTask is not null) try { await _connectionTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        try { await Task.WhenAll(_sessionCleanup.Values).WaitAsync(TimeSpan.FromSeconds(6)); } catch (Exception) { }
        _inventory.BrowserSessionStopRequested -= StopBrowserSession;
        _pluginLifecycleBinding.Dispose(); _plugins.Dispose(); _threads.Dispose(); _processes.Dispose(); _inventory.Dispose(); _stop.Dispose();
    }
}
