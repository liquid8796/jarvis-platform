using System.Collections.Concurrent;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows;

/// <summary>Stateful desktop services (display selection, screenshot frame) must not be shared across chats.</summary>
public sealed class PerSessionToolSet
{
    private readonly Func<IReadOnlyList<IAgentTool>> _factory;
    private readonly ConcurrentDictionary<AgentSessionIdentity, Lazy<Suite>> _sessions = new();
    public IReadOnlyList<IAgentTool> Tools { get; }
    public PerSessionToolSet(Func<IReadOnlyList<IAgentTool>> factory)
    {
        _factory = factory;
        Tools = factory().Select(tool => (IAgentTool)new ScopedTool(this, tool.Descriptor)).ToArray();
    }
    public void Forget(AgentSessionIdentity identity) => _sessions.TryRemove(identity, out _);
    private sealed record Suite(IReadOnlyDictionary<string, IAgentTool> Tools)
    {
        public SemaphoreSlim Serial { get; } = new(1, 1);
    }
    private sealed class ScopedTool(PerSessionToolSet owner, ToolDescriptor descriptor) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = descriptor;
        public async Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
        {
            var identity = context.RequireSessionIdentity();
            var suite = owner._sessions.GetOrAdd(identity, _ => new Lazy<Suite>(() =>
                new Suite(owner._factory().ToDictionary(t => t.Descriptor.Id, StringComparer.Ordinal)))).Value;
            await suite.Serial.WaitAsync(ct);
            try { context.SessionCancellation.ThrowIfCancellationRequested(); return await suite.Tools[Descriptor.Id].ExecuteAsync(args, context, ct); }
            finally { suite.Serial.Release(); }
        }
    }
}
