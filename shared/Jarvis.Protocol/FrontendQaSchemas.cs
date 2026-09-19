namespace Jarvis.Protocol;

/// <summary>Shared closed wire schemas for the external QA workflow and browser runner.</summary>
public static class FrontendQaSchemas
{
    private static object Text(int max = 2048) => new { type = "string", minLength = 1, maxLength = max };
    private static object Value() => new { type = new[] { "string", "boolean", "number" }, maxLength = 10000 };
    public static object Locator() => new
    {
        type = "object", additionalProperties = false,
        properties = new { role = Text(500), name = Text(500), text = Text(500), testId = Text(500), css = Text(500), frame = Text(500) }
    };
    public static object Spec() => new
    {
        type = "object", additionalProperties = false, required = new[] { "url", "viewports", "steps" },
        properties = new
        {
            url = Text(), expectedUrl = Text(), ready = Locator(),
            viewports = new { type = "array", minItems = 1, maxItems = 4, items = new
            {
                type = "object", additionalProperties = false, required = new[] { "name", "width", "height" },
                properties = new { name = Text(80), width = new { type = "integer", minimum = 240, maximum = 3840 },
                    height = new { type = "integer", minimum = 240, maximum = 2160 }, mobile = new { type = "boolean" } }
            } },
            steps = new { type = "array", minItems = 1, maxItems = 24, items = new
            {
                type = "object", additionalProperties = false, required = new[] { "action", "expect" },
                properties = new
                {
                    action = new { type = "string", @enum = new[] { "click", "fill", "select", "check", "press", "assert" } },
                    locator = Locator(), value = Value(), key = Text(80), expect = new
                    {
                        type = "object", additionalProperties = false, required = new[] { "kind" },
                        properties = new { kind = new { type = "string", @enum = new[] { "visible", "hidden", "text", "value", "checked", "count", "url" } }, locator = Locator(), value = Value() }
                    }
                }
            } },
            timeoutMs = new { type = "integer", minimum = 1000, maximum = 30000, @default = 15000 },
            fullPage = new { type = "boolean", @default = true }, requireVisualReview = new { type = "boolean", @default = true }, referenceId = Text()
        }
    };
    public static object Review() => new
    {
        type = "object", additionalProperties = false,
        required = new[] { "verificationRunId", "sourceRevision", "reviewer", "summary", "checks" },
        properties = new
        {
            verificationRunId = Text(100), sourceRevision = Hash(), reviewer = Text(200), summary = Text(4000), referenceId = Text(),
            checks = new { type = "array", minItems = 1, maxItems = 128, items = new
            {
                type = "object", additionalProperties = false, required = new[] { "captureId", "screenshotSha256", "category", "passed", "observation" },
                properties = new { captureId = Text(200), screenshotSha256 = Hash(),
                    category = new { type = "string", @enum = FrontendQaRules.ReviewCategories }, passed = new { type = "boolean" }, observation = Text(2000) }
            } }
        }
    };
    public static object Hash() => new { type = "string", pattern = "^[A-Fa-f0-9]{64}$" };
    public static object Provenance() => new
    {
        type = "object", additionalProperties = false,
        required = new[] { "taskId", "sourceRevision", "verificationRunId", "targetUrl", "observationId", "capturedAt", "producer" },
        properties = new { taskId = Text(100), sourceRevision = Hash(), verificationRunId = Text(100), targetUrl = Text(), observationId = Text(200),
            capturedAt = new { type = "string", format = "date-time" }, producer = Text(100), viewportId = Text(80), scenarioId = Text(100) }
    };
    public static object Capture() => new
    {
        type = "object", additionalProperties = false,
        required = new[] { "captureId", "artifactPath", "sha256", "width", "height", "sourceRevision", "verificationRunId", "targetUrl", "viewportId", "capturedAt" },
        properties = new { captureId = Text(200), artifactPath = Text(4096), sha256 = Hash(), width = new { type = "integer", minimum = 1 }, height = new { type = "integer", minimum = 1 },
            sourceRevision = Hash(), verificationRunId = Text(100), targetUrl = Text(), viewportId = Text(80), capturedAt = new { type = "string", format = "date-time" } }
    };
}
