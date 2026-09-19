using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Agent.Core.Autonomous.Verification;
using Jarvis.Agent.Core.DeveloperTools;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

internal sealed partial class RemoteTaskHost
{
    private static readonly HashSet<string> CodingExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".py", ".go", ".rs", ".java", ".kt", ".swift", ".c", ".cpp", ".h", ".hpp", ".m", ".mm",
        ".rb", ".php", ".sql", ".proto", ".sh", ".ps1", ".bat", ".cmd", ".js", ".ts", ".mjs", ".cjs",
        ".json", ".yaml", ".yml", ".toml", ".tf", ".csproj", ".fsproj", ".props", ".targets", ".csv", ".ipynb",
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg", ".pdf", ".docx", ".xlsx", ".pptx", ".parquet", ".onnx", ".wasm", ".zip"
    };
    private static readonly Regex CodingIntent = new(@"\b(backend|api|database|migration|worker|queue|cli|compiler|library|pipeline|infrastructure|machine learning)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool RequiresCodingQa(StoredRemoteTask task, FrontendWorkspaceState source, bool frontend)
    {
        if (task.Plan.CodingVerification is not null) return true;
        var changes = source.ChangedSince(task.SourceBaseline);
        if (!frontend && (CodingIntent.IsMatch(task.Plan.Goal) || changes.Any(path => CodingExtensions.Contains(Path.GetExtension(path))))) return true;
        return frontend && changes.Any(path => Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".py" or ".go" or ".rs" or ".java" or ".sql" or ".proto");
    }

    private async Task<(StoredRemoteTask Task, bool Success)> VerifyCodingGoalAsync(StoredRemoteTask task, Active active,
        FrontendWorkspaceState source, Func<AgentExecutionContext, Task>? beforeChecks = null)
    {
        var spec = task.Plan.CodingVerification;
        CodingVerificationSummary Summary(string state, string reason, bool required = true) => new()
        {
            State = state, Required = required, Passed = false, Reason = reason,
            SourceBefore = source.Complete ? source.Revision : null, SourceAfter = source.Complete ? source.Revision : null,
            NextAction = "Read agent_task_context, then supply codingVerification to agent_task_verify or submit new repair steps."
        };
        if (task.Plan.ExecutionMode == "READ_ONLY")
            return (SaveCoding(task, Summary("not_run", "Read-only execution did not run mutating coding checks."), null), true);
        if (spec?.Mode == "not_required")
        {
            if (task.KnownExecutionFailure || task.CodingEvidence?.Checks.Any(check => check.Required && !check.Passed) == true)
                return (SaveCoding(task, Summary("failed", "An exemption cannot override an observed failure."), "NEEDS_REPAIR"), false);
            return (SaveCoding(task, Summary("not_required", spec.Reason!, false) with { NextAction = null }, null), true);
        }
        if (spec is null || !source.Complete)
            return (SaveCoding(task, Summary("not_run", spec is null ? "Coding changes require an explicit acceptance specification." : source.Error!), "NEEDS_VERIFICATION"), false);

        CodingVerificationRules.Validate(spec);
        RequireArmed();
        var runId = Guid.NewGuid().ToString("N");
        var directory = _store.EvidenceDirectory(task.OwnerId, task.Snapshot.TaskId, runId);
        var receipts = new List<CodingCheckReceipt>();
        var services = new List<(string Id, string JobId, AgentExecutionContext Context)>();
        var context = active.Context with
        {
            CooperativeResourceGroup = task.Snapshot.TaskId + ":" + runId,
            SessionCancellation = active.Stop.Token
        };
        task = SaveCoding(task, Summary("running", "Collecting measured coding evidence.") with { VerificationRunId = runId }, "VERIFYING");
        Exception? infrastructureFailure = null;
        try
        {
            foreach (var service in spec.Services)
            {
                active.Stop.Token.ThrowIfCancellationRequested();
                await RequireUnusedEndpointAsync(service.ReadyUrl, active.Stop.Token);
                var serviceContext = context with { CallId = task.Snapshot.TaskId + ":service:" + service.Id + ":" + runId };
                ValidateCheckDirectory(task.Snapshot.Project, service.Step.Arguments);
                var started = await _invoke(service.Step.ToolId, service.Step.Arguments, serviceContext, active.Stop.Token);
                if (started.IsError) throw new InvalidOperationException("Fixture service failed to start: " + started.Text);
                using var startup = JsonDocument.Parse(started.Text);
                var jobId = startup.RootElement.GetProperty("jobId").GetString() ?? throw new InvalidDataException("Fixture did not return its owned job ID.");
                services.Add((service.Id, jobId, serviceContext));
                await EmitCodingEvent(task, new() { Type = "service.started", StepId = service.Id, ToolId = service.Step.ToolId, JobId = jobId });
                var deadline = DateTimeOffset.UtcNow.AddSeconds(service.ReadyTimeoutSeconds);
                var ready = false;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await ObserveServicesAsync(task, services, active.Stop.Token);
                    var probe = await _invoke("developer.verify", WireJson.Element(new { kind = "http", url = service.ReadyUrl, expectedStatus = 200 }),
                        serviceContext with { CallId = serviceContext.CallId + ":ready" }, active.Stop.Token);
                    if (!probe.IsError && ProbePassed(probe.Text)) { ready = true; break; }
                    await Task.Delay(150, active.Stop.Token);
                }
                if (!ready) throw new TimeoutException("Owned service readiness timed out: " + service.Id);
                await EmitCodingEvent(task, new() { Type = "service.ready", StepId = service.Id, JobId = jobId });
            }

            // Mixed FE/BE work uses the same owned fixture lifetime and guarded cooperative group.
            if (beforeChecks is not null) await beforeChecks(context);
            foreach (var check in spec.Checks)
            {
                active.Stop.Token.ThrowIfCancellationRequested();
                RequireArmed();
                await ObserveServicesAsync(task, services, active.Stop.Token);
                var before = FrontendWorkspaceState.Capture(task.Snapshot.Project, active.Stop.Token);
                if (!before.Complete || before.Revision != source.Revision)
                    throw new InvalidDataException("Source changed before the next verification check. Run verification again.");
                task = Save(task with { Snapshot = task.Snapshot with { CurrentStep = check.Id } });
                var receipt = await ExecuteCodingCheckAsync(task, check, context, runId, source.Revision, directory, active.Stop.Token);
                receipts.Add(receipt);
                await EmitCodingEvent(task, new() { Type = "check.completed", StepId = check.Id, ToolId = check.ToolId,
                    Text = receipt.Passed ? "Acceptance passed." : receipt.Error, ExitCode = receipt.ExitCode });
                // The next check may collect complementary diagnostics. A failed check is never erased by a later pass.
                task = SaveCoding(task, new() { State = "running", Required = true, Passed = false, VerificationRunId = runId,
                    SourceBefore = source.Revision, SourceAfter = receipt.SourceAfter, Checks = receipts.ToArray() }, "VERIFYING");
            }
            await ObserveServicesAsync(task, services, active.Stop.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { infrastructureFailure = ex; }
        finally
        {
            foreach (var service in services.AsEnumerable().Reverse())
            {
                try
                {
                    await _cancelJob(service.JobId, service.Context);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    while (true)
                    {
                        var reply = await _invoke("process.read", WireJson.Element(new { jobId = service.JobId }),
                            service.Context with { SessionCancellation = CancellationToken.None }, cleanup.Token);
                        if (reply.IsError) throw new InvalidOperationException("Unable to observe fixture shutdown: " + service.Id);
                        using var state = JsonDocument.Parse(reply.Text);
                        if (Bool(state.RootElement, "done")) break;
                        await Task.Delay(25, cleanup.Token);
                    }
                    await EmitCodingEvent(task, new() { Type = "service.stopped", StepId = service.Id, JobId = service.JobId });
                }
                catch (Exception ex) { infrastructureFailure ??= new InvalidOperationException("Owned service cleanup failed: " + service.Id, ex); }
            }
        }
        active.Stop.Token.ThrowIfCancellationRequested();
        var after = FrontendWorkspaceState.Capture(task.Snapshot.Project, active.Stop.Token);
        var stable = after.Complete && after.Revision == source.Revision && receipts.All(r => r.SourceBefore == source.Revision && r.SourceAfter == source.Revision);
        string? staleArtifact = null;
        try { ValidateCodingArtifactTargets(task with { CodingEvidence = new() { VerificationRunId = runId, Checks = receipts.ToArray() } }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or JsonException)
        { stable = false; staleArtifact = "A measured file/artifact changed before completion: " + ex.Message; }
        if (infrastructureFailure is not null)
            receipts.Add(new() { CheckId = "infrastructure", Kind = "service", ToolId = "agent.verification", Required = true,
                Requirement = "Owned fixture services must start, remain observable and be cleaned up.",
                ArgumentsDigest = RemoteTaskStore.Hash(JsonSerializer.Serialize(spec.Services, WireJson.Options)),
                VerificationRunId = runId, SourceBefore = source.Revision, SourceAfter = after.Complete ? after.Revision : null,
                WorkingDirectory = task.Snapshot.Project, StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
                Passed = false, State = "blocked", Error = ClipGoalText(infrastructureFailure.Message, 2000) });
        var passed = stable && infrastructureFailure is null && spec.Checks.Where(c => c.Required).All(c => receipts.Any(r => r.CheckId == c.Id && r.Passed && r.State == "passed"));
        var blocked = infrastructureFailure is not null || receipts.Any(r => r.Required && r.State is "blocked" or "not_supported" or "timed_out" or "unrecognized" or "invalid_report" or "not_run");
        var summary = new CodingVerificationSummary
        {
            Required = true, Passed = passed, State = !stable ? "stale" : blocked ? "blocked" : passed ? "passed" : "failed",
            Reason = !stable ? staleArtifact ?? "Source changed during QA or a complete revision could not be observed." : infrastructureFailure?.Message,
            VerificationRunId = runId, SourceBefore = source.Revision, SourceAfter = after.Complete ? after.Revision : null,
            Checks = receipts.ToArray(), NextAction = passed ? null : "Inspect agent_task_report/events, submit new repair steps, then rerun required checks."
        };
        task = SaveCoding(task with { KnownExecutionFailure = passed ? false : task.KnownExecutionFailure }, summary,
            passed ? null : stable && !blocked ? "NEEDS_REPAIR" : "NEEDS_VERIFICATION");
        return (task, passed);
    }

    private async Task<CodingCheckReceipt> ExecuteCodingCheckAsync(StoredRemoteTask task, CodingVerificationCheck check,
        AgentExecutionContext context, string runId, string revision, string directory, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var receipt = new CodingCheckReceipt { CheckId = check.Id, Kind = check.Kind, ToolId = check.ToolId, Requirement = check.Requirement,
            ArgumentsDigest = RemoteTaskStore.Hash(check.Arguments.GetRawText()), WorkingDirectory = CheckWorkingDirectory(task.Snapshot.Project, check.Arguments),
            Command = CheckCommandIdentity(check),
            SourceBefore = revision, VerificationRunId = runId, Required = check.Required, StartedAt = started };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(check.TimeoutSeconds));
        var call = context with
        {
            CallId = task.Snapshot.TaskId + ":check:" + check.Id + ":" + runId,
            ReportOutput = (stream, text) => EmitCodingEvent(task, new() { Type = "check.output", StepId = check.Id, ToolId = check.ToolId, Stream = stream, Text = text })
        };
        try
        {
            ValidateCheckDirectory(task.Snapshot.Project, check.Arguments);
            await EmitCodingEvent(task, new() { Type = "check.started", StepId = check.Id, ToolId = check.ToolId });
            if (check.Kind == "command")
            {
                var outputPath = Path.Combine(directory, check.Id + ".log");
                var result = await RemoteProcessRunner.RunAsync(new() { Id = check.Id, ToolId = check.ToolId,
                    Arguments = check.Arguments, TimeoutSeconds = check.TimeoutSeconds }, call, _invoke, _cancelJob, stop.Token,
                    e => EmitCodingEvent(task, e), outputPath);
                var expectedText = check.ExpectedText is null || result.Output.Contains(check.ExpectedText, StringComparison.Ordinal);
                var passed = result.KnownCompletion && result.ExitCode == check.ExpectedExitCode && expectedText;
                receipt = receipt with { Passed = passed, State = passed ? "passed" : result.KnownCompletion ? "failed" : "blocked", ExitCode = result.ExitCode, Output = ClipGoalText(result.Output, 4000),
                    OutputTruncated = result.Truncated || result.Output.Length > 4000,
                    Error = passed ? null : !expectedText ? "Required command output was not observed." : result.Error ?? "Command exit did not match acceptance.",
                    Report = File.Exists(outputPath) ? DescribeReport(outputPath, runId + ":" + check.Id, "text/plain") with { Truncated = result.RetainedOutputTruncated } : null };
            }
            else
            {
                var reply = await _invoke(check.ToolId, check.Arguments, call, stop.Token);
                using var document = JsonDocument.Parse(reply.Text);
                var root = document.RootElement;
                var reportPath = String(root, "reportPath");
                var reportHash = String(root, "reportSha256");
                if (check.Kind == "test")
                {
                    var exit = Int(root, "exitCode");
                    if (reportPath.Length == 0)
                    {
                        var state = String(root, "state");
                        if (state is not ("failed" or "not_run" or "unrecognized" or "invalid_report" or "blocked" or "timed_out")) state = "invalid_report";
                        receipt = receipt with { Passed = false, State = state, ExitCode = exit,
                            Report = CaptureTestDiagnostics(root, directory, check.Id, runId),
                            Output = ClipGoalText(String(root, "output"), 4000), OutputTruncated = Bool(root, "truncated") || String(root, "output").Length > 4000,
                            Error = "Test report unavailable: " + JsonSummary(root, "diagnostics") };
                    }
                    else
                    {
                        var report = CaptureCodingReport(reportPath, reportHash, directory, check.Id, runId);
                        var parsed = StructuredTestReportParser.Parse(String(root, "reportFormat"), File.ReadAllText(report.Path), exit);
                        var counts = new CodingTestCounts(parsed.Total, parsed.Passed, parsed.Failed, parsed.Skipped, parsed.Executed);
                        var passed = !reply.IsError && String(root, "state") == "passed" && parsed.State == "passed" && parsed.ReportParsed && parsed.Executed >= (check.MinimumTests ?? 1) && parsed.Failed == 0 && exit == 0;
                        receipt = receipt with { Passed = passed, State = passed ? "passed" : parsed.State == "passed" ? "failed" : parsed.State,
                            ExitCode = exit, TestCounts = counts, Report = report, Output = ClipGoalText(String(root, "output"), 4000),
                            OutputTruncated = Bool(root, "truncated") || String(root, "output").Length > 4000,
                            Error = passed ? null : "Test acceptance failed: " + parsed.State };
                    }
                }
                else
                {
                    var report = CaptureCodingReport(reportPath, reportHash, directory, check.Id, runId);
                    var passed = !reply.IsError && ProbePassed(reply.Text) && String(root, "kind") == check.Kind;
                    var state = String(root, "status");
                    receipt = receipt with { Passed = passed, State = passed ? "passed" : state is "blocked" or "not_supported" ? state : "failed",
                        Report = report, Output = ClipGoalText(reply.Text, 4000), OutputTruncated = reply.Text.Length > 4000,
                        Error = passed ? null : "Measured " + check.Kind + " acceptance failed: " + String(root, "status") };
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { receipt = receipt with { Passed = false, State = "timed_out", Error = "Verification check timed out; no mutation was replayed." }; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        { receipt = receipt with { Passed = false, State = "blocked", Error = ClipGoalText(ex.Message, 2000) }; }
        var after = FrontendWorkspaceState.Capture(task.Snapshot.Project, cancellationToken);
        return receipt with { SourceAfter = after.Complete ? after.Revision : null, FinishedAt = DateTimeOffset.UtcNow,
            Passed = receipt.Passed && after.Complete && after.Revision == revision,
            State = !after.Complete || after.Revision != revision ? "stale" : receipt.State,
            Error = !after.Complete || after.Revision != revision ? "Source changed during the check; evidence is stale." : receipt.Error };
    }

    private StoredRemoteTask SaveCoding(StoredRemoteTask task, CodingVerificationSummary summary, string? status) => Save(task with
    {
        CodingEvidence = summary,
        Snapshot = task.Snapshot with { CodingVerification = summary, Status = status ?? task.Snapshot.Status,
            CurrentStep = status is "VERIFYING" ? task.Snapshot.CurrentStep : null,
            Error = summary.Passed || summary.State == "not_required" ? null : summary.Reason,
            UpdatedAt = DateTimeOffset.UtcNow }
    });

    private Task EmitCodingEvent(StoredRemoteTask task, RemoteTaskEvent item)
    {
        AppendTaskEvent(task.OwnerId, task.Snapshot.TaskId, item);
        return Task.CompletedTask;
    }

    private async Task ObserveServicesAsync(StoredRemoteTask task, IEnumerable<(string Id, string JobId, AgentExecutionContext Context)> services, CancellationToken ct)
    {
        foreach (var service in services)
        {
            var reply = await _invoke("process.read", WireJson.Element(new { jobId = service.JobId }), service.Context, ct);
            if (reply.IsError) throw new InvalidOperationException("Cannot observe owned fixture service: " + service.Id);
            using var json = JsonDocument.Parse(reply.Text);
            if (Bool(json.RootElement, "done")) throw new InvalidOperationException("Owned fixture service exited before verification completed: " + service.Id);
        }
    }

    private static async Task RequireUnusedEndpointAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        using var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try { await client.ConnectAsync(uri.Host, uri.Port, deadline.Token); }
        catch (SocketException) { return; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
        throw new InvalidOperationException("Owned fixture endpoint is already in use; select a free loopback port.");
    }

    private static bool ProbePassed(string text)
    {
        try
        {
            using var data = JsonDocument.Parse(text);
            var root = data.RootElement;
            return Int(root, "schemaVersion") == 1 && Bool(root, "passed") && String(root, "status") == "passed" &&
                root.TryGetProperty("assertions", out var assertions) && assertions.ValueKind == JsonValueKind.Array &&
                assertions.GetArrayLength() > 0 && assertions.EnumerateArray().All(a => Bool(a, "passed"));
        }
        catch (JsonException) { return false; }
    }

    private static void ValidateCheckDirectory(string project, JsonElement arguments)
    {
        foreach (var field in new[] { "workingDirectory", "project" })
        {
            var value = String(arguments, field);
            if (value.Length == 0) continue;
            var path = Path.GetFullPath(value, project);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.Equals(root, comparison) && !path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException("Verification commands must run inside the task's source-revision scope.");
        }
    }

    private static string CheckWorkingDirectory(string project, JsonElement arguments)
    {
        var requested = String(arguments, "workingDirectory");
        if (requested.Length == 0) requested = String(arguments, "project");
        var resolved = requested.Length == 0 ? Path.GetFullPath(project) : Path.GetFullPath(requested, project);
        return File.Exists(resolved) ? Path.GetDirectoryName(resolved)! : resolved;
    }

    private static string CheckCommandIdentity(CodingVerificationCheck check)
    {
        if (check.Arguments.TryGetProperty("argv", out var argv) && argv.ValueKind == JsonValueKind.Array && argv.GetArrayLength() > 0)
            return Path.GetFileName(argv[0].GetString() ?? "") + " (" + (argv.GetArrayLength() - 1) + " argv parameters; values bound by argumentsDigest)";
        return check.ToolId == "process.start" ? "shell command (content bound by argumentsDigest)" : check.ToolId;
    }

    private static CodingReportArtifact CaptureCodingReport(string path, string expectedHash, string directory, string checkId, string runId)
    {
        var original = DescribeReport(path, runId + ":" + checkId, "text/plain");
        if (!string.Equals(original.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Verification report hash mismatch.");
        var target = Path.Combine(directory, checkId + ".report");
        File.Copy(path, target, overwrite: false);
        var captured = DescribeReport(target, original.ArtifactId, original.MediaType);
        if (captured.Sha256 != original.Sha256) throw new InvalidDataException("Verification report changed during capture.");
        return captured;
    }

    private static CodingReportArtifact CaptureTestDiagnostics(JsonElement result, string directory, string checkId, string runId)
    {
        var contents = new StringBuilder("No fresh machine-readable report was accepted.\n");
        contents.AppendLine(JsonSummary(result, "diagnostics"));
        var truncated = Bool(result, "truncated");
        foreach (var stream in new[] { "stdout", "stderr" })
        {
            var path = String(result, stream + "Path");
            if (path.Length == 0) continue;
            var artifact = DescribeReport(path, runId + ":" + checkId, "text/plain");
            if (!artifact.Sha256.Equals(String(result, stream + "Sha256"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Diagnostic log hash mismatch.");
            var text = File.ReadAllText(path);
            const int maximumChars = 900000;
            var remaining = Math.Max(0, maximumChars - contents.Length);
            contents.AppendLine("\n--- " + stream + " ---");
            contents.Append(text.AsSpan(0, Math.Min(remaining, text.Length)));
            truncated |= text.Length > remaining;
        }
        var destination = Path.Combine(directory, checkId + ".report");
        File.WriteAllText(destination, contents.ToString(), new UTF8Encoding(false));
        return DescribeReport(destination, runId + ":" + checkId, "text/plain") with { Truncated = truncated };
    }

    private static CodingReportArtifact DescribeReport(string path, string id, string mediaType)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Verification report path must be absolute.");
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Linked verification reports are not accepted.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > CodingVerificationRules.MaxReportBytes) throw new InvalidDataException("Verification report exceeds the evidence limit.");
        return new() { ArtifactId = id, Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(stream)), MediaType = mediaType, LengthBytes = stream.Length };
    }

    private void ValidateCodingArtifactTargets(StoredRemoteTask task)
    {
        var latest = new Dictionary<string, (string? Hash, bool? Exists)>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var check in task.CodingEvidence?.Checks ?? [])
        {
            if (!check.Required || !check.Passed || check.Kind is not ("file" or "json")) continue;
            if (check.Report is null) throw new InvalidDataException("Missing measured artifact report.");
            using var report = JsonDocument.Parse(ReadReportBytes(task, check.Report));
            var root = report.RootElement;
            if (!ProbePassed(root.GetRawText()) || !root.TryGetProperty("observations", out var observations))
                throw new InvalidDataException("Artifact report does not contain passing measured observations.");
            var path = String(observations, "path");
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Observed artifact path is not absolute.");
            var hash = String(observations, "inputSha256");
            bool? exists = null;
            foreach (var assertion in root.GetProperty("assertions").EnumerateArray())
                if (String(assertion, "id") == "file:exists") exists = Bool(assertion, "expected");
            if (hash.Length == 0 && exists is null) throw new InvalidDataException("Artifact report is missing an observable hash or existence assertion.");
            latest[path] = (hash.Length == 0 ? null : hash, exists);
        }
        foreach (var (path, expected) in latest)
        {
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Observed artifact now contains a linked path.");
            if (expected.Exists is { } exists && File.Exists(path) != exists) throw new InvalidDataException("Observed artifact existence changed.");
            if (expected.Hash is null) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 128 * 1024 * 1024 || !Convert.ToHexString(SHA256.HashData(stream)).Equals(expected.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Observed artifact bytes changed.");
        }
    }
}
