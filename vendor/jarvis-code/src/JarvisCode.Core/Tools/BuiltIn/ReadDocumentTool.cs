using System.Text.Json.Nodes;
using JarvisCode.Core.Documents;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// Reads the text of an Office document. Read refuses these — they are zip
/// containers, not text — so without this tool an attached spreadsheet or deck
/// is unreadable. Parsing is in-process XML: nothing is executed, so unlike the
/// reference's sandbox-backed attachment analysis it needs no opt-in switch.
/// </summary>
public sealed class ReadDocumentTool : ITool
{
    public string Name => "read_document";

    public string Description =>
        "Extracts the text of an Office document: spreadsheets (.xlsx, .xlsm — every sheet, rows as " +
        "tab-separated cells), Word documents (.docx — paragraphs and tables) and presentations " +
        "(.pptx — every slide plus speaker notes). Use it whenever the user attaches or points at one of " +
        "these; Read cannot read them because they are zipped XML. Formatting, images and charts are " +
        "not returned — only text.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("file_path", SchemaBuilder.String("Absolute or working-directory-relative path to the .xlsx/.xlsm/.docx/.pptx file")),
        ],
        "file_path");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"ReadDocument({JsonArgs.GetString(arguments, "file_path") ?? "?"})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var raw = JsonArgs.GetString(arguments, "file_path");
        if (string.IsNullOrWhiteSpace(raw))
            return Task.FromResult(ToolResult.Error("file_path is required."));

        var path = context.ResolvePath(raw);
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Error($"File not found: {path}"));

        try
        {
            var text = OfficeDocumentReader.Extract(path);
            return Task.FromResult(ToolResult.Success(text.Length == 0
                ? "(the document contains no text)"
                : context.Truncate(text, "document text")));
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message));
        }
        catch (InvalidDataException)
        {
            return Task.FromResult(ToolResult.Error(
                $"{path} is not a readable Office file (the older binary .xls/.doc/.ppt formats are not supported)."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Error($"Could not read {path}: {ex.Message}"));
        }
    }
}
