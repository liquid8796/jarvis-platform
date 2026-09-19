using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;
using JarvisCode.App.Services;

namespace Jarvis.Agent.Windows;

/// <summary>
/// Browser tools remain stable MCP adapters in the Agent process, while all browser execution and
/// BrowserBridge state live in the dedicated browser service.
/// </summary>
public sealed class SessionBrowserToolSet
{
    private readonly IBrowserRuntimeClient _client;
    private readonly ComputerStateTracker _states;
    private readonly ConcurrentDictionary<string, Suite> _suites = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BrowserSessionRouting> _routing = new(StringComparer.Ordinal);

    public IReadOnlyList<IAgentTool> Tools { get; }

    public SessionBrowserToolSet(IBrowserRuntimeClient client, ComputerStateTracker states)
    {
        _client = client;
        _states = states;
        Tools = BrowserToolDescriptorCatalog.Create()
            .Select(descriptor => (IAgentTool)new ScopedTool(this, descriptor))
            .ToArray();
    }

    public async Task StopSessionAsync(AgentSessionIdentity identity, bool close, CancellationToken cancellationToken)
    {
        if (close)
        {
            _routing.TryRemove(identity.SessionId, out _);
            foreach (var key in _suites.Keys.Where(key => key.StartsWith(identity.SessionId + "|", StringComparison.Ordinal)))
                _suites.TryRemove(key, out _);
        }
        await _client.EndApplicationSessionAsync(identity.SessionId, close, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Suite
    {
        public SemaphoreSlim Serial { get; } = new(1, 1);
        public BrowserObservationTracker Observations { get; } = new();
    }

    private sealed class ScopedTool(SessionBrowserToolSet owner, ToolDescriptor descriptor) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = descriptor;

        public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        {
            context.SessionCancellation.ThrowIfCancellationRequested();
            var args = JsonNode.Parse(arguments.GetRawText())?.AsObject()
                ?? throw new ArgumentException("Object browser arguments required.");

            var family = BrowserFamilyRouting.Parse(args.Remove("browserFamily", out var familyNode) && familyNode is not null
                ? familyNode.GetValue<string>()
                : null);
            var identity = context.TrySessionIdentity();
            var scope = identity?.SessionId ?? context.IsolationScopeId;
            var routing = owner._routing.GetOrAdd(scope, static _ => new BrowserSessionRouting());
            family = routing.Resolve(family, args);

            var workingDirectory = context.Workspace;
            if (args.Remove("workingDirectory", out var directory) && directory is not null)
                workingDirectory = WorkspaceDirectories.Normalize(
                    WorkspaceDirectories.ResolvePath(directory.GetValue<string>(), context.Workspace));

            var suiteKey = scope + "|" + family.WireName();
            var suite = owner._suites.GetOrAdd(suiteKey, static _ => new Suite());

            await suite.Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                context.SessionCancellation.ThrowIfCancellationRequested();
                var sanitized = JsonSerializer.Deserialize<JsonElement>(args.ToJsonString());
                suite.Observations.BeforeTool(Descriptor.Id, sanitized);

                var request = new BrowserRuntimeRequest(
                    Descriptor.Id,
                    sanitized,
                    new BrowserRuntimeContext(
                        context.CallId,
                        identity?.SessionId,
                        context.IsolationScopeId,
                        workingDirectory ?? string.Empty,
                        context.AdditionalDirectories,
                        context.FullPermission,
                        family.WireName()));

                var reply = await owner._client.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                if (!reply.IsError) routing.Observe(family, Descriptor.Id, sanitized, reply.Text);
                suite.Observations.AfterTool(Descriptor.Id, sanitized, reply.Text, !reply.IsError);
                return reply;
            }
            finally
            {
                if (ToolExecutionResources.MayChangeDesktop(Descriptor.Id)) owner._states.InvalidateAll();
                suite.Serial.Release();
            }
        }
    }
}

internal static class BrowserToolDescriptorCatalog
{
    private static readonly Lazy<IReadOnlyList<ToolDescriptor>> Cached = new(Build);

    public static IReadOnlyList<ToolDescriptor> Create() => Cached.Value;

    private static IReadOnlyList<ToolDescriptor> Build()
    {
        using var bridge = new BrowserBridge("JarvisAgent-browser-schema-" + Guid.NewGuid().ToString("N"));
        return JarvisBrowserTools.Create(bridge, enableQa: true)
            .Select(tool => LegacyToolAdapter.CreateDescriptor(tool, "browser"))
            .ToArray();
    }

}
