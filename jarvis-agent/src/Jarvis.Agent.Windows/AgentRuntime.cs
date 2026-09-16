using System.Reflection;
using System.Windows;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Plugins;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows;

/// <summary>Owns only this agent's transport/tools/processes; explicit local disconnect revokes the control grant.</summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly ToolInventory _inventory;
    private readonly ProcessToolSet _processes = new();
    private readonly PluginRuntimeBootstrap _plugins;
    private readonly CancellationTokenSource _stop = new();
    private Task? _connectionTask;
    private readonly ToolPermissionPolicy _permissions;
    public LocalControlGate Gate { get; } = new();
    public AgentConnection Connection { get; }
    public PluginCatalogSnapshot PluginCatalog => _plugins.Snapshot;

    public AgentRuntime(IApprovalService approvals, IUserQuestions questions, IArtifactSink artifacts,
        Func<Window?>? mainWindow = null, ToolPermissionPolicy? permissions = null, string? settingsRoot = null,
        string? pluginDirectory = null, IEnumerable<IAgentTool>? pluginTools = null,
        IRemoteTaskAdaptiveCoordinator? adaptiveCoordinator = null)
    {
        var root = settingsRoot ?? AgentProfile.Root;
        _permissions = permissions ?? new ToolPermissionPolicy(new ToolPermissionStore(
            System.IO.Path.Combine(root, "tool-permissions.json")).Load());
        _inventory = new ToolInventory(questions, artifacts, mainWindow, settingsRoot);

        var registry = new DynamicToolRegistry(_inventory.Tools.Concat(_processes.Tools));
        var lifecycle = new AgentLifecycleHub();
        var adaptive = adaptiveCoordinator ?? new DefaultRemoteTaskAdaptiveCoordinator(registry);
        Connection = new AgentConnection(registry, approvals, Gate, _permissions,
            lifecycle: lifecycle, adaptiveCoordinator: adaptive);

        var agentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);
        _plugins = new PluginRuntimeBootstrap(pluginDirectory ?? System.IO.Path.Combine(root, "plugins"),
            pluginTools ?? [], agentVersion);
        Connection.ApplyPluginCatalog(_plugins.Snapshot);

        _permissions.PermissionsRevoked += StopOwnedActivity;
        // A temporary transport loss cancels jobs, never replays them, but preserves
        // the user's arm choice for this process. Explicit Disconnect still disarms.
        Connection.ConnectionChanged += connected => { if (!connected) StopOwnedActivity(); };
    }

    public Task StartAsync(AgentOptions options, string token)
    {
        if (_connectionTask is not null) throw new InvalidOperationException("Agent already started.");
        return _connectionTask = Connection.RunAsync(options, token, _stop.Token);
    }

    public void Arm() => Gate.Arm();
    public void Pause() { Connection.Pause(); StopOwnedActivity(); }
    private void StopOwnedActivity() { _processes.StopAll(); _inventory.Pause(); }

    public async ValueTask DisposeAsync()
    {
        _permissions.PermissionsRevoked -= StopOwnedActivity;
        Pause(); _stop.Cancel(); await Connection.DisposeAsync();
        if (_connectionTask is not null) try { await _connectionTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _processes.Dispose(); _inventory.Dispose(); _stop.Dispose();
    }
}
