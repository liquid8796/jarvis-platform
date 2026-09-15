using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

public sealed class EditFileTool : ITool
{
    public string Name => "Edit";

    public string Description =>
        "Performs an exact string replacement in an existing file. 'old_string' must match the file content " +
        "exactly (including whitespace) and must be unique unless 'replace_all' is true.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("file_path", SchemaBuilder.String("Absolute or working-directory-relative path to the file")),
            ("old_string", SchemaBuilder.String("Exact text to replace")),
            ("new_string", SchemaBuilder.String("Replacement text")),
            ("replace_all", SchemaBuilder.Boolean("Replace every occurrence instead of requiring uniqueness (default false)")),
        ],
        "file_path", "old_string", "new_string");

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments) =>
        $"Edit({arguments["file_path"]?.GetValue<string>() ?? "?"})";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = context.ResolvePath(arguments["file_path"]?.GetValue<string>() ?? "");
        var oldString = arguments["old_string"]?.GetValue<string>() ?? "";
        var newString = arguments["new_string"]?.GetValue<string>() ?? "";
        bool replaceAll = JsonArgs.GetBool(arguments, "replace_all");

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");
        if (FileSystemDefaults.CheckReadableSize(path) is { } sizeError)
            return ToolResult.Error(sizeError);
        if (oldString.Length == 0)
            return ToolResult.Error("old_string must not be empty.");
        if (oldString == newString)
            return ToolResult.Error("old_string and new_string are identical; nothing to change.");

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        int occurrences = CountOccurrences(content, oldString);
        if (occurrences == 0)
            return ToolResult.Error("old_string was not found in the file. Read the file and match its content exactly.");
        if (occurrences > 1 && !replaceAll)
            return ToolResult.Error(
                $"old_string occurs {occurrences} times. Provide a longer, unique string or set replace_all=true.");

        if (context.Checkpoints is not null)
            await context.Checkpoints.RecordBeforeChangeAsync(path, cancellationToken);
        var updated = content.Replace(oldString, newString, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, updated, cancellationToken);
        return ToolResult.Success($"Replaced {occurrences} occurrence(s) in {path}.");
    }

    private static int CountOccurrences(string content, string value)
    {
        int count = 0;
        for (int index = content.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = content.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
