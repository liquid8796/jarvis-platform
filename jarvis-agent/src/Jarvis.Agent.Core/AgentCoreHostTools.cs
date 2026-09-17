using Jarvis.Agent.Core.DeveloperTools;
using Jarvis.Agent.Core.ToolPrograms;
using Jarvis.Protocol;
using Jarvis.Agent.Core.Sessions;

namespace Jarvis.Agent.Core;

/// <summary>
/// Single source of truth for host-owned Core tools that are layered onto the platform/vendor inventory.
/// Descriptor-only surfaces use the same implementations with an invoker that can never execute.
/// </summary>
public static class AgentCoreHostTools
{
    public static IReadOnlyList<IAgentTool> Create(GuardedToolInvoker guardedInvoker, SessionToolServices? sessionServices = null)
    {
        ArgumentNullException.ThrowIfNull(guardedInvoker);
        return
        [
            new ToolProgramTool(new ToolProgramEngine(guardedInvoker)),
            new SafeScriptTool(new SafeScriptEngine(guardedInvoker)),
            new DeveloperSymbolSearchTool(),
            new DeveloperTestTool(),
            .. SessionToolSet.Create(sessionServices)
        ];
    }

    public static IReadOnlyList<ToolDescriptor> Descriptors => Create(DescriptorOnlyInvoker)
        .Select(tool => tool.Descriptor).ToArray();

    private static Task<ToolReply> DescriptorOnlyInvoker(
        string toolId,
        System.Text.Json.JsonElement arguments,
        AgentExecutionContext context,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Descriptor-only host tool cannot execute.");
}