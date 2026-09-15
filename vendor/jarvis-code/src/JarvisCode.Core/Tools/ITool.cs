using System.Text.Json.Nodes;
using JarvisCode.Core.Providers;

namespace JarvisCode.Core.Tools;

/// <summary>
/// Command interface for one agent capability (read a file, run a shell command, ...).
/// Implementations must be stateless; per-invocation state travels in the context.
/// </summary>
public interface ITool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema describing the tool's arguments, sent verbatim to the model.</summary>
    JsonObject InputSchema { get; }

    /// <summary>Read-only tools are auto-approved; mutating tools go through the permission gate.</summary>
    bool IsReadOnly { get; }

    /// <summary>One-line human summary of a specific call, shown in permission prompts.</summary>
    string DescribeCall(JsonObject arguments);

    Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken);
}

public static class ToolExtensions
{
    public static ToolDefinition ToDefinition(this ITool tool) =>
        new(tool.Name, tool.Description, tool.InputSchema);
}
