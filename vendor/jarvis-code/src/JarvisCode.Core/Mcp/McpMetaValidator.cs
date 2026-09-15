using System.Text.Json.Nodes;
using Jint;

namespace JarvisCode.Core.Mcp;

/// <summary>
/// Ajv's standalone 2020-12 meta-validator, with the same first-error output as
/// the installed CLI. Only the trusted embedded validator is executable; the
/// server's schema enters as JSON data. No runtime Node/npm installation is used.
/// </summary>
internal static class McpMetaValidator
{
    private static readonly Lock Gate = new();
    private static Engine? _engine;

    public static McpSchemaValidation Validate(JsonObject schema)
    {
        lock (Gate)
        {
            try
            {
                if (_engine is null)
                {
                    using var stream = typeof(McpMetaValidator).Assembly.GetManifestResourceStream(
                        "JarvisCode.Core.Mcp.SchemaMetaValidator.js")
                        ?? throw new InvalidOperationException("Embedded schema validator is unavailable.");
                    using var reader = new StreamReader(stream);
                    _engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(3))
                        .LimitMemory(64 * 1024 * 1024));
                    _engine.Execute(reader.ReadToEnd());
                }
                var json = _engine.Invoke("validateMcpSchemaJson", schema.ToJsonString()).AsString();
                var result = JsonNode.Parse(json)!.AsObject();
                return result["valid"]!.GetValue<bool>() ? McpSchemaValidation.Ok
                    : new McpSchemaValidation(false, "meta", result["detail"]?.GetValue<string>());
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException
                                       || ex.GetType().Namespace?.StartsWith("Jint", StringComparison.Ordinal) == true)
            {
                _engine = null;
                return new McpSchemaValidation(false, "meta", "validation threw: " + ex.Message);
            }
        }
    }
}
