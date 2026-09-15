using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

public sealed class WriteFileTool : ITool
{
    public string Name => "Write";

    public string Description =>
        "Writes content to a file, creating it (and any missing parent directories) or overwriting it entirely. " +
        "Prefer Edit for changing part of an existing file.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("file_path", SchemaBuilder.String("Absolute or working-directory-relative path to the file")),
            ("content", SchemaBuilder.String("Full content to write")),
        ],
        "file_path", "content");

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments) =>
        $"Write({arguments["file_path"]?.GetValue<string>() ?? "?"})";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = context.ResolvePath(arguments["file_path"]?.GetValue<string>() ?? "");
        var content = arguments["content"]?.GetValue<string>() ?? "";
        if (System.Text.Encoding.UTF8.GetByteCount(content) > FileSystemDefaults.MaxFileBytes)
            return ToolResult.Error(
                $"Content exceeds the {FileSystemDefaults.MaxFileBytes / (1024 * 1024)} MB write limit; write it in smaller pieces.");

        bool existed = File.Exists(path);
        if (context.Checkpoints is not null)
            await context.Checkpoints.RecordBeforeChangeAsync(path, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);

        int lineCount = content.Length == 0 ? 0 : content.Count(c => c == '\n') + 1;
        return ToolResult.Success($"{(existed ? "Overwrote" : "Created")} {path} ({lineCount} lines).");
    }
}
