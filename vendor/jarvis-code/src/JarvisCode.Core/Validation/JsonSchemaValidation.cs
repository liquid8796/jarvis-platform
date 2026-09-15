using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace JarvisCode.Core.Validation;

public sealed record SchemaValidationResult(bool IsValid, string? Error = null);

/// <summary>
/// Draft 2020-12 schema and instance validation shared by MCP discovery and
/// structured output. Registries are private to each build: a schema supplied
/// by one server cannot replace another server's $id. Reference resolution
/// never fetches a document from the network or the filesystem.
/// </summary>
public static class JsonSchemaValidation
{
    public static SchemaValidationResult ValidateSchema(JsonNode? schema)
    {
        if (schema is null)
            return new(false, "A JSON schema must be an object or a boolean.");

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return Describe(MetaSchemas.Draft202012.Evaluate(document.RootElement, Options()));
    }

    public static SchemaValidationResult ValidateInstance(JsonNode schema, JsonNode? instance)
    {
        var validSchema = ValidateSchema(schema);
        if (!validSchema.IsValid)
            return validSchema;

        try
        {
            using var schemaDocument = JsonDocument.Parse(schema.ToJsonString());
            var compiled = JsonSchema.Build(schemaDocument.RootElement, new BuildOptions
            {
                Dialect = Dialect.Draft202012,
                SchemaRegistry = new SchemaRegistry(),
            });
            using var instanceDocument = JsonDocument.Parse(instance?.ToJsonString() ?? "null");
            return Describe(compiled.Evaluate(instanceDocument.RootElement, Options()));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException
                                   || ex.GetType().Namespace == "Json.Schema")
        {
            // An unresolved external reference or malformed keyword is an
            // invalid result, never a reason to accept unvalidated output.
            return new(false, ex.Message);
        }
    }

    private static EvaluationOptions Options() => new()
    {
        OutputFormat = OutputFormat.List,
        Culture = CultureInfo.InvariantCulture,
        RequireFormatValidation = false,
    };

    private static SchemaValidationResult Describe(EvaluationResults results)
    {
        if (results.IsValid)
            return new(true);

        var errors = Errors(results).Distinct(StringComparer.Ordinal).Take(12).ToArray();
        return new(false, errors.Length == 0 ? "The value does not match the JSON schema."
            : string.Join("; ", errors));
    }

    private static IEnumerable<string> Errors(EvaluationResults result)
    {
        if (result.IsValid)
            yield break;
        if (result.Errors is not null)
        {
            foreach (var error in result.Errors)
                yield return $"{result.InstanceLocation}: {error.Value}";
        }
        if (result.Details is not null)
        {
            foreach (var detail in result.Details)
                foreach (var error in Errors(detail))
                    yield return error;
        }
    }
}
