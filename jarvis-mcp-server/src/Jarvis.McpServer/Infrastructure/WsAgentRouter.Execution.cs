using Jarvis.Protocol;

namespace Jarvis.McpServer.Infrastructure;

public sealed partial class WsAgentRouter
{
    private sealed partial class Peer
    {
        private readonly object _admissionSync = new();
        private int _normalInFlight, _controlInFlight;
        private AgentExecutionSettings _executionSettings = new();
        public bool SupportsExecutionSettings => Capabilities.Contains(AgentExecutionSettings.Capability, StringComparer.Ordinal);
        public AgentExecutionSettings ExecutionSettings => Volatile.Read(ref _executionSettings);

        public void InitializeExecutionSettings(AgentExecutionSettings? settings)
        {
            var initial = settings ?? new AgentExecutionSettings();
            initial.Validate();
            Volatile.Write(ref _executionSettings, initial);
        }
        public long ApplyExecutionSettings(WireMessage message)
        {
            if (!SupportsExecutionSettings || message.ExecutionSettings is not { } settings ||
                message.ExecutionSettingsRevision != settings.Revision)
                throw new InvalidDataException("Invalid execution settings update.");
            settings.Validate();
            lock (_admissionSync)
            {
                var previous = ExecutionSettings;
                if (settings.Revision < previous.Revision) return previous.Revision;
                if (settings.Revision == previous.Revision && settings != previous)
                    throw new InvalidDataException("Conflicting execution settings revision.");
                Volatile.Write(ref _executionSettings, settings);
                return settings.Revision;
            }
        }

        public async Task<IDisposable> EnterAsync(bool control, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!SupportsExecutionSettings)
            {
                if (!await Slots.WaitAsync(0, ct)) throw new InvalidOperationException("Agent busy. No command was dispatched.");
                return new AdmissionLease(() => Slots.Release());
            }
            lock (_admissionSync)
            {
                if (control ? _controlInFlight >= 16 : _normalInFlight >= ExecutionSettings.AdmissionCapacity)
                    throw new AgentRequestException("QUEUE_FULL", control ? "Agent control queue is full. No command was dispatched."
                        : "Agent execution queue is full. No command was dispatched.");
                if (control) _controlInFlight++; else _normalInFlight++;
                return new AdmissionLease(() =>
                {
                    lock (_admissionSync)
                    {
                        if (control) _controlInFlight--; else _normalInFlight--;
                    }
                });
            }
        }
        private sealed class AdmissionLease(Action release) : IDisposable
        {
            private Action? _release = release;
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
