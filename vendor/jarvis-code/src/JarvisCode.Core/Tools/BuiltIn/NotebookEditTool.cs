using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// Edits Jupyter notebooks (.ipynb) cell by cell, the reference NotebookEdit:
/// replace a cell's source, insert a new cell, or delete one. Edited code
/// cells lose their outputs and execution count, since they no longer reflect
/// the new source.
/// </summary>
public sealed class NotebookEditTool : ITool
{
    public string Name => "NotebookEdit";

    public string Description =>
        "Replaces, inserts, or deletes a cell in a Jupyter notebook (.ipynb file). cell_id selects the target " +
        "cell — its id, or its 0-based index. edit_mode 'replace' (default) rewrites the selected cell's source " +
        "with new_source; 'insert' adds a new cell after the selected one (or at the top when cell_id is omitted) " +
        "and then requires cell_type; 'delete' removes the selected cell. Editing a code cell clears its outputs.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("notebook_path", SchemaBuilder.String("Path to the .ipynb file (absolute or relative to the workdir)")),
            ("new_source", SchemaBuilder.String("The new source for the cell (required unless edit_mode is delete)")),
            ("cell_id", SchemaBuilder.String("The id of the cell to edit, or its 0-based index")),
            ("cell_type", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("code", "markdown"),
                ["description"] = "The type of the cell; required when inserting a new cell",
            }),
            ("edit_mode", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("replace", "insert", "delete"),
                ["description"] = "replace (default) | insert | delete",
            }),
        ],
        "notebook_path");

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments)
    {
        var mode = JsonArgs.GetString(arguments, "edit_mode") ?? "replace";
        var cell = JsonArgs.GetString(arguments, "cell_id") ?? (mode == "insert" ? "top" : "?");
        return $"NotebookEdit({mode} {cell} in {JsonArgs.GetString(arguments, "notebook_path") ?? "?"})";
    }

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var rawPath = JsonArgs.GetString(arguments, "notebook_path");
        if (string.IsNullOrWhiteSpace(rawPath))
            return ToolResult.Error("notebook_path is required.");
        var path = context.ResolvePath(rawPath);
        if (!path.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Error("NotebookEdit only edits .ipynb files; use Edit for other files.");
        if (!File.Exists(path))
            return ToolResult.Error($"Notebook not found: {path}");

        var mode = (JsonArgs.GetString(arguments, "edit_mode") ?? "replace").ToLowerInvariant();
        if (mode is not ("replace" or "insert" or "delete"))
            return ToolResult.Error("edit_mode must be replace, insert, or delete.");
        var newSource = JsonArgs.GetString(arguments, "new_source");
        if (mode != "delete" && newSource is null)
            return ToolResult.Error("new_source is required unless edit_mode is delete.");

        JsonObject notebook;
        try
        {
            notebook = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject
                ?? throw new JsonException("the file is not a JSON object");
        }
        catch (JsonException ex)
        {
            return ToolResult.Error($"Could not parse the notebook: {ex.Message}");
        }
        if (notebook["cells"] is not JsonArray cells)
            return ToolResult.Error("The notebook has no cells array.");

        var cellId = JsonArgs.GetString(arguments, "cell_id");
        int index = FindCell(cells, cellId);
        if (cellId is not null && index < 0)
            return ToolResult.Error($"No cell with id or index '{cellId}' (the notebook has {cells.Count} cells).");

        string done;
        switch (mode)
        {
            case "replace":
            {
                if (index < 0)
                    return ToolResult.Error("cell_id is required to replace a cell.");
                if (cells[index] is not JsonObject cell)
                    return ToolResult.Error($"Cell {cellId} is malformed.");
                cell["source"] = ToSourceLines(newSource!);
                ClearOutputs(cell);
                done = $"Replaced cell {DescribeCell(cell, index)}";
                break;
            }
            case "insert":
            {
                var cellType = JsonArgs.GetString(arguments, "cell_type");
                if (cellType is not ("code" or "markdown"))
                    return ToolResult.Error("cell_type (code or markdown) is required when inserting a cell.");
                var cell = new JsonObject
                {
                    ["cell_type"] = cellType,
                    ["id"] = Guid.NewGuid().ToString("N")[..8],
                    ["metadata"] = new JsonObject(),
                    ["source"] = ToSourceLines(newSource!),
                };
                if (cellType == "code")
                {
                    cell["execution_count"] = null;
                    cell["outputs"] = new JsonArray();
                }
                int insertAt = index < 0 ? 0 : index + 1;
                cells.Insert(insertAt, cell);
                done = $"Inserted {cellType} cell at index {insertAt}";
                break;
            }
            default:
            {
                if (index < 0)
                    return ToolResult.Error("cell_id is required to delete a cell.");
                var described = cells[index] is JsonObject cell ? DescribeCell(cell, index) : $"index {index}";
                cells.RemoveAt(index);
                done = $"Deleted cell {described}";
                break;
            }
        }

        if (context.Checkpoints is not null)
            await context.Checkpoints.RecordBeforeChangeAsync(path, cancellationToken);
        await File.WriteAllTextAsync(path, notebook.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return ToolResult.Success($"{done} in {path} ({cells.Count} cells now).");
    }

    /// <summary>Finds a cell by its id, falling back to a 0-based index.</summary>
    private static int FindCell(JsonArray cells, string? cellId)
    {
        if (string.IsNullOrWhiteSpace(cellId))
            return -1;
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i] is JsonObject cell &&
                string.Equals(JsonArgs.GetString(cell, "id"), cellId, StringComparison.Ordinal))
                return i;
        }
        return int.TryParse(cellId, out int index) && index >= 0 && index < cells.Count ? index : -1;
    }

    /// <summary>Jupyter stores source as a list of lines, each keeping its newline.</summary>
    private static JsonArray ToSourceLines(string source)
    {
        var lines = new JsonArray();
        int start = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                lines.Add(JsonValue.Create(source[start..(i + 1)]));
                start = i + 1;
            }
        }
        if (start < source.Length)
            lines.Add(JsonValue.Create(source[start..]));
        return lines;
    }

    private static void ClearOutputs(JsonObject cell)
    {
        if (JsonArgs.GetString(cell, "cell_type") == "code")
        {
            cell["outputs"] = new JsonArray();
            cell["execution_count"] = null;
        }
    }

    private static string DescribeCell(JsonObject cell, int index) =>
        JsonArgs.GetString(cell, "id") is { Length: > 0 } id ? $"{id} (index {index})" : $"index {index}";
}
