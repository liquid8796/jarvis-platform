using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Approval-gated, per-session MCP bridge to the native BabylonJS Editor AI Assistant.</summary>
public sealed class BabylonEditorMcpToolSet : ExternalMcpToolSet
{
    public BabylonEditorMcpToolSet(string configPath,
        Func<McpServerConfig, CancellationToken, Task<IMcpClient>>? connect = null)
        : base(configPath, "babylonjs-editor", "BabylonJS Editor",
            "Uses the Editor's currently active, token-protected loopback AI Assistant session. " +
            "Open a project and start the Editor AI Assistant first. " +
            "Discover available tools with babylonjs_editor_list_tools before modifying the scene. " +
            "Scene edits are shared with other Editor clients, are not automatically undone on cancellation, " +
            "and failed modifying actions are never retried. " +
            "Save valuable work first; editor save operations may require user approval.",
            BabylonEditorMcpConfig.Load, connect, serializeAcrossSessions: true,
            catalogNamePrefix: "babylonjs_editor_") { }
}
