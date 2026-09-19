using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jarvis.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CodingVerificationSpec
{
    public string Mode { get; init; } = "required";
    public string? Reason { get; init; }
    public IReadOnlyList<string> Profiles { get; init; } = [];
    public IReadOnlyList<CodingVerificationCheck> Checks { get; init; } = [];
    public IReadOnlyList<CodingVerificationService> Services { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CodingVerificationCheck
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "command";
    public string ToolId { get; init; } = "";
    public string Requirement { get; init; } = "";
    public JsonElement Arguments { get; init; } = WireJson.Element(new { });
    public bool Required { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 120;
    public int ExpectedExitCode { get; init; }
    public string? ExpectedText { get; init; }
    public int? MinimumTests { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CodingVerificationService
{
    public string Id { get; init; } = "";
    public RemoteTaskStep Step { get; init; } = new();
    public string ReadyUrl { get; init; } = "";
    public int ReadyTimeoutSeconds { get; init; } = 30;
}

public sealed record CodingVerificationSummary
{
    public string State { get; init; } = "not_run";
    public bool Required { get; init; }
    public bool Passed { get; init; }
    public string? Reason { get; init; }
    public string? VerificationRunId { get; init; }
    public string? SourceBefore { get; init; }
    public string? SourceAfter { get; init; }
    public IReadOnlyList<CodingCheckReceipt> Checks { get; init; } = [];
    public string? NextAction { get; init; }
}

public sealed record CodingCheckReceipt
{
    public string CheckId { get; init; } = "";
    public string State { get; init; } = "not_run";
    public string Requirement { get; init; } = "";
    public string Kind { get; init; } = "";
    public string ToolId { get; init; } = "";
    public string ArgumentsDigest { get; init; } = "";
    public string? Command { get; init; }
    public string WorkingDirectory { get; init; } = "";
    public string SourceBefore { get; init; } = "";
    public string? SourceAfter { get; init; }
    public string VerificationRunId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public bool Passed { get; init; }
    public bool Required { get; init; } = true;
    public int? ExitCode { get; init; }
    public string? Output { get; init; }
    public bool OutputTruncated { get; init; }
    public string? Error { get; init; }
    public CodingTestCounts? TestCounts { get; init; }
    public CodingReportArtifact? Report { get; init; }
}

public sealed record CodingTestCounts(int Total, int Passed, int Failed, int Skipped, int Executed);
public sealed record CodingReportArtifact
{
    public string ArtifactId { get; init; } = "";
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string MediaType { get; init; } = "text/plain";
    public long? LengthBytes { get; init; }
    public bool Truncated { get; init; }
}

public sealed record CodingReportContent(string ArtifactId, string MediaType, string Sha256, int Offset,
    string Text, bool Truncated, int? NextOffset = null);

public sealed record RemoteTaskEvent
{
    public int Sequence { get; init; }
    public string Type { get; init; } = "";
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string? StepId { get; init; }
    public string? ToolId { get; init; }
    public string? Stream { get; init; }
    public string? Text { get; init; }
    public string? JobId { get; init; }
    public long? Cursor { get; init; }
    public int? ExitCode { get; init; }
    public bool Truncated { get; init; }
}

public sealed record CodingProjectContext(string Project, string Digest, IReadOnlyList<CodingProjectContextFile> Files,
    IReadOnlyList<string> Hints, IReadOnlyList<string> Warnings);
public sealed record CodingProjectContextFile(string Path, string Kind, string Sha256, string Content, bool Truncated);

public static partial class CodingVerificationRules
{
    public static readonly string[] Profiles = ["backend", "api", "database", "worker", "cli", "native", "desktop", "data", "ml", "infra", "security", "performance"];
    public static readonly string[] Kinds = ["test", "http", "json", "file", "sqlite", "command"];
    public const int MaxChecks = 24;
    public const int MaxEvents = 256;
    public const int EventTextLimit = 2000;
    public const int ReportPageChars = 16000;
    public const int MaxReportBytes = 8 * 1024 * 1024;
    public const int MaxRepairRounds = 3;
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    public static void Validate(CodingVerificationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Mode is not ("required" or "not_required") || spec.Reason?.Length > 4000)
            throw new ArgumentException("Coding verification mode must be required or not_required.");
        if (spec.Profiles is null || spec.Profiles.Count > Profiles.Length || spec.Profiles.Distinct(StringComparer.Ordinal).Count() != spec.Profiles.Count ||
            spec.Profiles.Any(profile => !Profiles.Contains(profile, StringComparer.Ordinal)))
            throw new ArgumentException("Coding verification profiles must be unique supported profile names.");
        if (spec.Checks is null || spec.Checks.Count > MaxChecks || spec.Services is null || spec.Services.Count > 4)
            throw new ArgumentException("Coding verification supports at most 24 checks and 4 owned services.");
        if (spec.Mode == "not_required")
        {
            if (string.IsNullOrWhiteSpace(spec.Reason) || spec.Checks.Count != 0 || spec.Services.Count != 0)
                throw new ArgumentException("A verification exemption requires an explicit reason and cannot contain executable checks or services.");
            return;
        }
        if (spec.Profiles.Count == 0 || spec.Checks.Count == 0 || !spec.Checks.Any(check => check is not null && check.Required))
            throw new ArgumentException("Required coding verification needs a profile and at least one required check.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in spec.Checks)
        {
            if (check is null || !ValidId(check.Id) || !ids.Add(check.Id) || !Kinds.Contains(check.Kind, StringComparer.Ordinal) ||
                !ValidId(check.ToolId) || check.Arguments.ValueKind != JsonValueKind.Object || check.TimeoutSeconds is < 1 or > 1800 ||
                string.IsNullOrWhiteSpace(check.Requirement) || check.Requirement.Length > 2000 || check.ExpectedText?.Length > 1000 || check.MinimumTests is < 1 or > 10000000)
                throw new ArgumentException("Invalid coding check identity, kind, arguments or bounds.");
            if (check.Kind == "test" && check.ToolId != "developer.test" ||
                check.Kind == "command" && check.ToolId is not ("process.start" or "process.spawn") ||
                check.Kind is "http" or "json" or "file" or "sqlite" && (check.ToolId != "developer.verify" ||
                    !check.Arguments.TryGetProperty("kind", out var probeKind) || probeKind.ValueKind != JsonValueKind.String || probeKind.GetString() != check.Kind))
                throw new ArgumentException("Check kinds must use their trusted execution adapter: developer.test, developer.verify, or process.start/spawn.");
            if (check.MinimumTests is not null && check.Kind != "test") throw new ArgumentException("minimumTests only applies to parsed test reports.");
            if (check.Kind == "command" && check.ExpectedExitCode == 0 && string.IsNullOrWhiteSpace(check.ExpectedText))
                throw new ArgumentException("A command check needs an explicit expected output or an intentional nonzero expected exit code.");
            if (check.Kind == "test" && check.ExpectedExitCode != 0)
                throw new ArgumentException("A test check cannot treat a failing test-runner exit code as success.");
        }
        var requiredKinds = spec.Checks.Where(check => check.Required).Select(check => check.Kind).ToHashSet(StringComparer.Ordinal);
        foreach (var profile in spec.Profiles)
        {
            var supported = profile switch
            {
                "backend" or "worker" => requiredKinds.Overlaps(["test", "http", "sqlite"]),
                "api" => requiredKinds.Overlaps(["http", "test"]),
                "database" => requiredKinds.Overlaps(["sqlite", "test"]),
                "data" or "ml" => requiredKinds.Overlaps(["json", "test"]),
                "security" or "performance" => requiredKinds.Overlaps(["test", "http", "json", "sqlite"]),
                "cli" => requiredKinds.Overlaps(["test", "command"]),
                "infra" => requiredKinds.Overlaps(["test", "http", "json", "file"]),
                "native" => requiredKinds.Contains("test") || requiredKinds.Contains("command") && requiredKinds.Contains("file"),
                "desktop" => requiredKinds.Contains("test"),
                _ => false
            };
            if (!supported) throw new ArgumentException($"Profile '{profile}' requires a corresponding test or domain assertion; a nominal command alone is insufficient.");
        }
        foreach (var service in spec.Services)
        {
            if (service is null || !ValidId(service.Id) || !ids.Add(service.Id) || service.Step is null ||
                service.Step.ToolId != "process.spawn" || service.Step.MaxAttempts != 1 ||
                !Uri.TryCreate(service.ReadyUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || service.ReadyUrl.Length > 2048 || service.ReadyTimeoutSeconds is < 1 or > 120)
                throw new ArgumentException("Owned QA services require a single argv process.spawn and a loopback readiness URL.");
            RemoteTaskRules.Validate(new RemoteTaskPlan { Goal = "Owned verification service", Steps = [service.Step] });
        }
        if (JsonSerializer.SerializeToUtf8Bytes(spec, WireJson.Options).Length > 96 * 1024)
            throw new ArgumentException("Coding verification specification exceeds 96 KiB.");
    }

    private static bool ValidId(string? value) => value is not null && Identifier().IsMatch(value);

    public static IEnumerable<RemoteTaskStep> ToolSteps(CodingVerificationSpec? spec)
    {
        if (spec is null || spec.Mode != "required") yield break;
        foreach (var service in spec.Services)
        {
            yield return service.Step;
            yield return new RemoteTaskStep { Id = (service.Id.Length > 94 ? service.Id[..94] : service.Id) + ".ready",
                ToolId = "developer.verify", Arguments = WireJson.Element(new { kind = "http", url = service.ReadyUrl, expectedStatus = 200 }),
                Stage = "VERIFY", TimeoutSeconds = service.ReadyTimeoutSeconds };
        }
        foreach (var check in spec.Checks)
            yield return new RemoteTaskStep { Id = check.Id, ToolId = check.ToolId, Arguments = check.Arguments,
                Stage = "VERIFY", TimeoutSeconds = check.TimeoutSeconds, ExpectedText = check.ExpectedText };
    }
}
