using System.Globalization;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.DeveloperTools;

internal sealed record VerificationAssertion(string Id, bool Passed, object? Expected, object? Actual, string? Reason = null);

internal static class VerificationAssertions
{
    private static readonly HashSet<string> SchemaKeys = new(StringComparer.Ordinal)
    { "type", "properties", "required", "additionalProperties", "items", "enum", "minimum", "maximum", "minItems", "maxItems" };

    public static void Evaluate(JsonElement document, JsonElement arguments, List<VerificationAssertion> results)
    {
        var observedNodes = 0;
        UniqueKeys(document, ref observedNodes);
        var hasAssertions = arguments.TryGetProperty("assertions", out var assertions);
        var hasSchema = arguments.TryGetProperty("schema", out var schema);
        if (!hasAssertions && !hasSchema) throw new ArgumentException("JSON/SQLite verification requires assertions or schema.");
        if (hasAssertions)
        {
            if (assertions.ValueKind != JsonValueKind.Array || assertions.GetArrayLength() is < 1 or > 64)
                throw new ArgumentException("assertions must contain 1-64 checks.");
            foreach (var assertion in assertions.EnumerateArray())
            {
                DeveloperVerifyTool.Keys(assertion, "path", "op", "expected", "tolerance");
                var pointer = DeveloperVerifyTool.Text(assertion, "path", 1024, allowEmpty: true);
                var op = DeveloperVerifyTool.Text(assertion, "op", 32);
                var exists = Resolve(document, pointer, out var actual);
                var hasExpected = assertion.TryGetProperty("expected", out var expected);
                if (!hasExpected && op != "exists") throw new ArgumentException(op + " requires expected.");
                var passed = op switch
                {
                    "exists" => exists == (!hasExpected || expected.GetBoolean()),
                    "equals" => exists && Equal(actual, expected),
                    "notEquals" => exists && !Equal(actual, expected),
                    "type" => exists && IsType(actual, expected.GetString() ?? ""),
                    "near" => exists && Near(actual, expected, assertion),
                    "gte" => NumericComparison(exists, actual, expected, minimum: true),
                    "lte" => NumericComparison(exists, actual, expected, minimum: false),
                    "contains" => exists && (actual.ValueKind == JsonValueKind.String
                        ? actual.GetString()!.Contains(expected.GetString()!, StringComparison.Ordinal)
                        : actual.ValueKind == JsonValueKind.Array && actual.EnumerateArray().Any(value => Equal(value, expected))),
                    "count" => exists && actual.ValueKind == JsonValueKind.Array && actual.GetArrayLength() == expected.GetInt32(),
                    _ => throw new NotSupportedException("Unknown assertion operator: " + op)
                };
                results.Add(new("json:" + pointer + ":" + op, passed, hasExpected ? Bounded(expected) : true,
                    exists ? Bounded(actual) : null, exists ? null : "JSON pointer did not resolve."));
            }
        }
        if (hasSchema)
        {
            var schemaNodes = 0;
            ValidateSchemaShape(schema, 0, ref schemaNodes);
            ValidateSchema(schema, document, "", results, 0);
        }
    }

    private static bool Near(JsonElement actual, JsonElement expected, JsonElement assertion)
    {
        if (!assertion.TryGetProperty("tolerance", out var tolerance)) throw new ArgumentException("near requires an absolute tolerance.");
        var bound = Number(tolerance);
        if (bound < 0) throw new ArgumentException("Tolerance cannot be negative.");
        var wanted = Number(expected);
        return actual.ValueKind == JsonValueKind.Number && actual.TryGetDouble(out var observed) && double.IsFinite(observed) && Math.Abs(observed - wanted) <= bound;
    }

    private static bool NumericComparison(bool exists, JsonElement actual, JsonElement expected, bool minimum)
    {
        var wanted = Number(expected);
        return exists && actual.ValueKind == JsonValueKind.Number && actual.TryGetDouble(out var observed) && double.IsFinite(observed) && (minimum ? observed >= wanted : observed <= wanted);
    }

    internal static double Number(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new ArgumentException("Assertion requires a finite JSON number.");
        return number;
    }

    private static bool IsType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) && decimal.Truncate(d) == d,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new NotSupportedException("Unsupported JSON type: " + type)
    };

    internal static bool Equal(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count() &&
                a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var value) && Equal(p.Value, value)),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => Equal(pair.First, pair.Second)),
            JsonValueKind.Number => a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) ? x == y : Number(a) == Number(b),
            JsonValueKind.String => a.GetString() == b.GetString(),
            _ => a.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False
        };
    }

    internal static bool Resolve(JsonElement root, string pointer, out JsonElement result)
    {
        result = root;
        if (pointer.Length == 0) return true;
        if (!pointer.StartsWith('/')) throw new ArgumentException("Assertion path must be a JSON pointer beginning with '/'.");
        foreach (var encoded in pointer[1..].Split('/'))
        {
            for (var i = 0; i < encoded.Length; i++)
                if (encoded[i] == '~' && (++i == encoded.Length || encoded[i] is not ('0' or '1')))
                    throw new ArgumentException("Invalid JSON pointer escape.");
            var key = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty(key, out var child)) result = child;
            else if (result.ValueKind == JsonValueKind.Array && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < result.GetArrayLength()) result = result[index];
            else { result = default; return false; }
        }
        return true;
    }

    private static void ValidateSchema(JsonElement schema, JsonElement value, string pointer, List<VerificationAssertion> results, int depth)
    {
        if (depth > 16 || results.Count > 256) throw new ArgumentException("JSON schema verification exceeded its bounds.");
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("Schema must be an object.");
        foreach (var property in schema.EnumerateObject())
            if (!SchemaKeys.Contains(property.Name)) throw new NotSupportedException("Unsupported schema keyword: " + property.Name);
        if (schema.TryGetProperty("type", out var type))
        {
            var passed = IsType(value, type.GetString() ?? "");
            results.Add(new("schema:" + pointer + ":type", passed, type.GetString(), value.ValueKind.ToString()));
            if (!passed) return;
        }
        if (schema.TryGetProperty("enum", out var choices))
        {
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() is < 1 or > 64) throw new ArgumentException("Schema enum requires 1-64 values.");
            results.Add(new("schema:" + pointer + ":enum", choices.EnumerateArray().Any(option => Equal(option, value)), Bounded(choices), Bounded(value)));
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.EnumerateObject().Take(257).Count() > 256) throw new NotSupportedException("Schema object exceeds 256 fields.");
            if (schema.TryGetProperty("required", out var required))
            {
                if (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > 64) throw new ArgumentException("Invalid required schema fields.");
                foreach (var field in required.EnumerateArray())
                    results.Add(new("schema:" + pointer + ":required:" + field.GetString(), value.TryGetProperty(field.GetString()!, out _), true, value.TryGetProperty(field.GetString()!, out _)));
            }
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            if (hasProperties && properties.ValueKind != JsonValueKind.Object) throw new ArgumentException("Schema properties must be an object.");
            if (schema.TryGetProperty("additionalProperties", out var additional) && !additional.GetBoolean())
                foreach (var field in value.EnumerateObject()) results.Add(new("schema:" + pointer + ":known:" + field.Name, hasProperties && properties.TryGetProperty(field.Name, out _), true, hasProperties && properties.TryGetProperty(field.Name, out _)));
            if (hasProperties)
                foreach (var property in properties.EnumerateObject())
                    if (value.TryGetProperty(property.Name, out var child)) ValidateSchema(property.Value, child, pointer + "/" + property.Name, results, depth + 1);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var (key, minimum) in new[] { ("minItems", true), ("maxItems", false) })
                if (schema.TryGetProperty(key, out var limit)) results.Add(new("schema:" + pointer + ":" + key, minimum ? value.GetArrayLength() >= limit.GetInt32() : value.GetArrayLength() <= limit.GetInt32(), limit.GetInt32(), value.GetArrayLength()));
            if (schema.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var child in value.EnumerateArray()) ValidateSchema(items, child, pointer + "/" + index++, results, depth + 1);
            }
        }
        if (value.ValueKind == JsonValueKind.Number)
            foreach (var (key, minimum) in new[] { ("minimum", true), ("maximum", false) })
                if (schema.TryGetProperty(key, out var limit)) results.Add(new("schema:" + pointer + ":" + key, minimum ? Number(value) >= Number(limit) : Number(value) <= Number(limit), Number(limit), Number(value)));
    }

    private static void ValidateSchemaShape(JsonElement schema, int depth, ref int nodes)
    {
        if (depth > 16 || ++nodes > 256 || schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid or oversized schema.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in schema.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new ArgumentException("Duplicate schema keyword: " + property.Name);
            if (!SchemaKeys.Contains(property.Name)) throw new NotSupportedException("Unsupported schema keyword: " + property.Name);
            switch (property.Name)
            {
                case "type": _ = IsType(WireJson.Element((object?)null), property.Value.GetString() ?? ""); break;
                case "properties":
                    if (property.Value.ValueKind != JsonValueKind.Object || property.Value.EnumerateObject().Count() > 64) throw new ArgumentException("Schema properties requires at most 64 fields.");
                    foreach (var child in property.Value.EnumerateObject()) ValidateSchemaShape(child.Value, depth + 1, ref nodes);
                    break;
                case "items": ValidateSchemaShape(property.Value, depth + 1, ref nodes); break;
                case "additionalProperties": _ = property.Value.GetBoolean(); break;
                case "minimum": case "maximum": _ = Number(property.Value); break;
                case "minItems": case "maxItems":
                    if (!property.Value.TryGetInt32(out var count) || count < 0) throw new ArgumentException("Schema array bound must be a nonnegative integer.");
                    break;
                case "required":
                    if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > 64 || property.Value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) throw new ArgumentException("Schema required must be an array of field names.");
                    break;
                case "enum":
                    if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() is < 1 or > 64) throw new ArgumentException("Schema enum requires 1-64 values.");
                    break;
            }
        }
    }

    private static void UniqueKeys(JsonElement value, ref int nodes)
    {
        if (++nodes > 100000) throw new NotSupportedException("Observed JSON exceeds 100000 nodes.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Observed JSON contains a duplicate property name.");
                UniqueKeys(property.Value, ref nodes);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) UniqueKeys(child, ref nodes);
    }

    internal static object? Bounded(JsonElement value)
    {
        var text = value.GetRawText();
        return text.Length <= 2048 ? value.Clone() : new { truncated = true, prefix = text[..2048] };
    }
}
