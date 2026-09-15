using System.Text.Json.Nodes;

namespace JarvisCode.Core.Providers;

/// <summary>Provider-neutral description of a callable tool (name, purpose, JSON schema).</summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject InputSchema);
