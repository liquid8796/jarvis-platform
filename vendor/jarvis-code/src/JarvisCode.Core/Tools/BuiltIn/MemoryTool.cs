using System.Text.Json.Nodes;
using JarvisCode.Core.Memory;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// The agent's persistent notebook for this project. Auto-approved because it
/// can only touch markdown files inside the project's memory directory.
/// </summary>
public sealed class MemoryTool : ITool
{
    private const int MaxFileChars = 100_000;

    public string Name => "memory";

    public string Description =>
        "Reads and maintains your persistent memory for this project (survives across sessions). " +
        "MEMORY.md is the index that is loaded into every session — keep it a short list of pointers and " +
        "durable facts (conventions, decisions, pitfalls); put longer notes in separate .md files and link " +
        "them from the index. Actions: read, write (replace), append. Record facts worth remembering as you " +
        "discover them; update entries that turn out to be wrong.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("action", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("read", "write", "append"),
            }),
            ("file", SchemaBuilder.String("Markdown file name inside the memory directory (default MEMORY.md)")),
            ("content", SchemaBuilder.String("The content for write/append")),
        ],
        "action");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"Memory({JsonArgs.GetString(arguments, "action") ?? "?"}: {JsonArgs.GetString(arguments, "file") ?? ProjectMemory.IndexFileName})";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.MemoryDirectory is null)
            return ToolResult.Error("Memory is not available in this context.");

        var action = JsonArgs.GetString(arguments, "action");
        var path = ProjectMemory.ResolveFile(context.MemoryDirectory, JsonArgs.GetString(arguments, "file"));
        if (path is null)
            return ToolResult.Error("file must be a plain markdown name (e.g. notes.md) inside the memory directory.");

        switch (action)
        {
            case "read":
                if (!File.Exists(path))
                    return ToolResult.Success($"{Path.GetFileName(path)} does not exist yet.");
                var content = await File.ReadAllTextAsync(path, cancellationToken);
                return ToolResult.Success(context.Truncate(
                    content.Length == 0 ? "(empty)" : content, "memory content"));

            case "write":
            {
                var body = JsonArgs.GetString(arguments, "content");
                if (body is null)
                    return ToolResult.Error("content is required for write.");
                if (body.Length > MaxFileChars)
                    return ToolResult.Error($"Memory files are capped at {MaxFileChars:N0} characters.");
                Directory.CreateDirectory(context.MemoryDirectory);
                await File.WriteAllTextAsync(path, body, cancellationToken);
                return ToolResult.Success($"Wrote {Path.GetFileName(path)} ({body.Length} chars).");
            }

            case "append":
            {
                var body = JsonArgs.GetString(arguments, "content");
                if (string.IsNullOrEmpty(body))
                    return ToolResult.Error("content is required for append.");
                Directory.CreateDirectory(context.MemoryDirectory);
                var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "";
                if (existing.Length + body.Length > MaxFileChars)
                    return ToolResult.Error($"Memory files are capped at {MaxFileChars:N0} characters; rewrite the file smaller instead.");
                await File.WriteAllTextAsync(
                    path, existing.Length == 0 ? body : $"{existing.TrimEnd()}\n{body}", cancellationToken);
                return ToolResult.Success($"Appended to {Path.GetFileName(path)}.");
            }

            default:
                return ToolResult.Error("action must be read, write or append.");
        }
    }
}
