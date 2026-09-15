namespace Jarvis.Agent.Core.Autonomous.Execution;

public sealed record ToolManifest(
    string Name,
    string Version,
    string Category,
    bool Enabled);
