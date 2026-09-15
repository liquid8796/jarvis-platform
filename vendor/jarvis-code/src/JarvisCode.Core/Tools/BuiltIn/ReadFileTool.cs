using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

public sealed class ReadFileTool : ITool
{
    private const int DefaultLineLimit = 2000;

    public string Name => "Read";

    public string Description =>
        "Reads a local file. Text files return content with line numbers; PDF files return " +
        "page text and rendered page images. Use 'pages' for PDF ranges (at most 20 pages); " +
        "use 'offset' (1-based line) and 'limit' to read a slice of a text file.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("file_path", SchemaBuilder.String("Absolute or working-directory-relative path to the file")),
            ("offset", SchemaBuilder.Integer("1-based line number to start reading from (default 1)")),
            ("limit", SchemaBuilder.Integer($"Maximum number of lines to return (default {DefaultLineLimit})")),
            ("pages", SchemaBuilder.String(
                "Page range for PDF files (e.g., \"1-5\", \"3\", \"10-20\"). Only applicable to PDF files. " +
                "Maximum 20 pages per request.")),
        ],
        "file_path");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"Read({arguments["file_path"]?.GetValue<string>() ?? "?"})";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = context.ResolvePath(arguments["file_path"]?.GetValue<string>() ?? "");
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return await Documents.PdfDocumentReader.ReadAsync(
                path, JsonArgs.GetString(arguments, "pages"), context.MaxOutputChars, cancellationToken);
        }

        if (FileSystemDefaults.CheckReadableSize(path) is { } sizeError)
            return ToolResult.Error(sizeError);
        if (FileSystemDefaults.LooksBinary(path))
            return ToolResult.Error($"File appears to be binary and cannot be shown as text: {path}");

        int offset = Math.Max(1, JsonArgs.GetInt(arguments, "offset") ?? 1);
        int limit = Math.Max(1, JsonArgs.GetInt(arguments, "limit") ?? DefaultLineLimit);

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (lines.Length == 0)
            return ToolResult.Success("(file is empty)");
        if (offset > lines.Length)
            return ToolResult.Error($"Offset {offset} is past the end of the file ({lines.Length} lines).");

        var slice = lines.Skip(offset - 1).Take(limit)
            .Select((line, i) => $"{offset + i,6}\t{line}");
        var body = string.Join('\n', slice);
        int end = Math.Min(lines.Length, offset + limit - 1);
        if (end < lines.Length)
            body += $"\n... ({lines.Length - end} more lines; continue with offset={end + 1})";
        return ToolResult.Success(context.Truncate(body, "file content"));
    }
}
