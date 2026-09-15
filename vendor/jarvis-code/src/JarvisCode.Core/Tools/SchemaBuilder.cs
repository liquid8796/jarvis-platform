using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools;

/// <summary>Tiny helper for building the JSON Schema objects tools advertise.</summary>
internal static class SchemaBuilder
{
    public static JsonObject Object(IEnumerable<(string Name, JsonObject Schema)> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
            props[name] = schema;

        var schemaObject = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
        };
        if (required.Length > 0)
            schemaObject["required"] = new JsonArray([.. required.Select(r => JsonValue.Create(r))]);
        return schemaObject;
    }

    public static JsonObject String(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    public static JsonObject Integer(string description) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
    };

    public static JsonObject Boolean(string description) => new()
    {
        ["type"] = "boolean",
        ["description"] = description,
    };
}
