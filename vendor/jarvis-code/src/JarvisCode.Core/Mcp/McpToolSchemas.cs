using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JarvisCode.Core.Mcp;

/// <summary>Which settings layer a server came from — the reference's <c>Ye</c>.</summary>
public enum McpServerScope
{
    /// <summary>userSettings / flagSettings / policySettings / built-in.</summary>
    Operator,

    /// <summary>projectSettings / localSettings — the only scope a bad tool is dropped for.</summary>
    Repo,

    /// <summary>plugin / additionalDirectory.</summary>
    ThirdParty,
}

/// <summary>What <see cref="McpToolSchemas.Normalize"/> decided.</summary>
public enum McpSchemaOutcome
{
    /// <summary>No top-level combinator; the schema goes through as it is.</summary>
    Unchanged,

    /// <summary>Flattened into one object schema, with a note for the description.</summary>
    Normalized,

    /// <summary>Cannot be sent; the tool is dropped or kept with a warning.</summary>
    Drop,
}

/// <summary>The result of a normalization pass — its <c>yat</c>.</summary>
public sealed record McpSchemaNormalization(
    McpSchemaOutcome Outcome,
    JsonObject? Schema = null,
    string? Note = null,
    IReadOnlyList<string>? Combinators = null,
    string? Reason = null);

/// <summary>The result of a validation pass — its <c>Lr</c>.</summary>
public sealed record McpSchemaValidation(bool Valid, string? Check = null, string? Detail = null)
{
    public static readonly McpSchemaValidation Ok = new(true);
}

/// <summary>
/// The reference's MCP tool input-schema checks, ported from CLI 2.1.257 (its
/// <c>O</c>/<c>rt</c>/<c>x</c>/<c>nt</c>/<c>yat</c>/<c>ot</c>/<c>Nr</c>/<c>Lr</c>
/// around byte 208828602). Two passes run over every tool a server reports: a
/// top-level <c>anyOf</c>/<c>oneOf</c>/<c>allOf</c> is flattened into one object
/// schema carrying an "Input constraint:" note, and the result is then
/// validated. A tool that fails is dropped for a repo-scoped server and kept
/// with a warning anywhere else.
/// </summary>
public static class McpToolSchemas
{
    /// <summary>Its <c>O</c>: the property keys the Anthropic API accepts.</summary>
    public static readonly Regex PropertyKey = new(@"^[a-zA-Z0-9_.-]{1,64}$", RegexOptions.Compiled);

    /// <summary>How the reference renders that regex inside its own message.</summary>
    internal const string PropertyKeyDisplay = @"/^[a-zA-Z0-9_.-]{1,64}$/";

    /// <summary>Its <c>tt</c>: the top-level combinators it flattens.</summary>
    public static readonly string[] Combinators = ["anyOf", "oneOf", "allOf"];

    /// <summary>Its <c>rt</c>: the keys carried through onto the flattened schema.</summary>
    private static readonly string[] Carried =
        ["$defs", "definitions", "$schema", "additionalProperties", "description", "title"];

    private static readonly Regex LocalRef =
        new(@"^#/(\$defs|definitions)/([^/]+)$", RegexOptions.Compiled);

    /// <summary>Its <c>x</c>: resolves a local <c>$ref</c> against the root's defs.</summary>
    internal static JsonObject Resolve(JsonObject node, JsonObject root)
    {
        if (node["$ref"] is not JsonValue reference ||
            reference.GetValueKind() != JsonValueKind.String)
        {
            return node;
        }

        var match = LocalRef.Match(reference.GetValue<string>());
        if (!match.Success || root[match.Groups[1].Value] is not JsonObject defs)
        {
            return node;
        }

        return defs[match.Groups[2].Value] as JsonObject ?? node;
    }

    /// <summary>Its <c>nt</c>: how one branch of a combinator is named in the note.</summary>
    internal static string? ParameterGroup(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        if (obj["required"] is JsonArray required && required.Count > 0 &&
            required.All(static r => r is JsonValue v && v.GetValueKind() == JsonValueKind.String))
        {
            return string.Join(", ", required.Select(static r => r!.GetValue<string>()));
        }

        if (obj["properties"] is JsonObject properties && properties.Count > 0)
        {
            return string.Join(", ", properties.Select(static p => p.Key));
        }

        return null;
    }

    /// <summary>Its <c>ot</c>: the sentence prepended to the tool's description.</summary>
    internal static string Note(IReadOnlyList<string> combinators, JsonObject schema, bool anyOrOne)
    {
        if (!anyOrOne)
        {
            return "Input constraint: all listed parameters apply together " +
                "(flattened from a JSON Schema allOf).";
        }

        var key = combinators.Contains("oneOf") ? "oneOf" : "anyOf";
        var groups = new List<string>();
        if (schema[key] is JsonArray branches)
        {
            foreach (var branch in branches)
            {
                var named = ParameterGroup(branch is JsonObject o ? Resolve(o, schema) : branch);
                if (named is not null && !groups.Contains(named, StringComparer.Ordinal))
                {
                    groups.Add(named);
                }
            }
        }

        var lead = key == "oneOf"
            ? "Provide parameters for exactly one of"
            : "Provide parameters for at least one of";

        return groups.Count == 0
            ? $"Input constraint: {lead} the documented parameter groups (flattened from a JSON Schema {key})."
            : $"Input constraint: {lead}: {string.Join(" or ", groups.Select(static g => "(" + g + ")"))}.";
    }

    /// <summary>Its <c>yat</c>: flattens a top-level combinator into one object schema.</summary>
    public static McpSchemaNormalization Normalize(JsonObject? schema)
    {
        if (schema is null)
        {
            return new McpSchemaNormalization(McpSchemaOutcome.Unchanged);
        }

        var present = Combinators.Where(schema.ContainsKey).ToList();
        if (present.Count == 0)
        {
            return new McpSchemaNormalization(McpSchemaOutcome.Unchanged);
        }

        var merged = new JsonObject();
        void Absorb(JsonNode? node)
        {
            if (node is not JsonObject obj)
            {
                return;
            }

            foreach (var (key, value) in obj)
            {
                if (PropertyKey.IsMatch(key) && !merged.ContainsKey(key) && value is JsonObject)
                {
                    merged[key] = value.DeepClone();
                }
            }
        }

        Absorb(schema["properties"]);
        foreach (var name in present)
        {
            if (schema[name] is not JsonArray branches)
            {
                return new McpSchemaNormalization(
                    McpSchemaOutcome.Drop,
                    Reason: $"input schema has top-level {name} that is not an array");
            }

            foreach (var branch in branches)
            {
                if (branch is JsonObject obj)
                {
                    Absorb(Resolve(obj, schema)["properties"]);
                }
            }
        }

        var required = new List<string>();
        void AbsorbRequired(JsonNode? node)
        {
            if (node is not JsonArray list)
            {
                return;
            }

            foreach (var entry in list)
            {
                if (entry is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
                {
                    continue;
                }

                var name = value.GetValue<string>();
                if (merged.ContainsKey(name) && !required.Contains(name, StringComparer.Ordinal))
                {
                    required.Add(name);
                }
            }
        }

        AbsorbRequired(schema["required"]);
        if (schema["allOf"] is JsonArray all)
        {
            foreach (var branch in all)
            {
                if (branch is JsonObject obj)
                {
                    AbsorbRequired(Resolve(obj, schema)["required"]);
                }
            }
        }

        var anyOrOne = present.Contains("anyOf") || present.Contains("oneOf");
        var flattened = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = merged,
            ["required"] = new JsonArray([.. required.Select(static r => (JsonNode)r)]),
        };
        foreach (var key in Carried)
        {
            if (schema[key] is { } carried)
            {
                flattened[key] = carried.DeepClone();
            }
        }

        return new McpSchemaNormalization(
            McpSchemaOutcome.Normalized,
            flattened,
            Note(present, schema, anyOrOne),
            present);
    }

    /// <summary>Its <c>Nr</c>: the first property key the API would reject.</summary>
    public static string? FirstInvalidPropertyKey(JsonObject? schema)
    {
        if (schema?["properties"] is not JsonObject properties)
        {
            return null;
        }

        foreach (var (key, _) in properties)
        {
            if (!PropertyKey.IsMatch(key))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// Its <c>Lr</c>: property names first, then validation against the complete
    /// draft 2020-12 meta-schema, including nested schemas and keyword types.
    /// </summary>
    public static McpSchemaValidation Validate(JsonObject? schema)
    {
        if (FirstInvalidPropertyKey(schema) is { } offending)
        {
            var shown = offending.Length > 80 ? offending[..80] : offending;
            return new McpSchemaValidation(
                false,
                "propertyKey",
                $"property key {JsonSerializer.Serialize(shown)} does not match {PropertyKeyDisplay}");
        }

        // CLI 2.1.260's Nr removes null-valued root keywords before its meta
        // check and leaves an explicitly different dialect to its own server.
        var effective = schema is null ? new JsonObject() : new JsonObject(
            schema.Where(pair => pair.Value is not null)
                .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value!.DeepClone())));
        if (effective["$schema"] is { } dialect &&
            dialect.ToString() is not ("https://json-schema.org/draft/2020-12/schema" or
                                      "https://json-schema.org/draft/2020-12/schema#"))
            return McpSchemaValidation.Ok;

        return McpMetaValidator.Validate(effective);
    }

    /// <summary>Its <c>cQo</c>: one line of the # Unavailable MCP Tools block.</summary>
    public static string Entry(string server, string tool, string reason) =>
        $"{Quote(tool)} (MCP server {Quote(server)}): {Quote(reason)}";

    /// <summary>Its <c>uQo</c>: past this many, a server is summarized instead of listed.</summary>
    public const int MaxEntriesPerServer = 30;

    /// <summary>Its <c>dQo</c>: the line a server over the cap gets instead.</summary>
    public static string Summary(string server, int count) =>
        $"MCP server {Quote(server)}: {count} tools excluded (invalid input schema)";

    private static string Quote(string value) => "\"" + Escape(value) + "\"";

    /// <summary>
    /// Its <c>ja</c>: another server's text reaches the model inside this block,
    /// so the angle brackets that could close the surrounding tag are escaped —
    /// the same rule the cross-session envelope already uses.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>
    /// Its keep-or-drop rule: only a repo-scoped server's bad tool is dropped;
    /// anywhere else the tool is kept and a warning is logged, because an
    /// operator or a plugin put it there deliberately.
    /// </summary>
    public static bool DropsInvalidTools(McpServerScope scope) => scope == McpServerScope.Repo;

    /// <summary>
    /// Applies both passes to one tool. Returns the schema and description to
    /// advertise, or null when the tool is dropped; <paramref name="reason"/>
    /// carries why for the # Unavailable MCP Tools block.
    /// </summary>
    public static (JsonObject Schema, string? NotePrefix)? Apply(
        JsonObject? schema, McpServerScope scope, out string? reason, bool normalizeCombinators = true)
    {
        reason = null;
        var normalized = Normalize(schema);
        JsonObject effective;
        string? note = null;
        switch (normalized.Outcome)
        {
            case McpSchemaOutcome.Unchanged:
                effective = schema ?? [];
                break;
            case McpSchemaOutcome.Normalized:
                if (!normalizeCombinators)
                {
                    reason = "its input schema uses top-level " + string.Join("/", normalized.Combinators ?? []) +
                        ", which the Anthropic API does not accept";
                    return null;
                }
                effective = normalized.Schema!;
                note = normalized.Note;
                break;
            default:
                // A schema the flattener could not rewrite is never sent, whatever
                // the scope: the reference drops it before the validity check.
                reason = normalized.Reason ??
                    "input schema uses top-level " + string.Join("/", normalized.Combinators ?? []) +
                    ", which the Anthropic API does not accept";
                return null;
        }

        var validation = Validate(effective);
        if (validation.Valid)
        {
            return (effective, note);
        }

        if (!DropsInvalidTools(scope))
        {
            return (effective, note);
        }

        reason = validation.Detail;
        return null;
    }
}
