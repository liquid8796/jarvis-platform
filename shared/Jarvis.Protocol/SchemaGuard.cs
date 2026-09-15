using System.Text.Json;
using Json.Schema;
namespace Jarvis.Protocol;
public static class SchemaGuard
{
    public static JsonSchema Compile(JsonElement schema)
    {
        RejectExternalReferences(schema);
        return JsonSchema.Build(schema, new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new SchemaRegistry() });
    }
    public static bool Matches(JsonSchema schema, JsonElement arguments) => schema.Evaluate(arguments,
        new EvaluationOptions { OutputFormat = OutputFormat.Flag, RequireFormatValidation = false }).IsValid;
    private static void RejectExternalReferences(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
            {
                if ((property.Name is "$ref" or "$dynamicRef") && property.Value.ValueKind == JsonValueKind.String &&
                    !(property.Value.GetString() ?? "").StartsWith('#'))
                    throw new ArgumentException("Tool schemas must be self-contained; external references are disabled.");
                RejectExternalReferences(property.Value);
            }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) RejectExternalReferences(item);
    }
}
