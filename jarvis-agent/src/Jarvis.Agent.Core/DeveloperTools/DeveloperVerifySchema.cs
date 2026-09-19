using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.DeveloperTools;

public static class DeveloperVerifySchema
{
    public static JsonElement Create() => WireJson.Element(new
    {
        type = "object", additionalProperties = false, required = new[] { "kind" },
        properties = new
        {
            kind = new { type = "string", @enum = new[] { "http", "json", "file", "sqlite" } },
            path = new { type = "string", minLength = 1, maxLength = 2048 },
            url = new { type = "string", minLength = 1, maxLength = 4096 },
            method = new { type = "string", @enum = new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE" } },
            allowRemote = new { type = "boolean", description = "Explicitly authorize a non-loopback HTTPS probe. Redirects are never followed." },
            headers = new { type = "object", additionalProperties = new { type = "string" }, maxProperties = 16 },
            body = new { description = "Optional JSON request body; never replayed by readiness polling." },
            expectedStatus = new { type = "integer", minimum = 100, maximum = 599 },
            timeoutMs = new { type = "integer", minimum = 100, maximum = 60000, @default = 10000 },
            readiness = new
            {
                type = "object", additionalProperties = false,
                properties = new
                {
                    url = new { type = "string", maxLength = 4096 },
                    expectedStatus = new { type = "integer", minimum = 100, maximum = 599, @default = 200 },
                    timeoutMs = new { type = "integer", minimum = 100, maximum = 30000, @default = 5000 }
                }
            },
            assertions = new
            {
                type = "array", minItems = 1, maxItems = 64,
                items = new
                {
                    type = "object", additionalProperties = false, required = new[] { "path", "op" },
                    properties = new
                    {
                        path = new { type = "string", maxLength = 1024, description = "RFC 6901 JSON pointer; empty string selects the root." },
                        op = new { type = "string", @enum = new[] { "exists", "type", "equals", "notEquals", "near", "contains", "count", "gte", "lte" } },
                        expected = new { description = "Expected JSON value. Required except exists (defaults true)." },
                        tolerance = new { type = "number", minimum = 0, description = "Absolute finite tolerance, required by near." }
                    }
                }
            },
            schema = new { type = "object", description = "Supported JSON Schema subset: type, properties, required, additionalProperties(boolean), items, enum, minimum, maximum, minItems, maxItems. Other keywords fail as not_supported." },
            exists = new { type = "boolean" },
            sha256 = new { type = "string", pattern = "^[a-fA-F0-9]{64}$" },
            textEquals = new { type = "string", maxLength = 32768 },
            textContains = new { type = "string", minLength = 1, maxLength = 32768 },
            archiveMembers = new { type = "array", minItems = 1, maxItems = 128, items = new { type = "string", minLength = 1, maxLength = 512 } },
            query = new { type = "string", minLength = 1, maxLength = 8192, description = "One SELECT statement against a workspace SQLite fixture. No statement separators or comments." },
            parameters = new { type = "object", maxProperties = 32, additionalProperties = new { description = "Scalar SQLite bound parameter value." } }
        }
    });
}
