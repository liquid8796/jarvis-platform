namespace Jarvis.Protocol;

public static class CodingVerificationSchemas
{
    private static object Text(int max = 4000, bool nonEmpty = true) => new { type = "string", minLength = nonEmpty ? 1 : 0, maxLength = max };
    private static object Int(int min = 0, int max = int.MaxValue) => new { type = "integer", minimum = min, maximum = max };
    private static object Bool() => new { type = "boolean" };
    private static object Hash() => FrontendQaSchemas.Hash();
    private static object Time() => new { type = "string", format = "date-time" };
    private static object Object(Dictionary<string, object> properties, params string[] required) => new { type = "object", properties, required, additionalProperties = false };
    private static object Array(object items, int max = 24, int min = 0) => new { type = "array", items, minItems = min, maxItems = max };

    public static object Spec() => Object(new()
    {
        ["mode"] = new { type = "string", @enum = new[] { "required", "not_required" }, @default = "required" },
        ["reason"] = Text(), ["profiles"] = Array(new { type = "string", @enum = CodingVerificationRules.Profiles }, CodingVerificationRules.Profiles.Length),
        ["checks"] = Array(Object(new()
        {
            ["id"] = Text(100), ["kind"] = new { type = "string", @enum = CodingVerificationRules.Kinds }, ["toolId"] = Text(100), ["requirement"] = Text(2000),
            ["arguments"] = new { type = "object" }, ["required"] = Bool(), ["timeoutSeconds"] = Int(1, 1800),
            ["expectedExitCode"] = new { type = "integer" }, ["expectedText"] = Text(1000), ["minimumTests"] = Int(1, 10000000)
        }, "id", "kind", "toolId", "requirement", "arguments")),
        ["services"] = Array(Object(new()
        {
            ["id"] = Text(100), ["step"] = Step(), ["readyUrl"] = Text(2048), ["readyTimeoutSeconds"] = Int(1, 120)
        }, "id", "step", "readyUrl"), 4)
    }, "mode");

    public static object Step() => Object(new()
    {
        ["id"] = Text(100), ["toolId"] = Text(100), ["arguments"] = new { type = "object" },
        ["stage"] = new { type = "string", @enum = new[] { "EXECUTE", "BUILD", "TEST", "PACKAGE", "VERIFY" } },
        ["timeoutSeconds"] = Int(1, 1800), ["maxAttempts"] = Int(1, 3), ["expectedText"] = Text(1000)
    }, "id", "toolId");

    public static object Summary() => Object(new()
    {
        ["state"] = new { type = "string", @enum = new[] { "not_run", "running", "blocked", "failed", "stale", "passed", "not_required" } },
        ["required"] = Bool(), ["passed"] = Bool(), ["reason"] = Text(), ["verificationRunId"] = Text(100),
        ["sourceBefore"] = Hash(), ["sourceAfter"] = Hash(), ["checks"] = Array(Receipt(), CodingVerificationRules.MaxChecks + 1), ["nextAction"] = Text()
    }, "state", "required", "passed", "checks");

    public static object Receipt() => Object(new()
    {
        ["checkId"] = Text(100), ["state"] = new { type = "string", @enum = new[] { "passed", "failed", "blocked", "not_supported", "not_run", "unrecognized", "invalid_report", "stale", "timed_out" } },
        ["requirement"] = Text(2000, false), ["kind"] = Text(100), ["toolId"] = Text(100), ["argumentsDigest"] = Hash(),
        ["command"] = Text(16000), ["workingDirectory"] = Text(4096), ["sourceBefore"] = Hash(), ["sourceAfter"] = Hash(),
        ["verificationRunId"] = Text(100), ["startedAt"] = Time(), ["finishedAt"] = Time(), ["passed"] = Bool(), ["required"] = Bool(),
        ["exitCode"] = new { type = "integer" }, ["output"] = Text(16000, false), ["outputTruncated"] = Bool(), ["error"] = Text(), ["testCounts"] = Object(new()
        { ["total"] = Int(), ["passed"] = Int(), ["failed"] = Int(), ["skipped"] = Int(), ["executed"] = Int() }, "total", "passed", "failed", "skipped", "executed"),
        ["report"] = ReportArtifact()
    }, "checkId", "state", "kind", "toolId", "argumentsDigest", "workingDirectory", "sourceBefore", "verificationRunId", "startedAt", "finishedAt", "passed", "required");

    public static object ReportArtifact() => Object(new()
    {
        ["artifactId"] = Text(240), ["path"] = Text(4096), ["sha256"] = Hash(), ["mediaType"] = Text(100), ["lengthBytes"] = new { type = "integer", minimum = 0L }, ["truncated"] = Bool()
    }, "artifactId", "path", "sha256", "mediaType");

    public static object ReportContent() => Object(new()
    {
        ["artifactId"] = Text(240), ["mediaType"] = Text(100), ["sha256"] = Hash(), ["offset"] = Int(),
        ["text"] = Text(CodingVerificationRules.ReportPageChars, false), ["truncated"] = Bool(), ["nextOffset"] = Int()
    }, "artifactId", "mediaType", "sha256", "offset", "text", "truncated");

    public static object Event() => Object(new()
    {
        ["sequence"] = Int(), ["type"] = Text(100), ["at"] = Time(), ["stepId"] = Text(100), ["toolId"] = Text(100),
        ["stream"] = Text(30), ["text"] = Text(CodingVerificationRules.EventTextLimit, false), ["jobId"] = Text(100),
        ["cursor"] = new { type = "integer", minimum = 0L }, ["exitCode"] = new { type = "integer" }, ["truncated"] = Bool()
    }, "sequence", "type", "at");

    public static object Context() => Object(new()
    {
        ["project"] = Text(4096), ["digest"] = Hash(), ["files"] = Array(Object(new()
        { ["path"] = Text(4096), ["kind"] = Text(100), ["sha256"] = Hash(), ["content"] = Text(16000, false), ["truncated"] = Bool() },
            "path", "kind", "sha256", "content", "truncated"), 20),
        ["hints"] = Array(Text(), 20), ["warnings"] = Array(Text(), 30)
    }, "project", "digest", "files", "hints", "warnings");
}
