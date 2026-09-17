using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;
using JarvisCode.App.Services;

namespace Jarvis.Agent.Windows;

/// <summary>Each application session has its own site-consent cache, image directory and browser selection.</summary>
public sealed class SessionBrowserToolSet
{
    private readonly BrowserBridge _bridge;
    private readonly IUserQuestions _questions;
    private readonly IArtifactSink _artifacts;
    private readonly string _root;
    private readonly ComputerStateTracker _states;
    private readonly ConcurrentDictionary<AgentSessionIdentity, Lazy<Suite>> _suites = new();
    public IReadOnlyList<IAgentTool> Tools { get; }

    public SessionBrowserToolSet(BrowserBridge bridge, IUserQuestions questions, IArtifactSink artifacts,
        string root, ComputerStateTracker states)
    {
        _bridge = bridge; _questions = questions; _artifacts = artifacts; _root = root; _states = states;
        Tools = JarvisBrowserTools.Create(bridge).Select(tool => (IAgentTool)new ScopedTool(this,
            new LegacyToolAdapter(tool, "browser", questions, artifacts, root).Descriptor)).ToArray();
    }

    private Suite Create(AgentSessionIdentity identity) => new(JarvisBrowserTools.Create(_bridge,
        Path.Combine(_root, "browser-images", identity.SessionId))
        .Select(tool => new LegacyToolAdapter(tool, "browser", _questions, _artifacts, _root))
        .ToDictionary(tool => tool.Descriptor.Id, tool => (IAgentTool)tool, StringComparer.Ordinal));

    public async Task StopSessionAsync(AgentSessionIdentity identity, bool close, CancellationToken ct)
    {
        // Removing the suite drops only this session's transient origin grants. Persistent permissions remain untouched.
        if (close) _suites.TryRemove(identity, out _);
        await _bridge.EndApplicationSessionAsync(identity.SessionId, close, ct);
    }

    private sealed record Suite(IReadOnlyDictionary<string, IAgentTool> Tools)
    {
        public SemaphoreSlim Serial { get; } = new(1, 1);
    }
    private sealed class ScopedTool(SessionBrowserToolSet owner, ToolDescriptor descriptor) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = descriptor;
        public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken ct)
        {
            var identity = context.RequireSessionIdentity();
            context.SessionCancellation.ThrowIfCancellationRequested();
            var suite = owner._suites.GetOrAdd(identity, key => new Lazy<Suite>(() => owner.Create(key))).Value;
            await suite.Serial.WaitAsync(ct);
            try
            {
                context.SessionCancellation.ThrowIfCancellationRequested();
                using var scope = owner._bridge.EnterApplicationSession(identity.SessionId);
                return await suite.Tools[Descriptor.Id].ExecuteAsync(arguments, context, ct);
            }
            finally
            {
                if (ToolExecutionResources.MayChangeDesktop(Descriptor.Id)) owner._states.InvalidateAll();
                suite.Serial.Release();
            }
        }
    }
}
