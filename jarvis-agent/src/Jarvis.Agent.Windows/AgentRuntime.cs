using System.Windows;
using Jarvis.Agent.Core;
using Jarvis.Protocol;
namespace Jarvis.Agent.Windows;
/// <summary>Owns only this agent's transport/tools/processes; explicit local disconnect revokes the control grant.</summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly ToolInventory _inventory;
    private readonly ProcessToolSet _processes = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _connectionTask;
    private readonly ToolPermissionPolicy _permissions;
    public LocalControlGate Gate { get; } = new();
    public AgentConnection Connection { get; }
    public AgentRuntime(IApprovalService approvals, IUserQuestions questions, IArtifactSink artifacts, Func<Window?>? mainWindow = null, ToolPermissionPolicy? permissions = null, string? settingsRoot = null)
    {
        _permissions = permissions ?? new ToolPermissionPolicy(new ToolPermissionStore(
            System.IO.Path.Combine(settingsRoot ?? AgentProfile.Root, "tool-permissions.json")).Load());
        _inventory = new ToolInventory(questions, artifacts, mainWindow, settingsRoot);
        Connection = new AgentConnection(_inventory.Tools.Concat(_processes.Tools), approvals, Gate, _permissions);
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
