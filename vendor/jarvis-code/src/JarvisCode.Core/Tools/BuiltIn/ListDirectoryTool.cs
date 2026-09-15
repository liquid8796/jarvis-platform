using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

public sealed class ListDirectoryTool : ITool
{
    public string Name => "list_directory";

    public string Description =>
        "Lists files and subdirectories of a directory (non-recursive). Directories end with a path separator.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("path", SchemaBuilder.String("Directory to list; defaults to the working directory")),
        ]);

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"List({arguments["path"]?.GetValue<string>() ?? "."})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = context.ResolvePath(arguments["path"]?.GetValue<string>() ?? ".");
        if (!Directory.Exists(path))
            return Task.FromResult(ToolResult.Error($"Directory not found: {path}"));

        var builder = new StringBuilder();
        builder.AppendLine(path);
        var directories = Directory.GetDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
            builder.AppendLine($"  {Path.GetFileName(directory)}{Path.DirectorySeparatorChar}");
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            builder.AppendLine($"  {info.Name}  ({FormatSize(info.Length)})");
        }
        if (!directories.Any() && !files.Any())
            builder.AppendLine("  (empty)");

        return Task.FromResult(ToolResult.Success(context.Truncate(builder.ToString().TrimEnd(), "listing")));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
