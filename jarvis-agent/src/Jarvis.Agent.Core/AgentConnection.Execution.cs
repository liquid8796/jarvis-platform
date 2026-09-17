using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core;

public sealed partial class AgentConnection
{
    private readonly ExecutionResourceCoordinator _resources = new(100);
    private readonly FairExecutionScheduler _execution = new(5, 100, TimeSpan.FromSeconds(60));
    private readonly FairExecutionScheduler _controlExecution = new(4, 12, TimeSpan.FromSeconds(15));
    private readonly object _executionSettingsSync = new();
    private AgentExecutionSettings _executionSettings = new();
    private long _acknowledgedExecutionRevision;
    private bool _settingsProtocol;
    private readonly object _admissionSync = new();
    private readonly Dictionary<string, bool> _admitted = new(StringComparer.Ordinal);
    private int _acceptedNormal, _acceptedControl;

    public AgentExecutionSettings ExecutionSettings => Volatile.Read(ref _executionSettings);
    public long AcknowledgedExecutionSettingsRevision => Interlocked.Read(ref _acknowledgedExecutionRevision);
    public bool ServerSupportsExecutionSettings => _settingsProtocol;
    public event Action<AgentExecutionSettings>? ExecutionSettingsChanged;

    private static string SchedulingKey(AgentExecutionContext context) =>
        (context.OwnerId ?? "legacy") + "|" + (context.AgentDeviceId ?? "local") + "|" + context.SessionId;

    private void InitializeExecutionSettings(AgentExecutionSettings settings)
    {
        settings.Validate();
        lock (_executionSettingsSync)
        {
            _execution.Configure(settings.MaxConcurrentCalls, settings.MaxQueuedCalls, TimeSpan.FromSeconds(settings.QueueTimeoutSeconds));
            _resources.Configure(settings.MaxQueuedCalls);
            Volatile.Write(ref _executionSettings, settings);
            Interlocked.Exchange(ref _acknowledgedExecutionRevision, 0);
        }
    }

    /// <summary>Apply a validated, locally persisted revision without reconnecting or cancelling active work.</summary>
    public void ApplyExecutionSettings(AgentExecutionSettings settings)
    {
        settings.Validate();
        lock (_executionSettingsSync)
        {
            var before = ExecutionSettings;
            if (settings.Revision < before.Revision || (settings.Revision == before.Revision && settings != before))
                throw new AgentRequestException("SETTINGS_REVISION_CONFLICT", "Reload execution settings before applying another change.");
            if (settings == before) return;
            _execution.Configure(settings.MaxConcurrentCalls, settings.MaxQueuedCalls, TimeSpan.FromSeconds(settings.QueueTimeoutSeconds));
            _resources.Configure(settings.MaxQueuedCalls);
            Volatile.Write(ref _executionSettings, settings);
        }
        _remoteTasks?.ConfigureExecutionSettings(settings);
        foreach (var handler in ExecutionSettingsChanged?.GetInvocationList() ?? [])
            try { ((Action<AgentExecutionSettings>)handler)(settings); } catch (Exception ex) { Emit("settings", "Settings observer: " + ex.GetType().Name); }
        var wire = _current;
        if (wire is not null && _settingsProtocol)
            Track("settings-" + settings.Revision, SendExecutionSettingsAsync(wire, settings));
        NotifySessionActivity();
    }

    private async Task SendExecutionSettingsAsync(WireSocket wire, AgentExecutionSettings settings)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await wire.SendAsync(new WireMessage("execution.settings.changed")
            { ExecutionSettings = settings, ExecutionSettingsRevision = settings.Revision }, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException or ObjectDisposedException)
        { Emit("settings", "Saved locally; settings will be synchronized on reconnect."); }
    }

    private void AcknowledgeExecutionSettings(long? revision)
    {
        if (revision == ExecutionSettings.Revision) Interlocked.Exchange(ref _acknowledgedExecutionRevision, revision.Value);
        NotifySessionActivity();
    }

    private Admission TryAdmit(string id, bool control)
    {
        lock (_admissionSync)
        {
            if (_admitted.ContainsKey(id)) return Admission.Duplicate;
            if (control ? _acceptedControl >= 16 : _acceptedNormal >= ExecutionSettings.AdmissionCapacity) return Admission.Full;
            _admitted.Add(id, control);
            if (control) _acceptedControl++; else _acceptedNormal++;
            return Admission.Accepted;
        }
    }
    private void ReleaseAdmission(string id)
    {
        lock (_admissionSync)
        {
            if (!_admitted.Remove(id, out var control)) return;
            if (control) _acceptedControl--; else _acceptedNormal--;
        }
    }
    private enum Admission { Accepted, Duplicate, Full }
}
