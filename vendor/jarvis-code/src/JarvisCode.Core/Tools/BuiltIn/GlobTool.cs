using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

public sealed class GlobTool : ITool
{
    private const int MaxResults = 300;

    public string Name => "Glob";

    public string Description =>
        "Finds files matching a glob pattern (e.g. '**/*.cs', 'src/**/*.json'), newest first. " +
        "Build and VCS directories (.git, bin, obj, node_modules, ...) and git-ignored files " +
        "are skipped unless no_ignore is set.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("pattern", SchemaBuilder.String("Glob pattern relative to the search root")),
            ("path", SchemaBuilder.String("Directory to search; defaults to the working directory")),
            ("no_ignore", SchemaBuilder.Boolean("List build/VCS directories and git-ignored files as well")),
        ],
        "pattern");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"Glob({JsonArgs.GetString(arguments, "pattern") ?? "?"})";

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var pattern = JsonArgs.GetString(arguments, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResult.Error("pattern is required.");

        var root = context.ResolvePath(JsonArgs.GetString(arguments, "path") ?? ".");
        if (!Directory.Exists(root))
            return ToolResult.Error($"Directory not found: {root}");

        var collected = await FileSystemDefaults.CollectSearchableFilesAsync(
            root, pattern, JsonArgs.GetBool(arguments, "no_ignore"), cancellationToken);
        var files = collected
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        if (files.Count == 0)
            return ToolResult.Success($"No files match '{pattern}' under {root}.");

        var shown = files.Take(MaxResults).Select(file => file.FullName);
        var body = string.Join('\n', shown);
        if (files.Count > MaxResults)
            body += $"\n... ({files.Count - MaxResults} more matches omitted)";
        return ToolResult.Success(context.Truncate(body, "match list"));
    }
}
