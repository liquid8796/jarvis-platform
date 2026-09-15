using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.LanguageServers;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>Input and operation mapping measured from installed CLI 2.1.260 (RHo/MHo).</summary>
public sealed class LspTool(PluginLanguageServerManager manager) : ITool
{
    public string Name => "LSP";
    public bool IsReadOnly => true;
    public string Description => """
        Interact with Language Server Protocol (LSP) servers to get code intelligence features.

        Supported operations:
        - goToDefinition: Find where a symbol is defined
        - findReferences: Find all references to a symbol
        - hover: Get hover information (documentation, type info) for a symbol
        - documentSymbol: Get all symbols (functions, classes, variables) in a document
        - workspaceSymbol: Search for symbols matching a query across the entire workspace
        - goToImplementation: Find implementations of an interface or abstract method
        - prepareCallHierarchy: Get call hierarchy item at a position (functions/methods)
        - incomingCalls: Find all functions/methods that call the function at a position
        - outgoingCalls: Find all functions/methods called by the function at a position

        All operations require:
        - filePath: The file to operate on
        - line: The line number (1-based, as shown in editors)
        - character: The character offset (1-based, as shown in editors)

        The workspaceSymbol operation also takes:
        - query: The symbol name or partial name to search for. Always provide it — most language servers return no results for an empty query.

        Note: LSP servers must be configured for the file type. If no server is available, an error will be returned.
        """;

    public JsonObject InputSchema => JsonNode.Parse("""
        {"type":"object","properties":{"operation":{"type":"string","enum":["goToDefinition","findReferences","hover","documentSymbol","workspaceSymbol","goToImplementation","prepareCallHierarchy","incomingCalls","outgoingCalls"],"description":"The LSP operation to perform"},"filePath":{"type":"string","description":"The absolute or relative path to the file"},"line":{"type":"integer","exclusiveMinimum":0,"description":"The line number (1-based, as shown in editors)"},"character":{"type":"integer","exclusiveMinimum":0,"description":"The character offset (1-based, as shown in editors)"},"query":{"type":"string","description":"The symbol name or partial name to search for (workspaceSymbol only). Most language servers return no results for an empty query, so always provide it when using workspaceSymbol."}},"required":["operation","filePath","line","character"],"additionalProperties":false}
        """)!.AsObject();

    public string DescribeCall(JsonObject arguments) => $"LSP({JsonArgs.GetString(arguments, "operation")}, {JsonArgs.GetString(arguments, "filePath")})";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var operation = JsonArgs.GetString(arguments, "operation");
        var filePath = JsonArgs.GetString(arguments, "filePath");
        var line = JsonArgs.GetInt(arguments, "line");
        var character = JsonArgs.GetInt(arguments, "character");
        if (operation is null || string.IsNullOrEmpty(filePath) || line is null or < 1 || character is null or < 1)
            return ToolResult.Error("LSP requires operation, filePath, and positive one-based line and character values.");
        try
        {
            var result = await manager.ExecuteAsync(operation, context.ResolvePath(filePath), line.Value, character.Value,
                JsonArgs.GetString(arguments, "query"), cancellationToken);
            return ToolResult.Success(context.Truncate(Format(operation, result, filePath), "LSP result"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return ToolResult.Error($"LSP request timed out: {operation}"); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or FormatException or System.ComponentModel.Win32Exception)
        { return ToolResult.Error($"Error performing {operation}: {error.Message}"); }
    }

    internal static string Format(string operation, JsonNode? result, string filePath)
    {
        if (operation == "hover")
            return result?["contents"] is { } contents ? Hover(contents) : "No hover information available";
        var items = result switch { null => [], JsonArray array => array.ToArray(), _ => new[] { result } };
        if (items.Length == 0) return operation switch
        {
            "goToDefinition" => "No definition found",
            "goToImplementation" => "No implementation found",
            "findReferences" => "No references found",
            "documentSymbol" => "No symbols found in document",
            "workspaceSymbol" => "No matching symbols found",
            "prepareCallHierarchy" => "No call hierarchy item found at this position",
            "incomingCalls" => "No incoming calls found (nothing calls this function)",
            "outgoingCalls" => "No outgoing calls found (this function calls nothing)",
            _ => throw new ArgumentException($"Unknown LSP operation: {operation}"),
        };
        var output = new StringBuilder();
        foreach (var item in items)
        {
            if (item is not JsonObject obj) throw new InvalidDataException("Language server returned a malformed result item.");
            switch (operation)
            {
                case "goToDefinition": case "goToImplementation": case "findReferences":
                    output.AppendLine(Location(obj)); break;
                case "documentSymbol": case "workspaceSymbol": case "prepareCallHierarchy":
                    AppendSymbol(output, obj, filePath, 0); break;
                case "incomingCalls": case "outgoingCalls":
                    var symbol = obj[operation == "incomingCalls" ? "from" : "to"] as JsonObject
                        ?? throw new InvalidDataException("Language server returned a call without a symbol.");
                    AppendSymbol(output, symbol, filePath, 0);
                    if (obj["fromRanges"] is JsonArray ranges)
                        output.AppendLine("  " + (operation == "incomingCalls" ? "calls at: " : "called from: ") +
                            string.Join(", ", ranges.Select(range => Position(range?["start"]))));
                    break;
                default: throw new ArgumentException($"Unknown LSP operation: {operation}");
            }
        }
        return output.ToString().TrimEnd();
    }

    private static string Hover(JsonNode contents) => contents switch
    {
        JsonArray array => string.Join("\n\n", array.Where(item => item is not null).Select(item => Hover(item!))),
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonObject obj when obj["value"]?.GetValue<string>() is { } text =>
            obj["language"]?.GetValue<string>() is { } language ? $"```{language}\n{text}\n```" : text,
        _ => throw new InvalidDataException("Language server returned malformed hover contents."),
    };

    private static string Location(JsonObject location)
    {
        var uri = location["targetUri"]?.GetValue<string>() ?? location["uri"]?.GetValue<string>()
            ?? throw new InvalidDataException("Language server returned a location without a URI.");
        var range = location["targetSelectionRange"] ?? location["targetRange"] ?? location["range"];
        return DisplayUri(uri) + ":" + Position(range?["start"]);
    }

    private static string DisplayUri(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri;

    private static string Position(JsonNode? position)
    {
        var line = position?["line"]?.GetValue<int>();
        var character = position?["character"]?.GetValue<int>();
        if (line is null or < 0 || character is null or < 0) throw new InvalidDataException("Language server returned an invalid position.");
        return $"{line + 1}:{character + 1}";
    }

    private static void AppendSymbol(StringBuilder output, JsonObject symbol, string filePath, int depth)
    {
        if (depth > 100) throw new InvalidDataException("Language server symbol nesting exceeds 100 levels.");
        var name = symbol["name"]?.GetValue<string>() ?? throw new InvalidDataException("Language server returned an unnamed symbol.");
        var location = symbol["location"] as JsonObject;
        var path = location is not null ? Location(location)
            : (symbol["uri"]?.GetValue<string>() is { } uri ? DisplayUri(uri) : filePath) + ":" + Position((symbol["selectionRange"] ?? symbol["range"])?["start"]);
        var detail = symbol["detail"]?.GetValue<string>();
        output.Append(' ', depth * 2).Append(name).Append(" (").Append(SymbolKind(symbol["kind"]?.GetValue<int>() ?? 0)).Append(") — ").Append(path);
        if (!string.IsNullOrWhiteSpace(detail)) output.Append(" — ").Append(detail);
        output.AppendLine();
        if (symbol["children"] is JsonArray children)
            foreach (var child in children) AppendSymbol(output, child?.AsObject() ?? throw new InvalidDataException("Invalid child symbol."), filePath, depth + 1);
    }

    private static string SymbolKind(int kind) => kind switch
    {
        1 => "File", 2 => "Module", 3 => "Namespace", 4 => "Package", 5 => "Class", 6 => "Method", 7 => "Property",
        8 => "Field", 9 => "Constructor", 10 => "Enum", 11 => "Interface", 12 => "Function", 13 => "Variable", 14 => "Constant",
        15 => "String", 16 => "Number", 17 => "Boolean", 18 => "Array", 19 => "Object", 20 => "Key", 21 => "Null",
        22 => "EnumMember", 23 => "Struct", 24 => "Event", 25 => "Operator", 26 => "TypeParameter", _ => "Symbol",
    };
}
