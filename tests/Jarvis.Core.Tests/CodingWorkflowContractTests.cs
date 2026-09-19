using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.DeveloperTools;
using Jarvis.Agent.Core.Autonomous.Verification;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class CodingWorkflowContractTests
{
    [Fact]
    public void Coding_contract_requires_trusted_adapter_acceptance_requirement_and_profile_coverage()
    {
        var spec = Spec();
        CodingVerificationRules.Validate(spec);
        Assert.Throws<ArgumentException>(() => CodingVerificationRules.Validate(spec with { Checks = [spec.Checks[0] with { ToolId = "filesystem.Read" }] }));
        Assert.Throws<ArgumentException>(() => CodingVerificationRules.Validate(spec with { Checks = [spec.Checks[0] with { Requirement = "" }] }));
        Assert.Throws<ArgumentException>(() => CodingVerificationRules.Validate(spec with { Checks = [spec.Checks[0] with { Required = false }] }));
        Assert.Throws<ArgumentException>(() => CodingVerificationRules.Validate(spec with
        {
            Profiles = ["backend"], Checks = [new() { Id = "nominal", Kind = "command", ToolId = "process.spawn", Requirement = "Print a marker", ExpectedText = "PASS",
                Arguments = WireJson.Element(new { argv = new[] { "echo", "PASS" } }) }]
        }));
        Assert.Throws<ArgumentException>(() => CodingVerificationRules.Validate(new() { Mode = "not_required" }));
        CodingVerificationRules.Validate(new() { Mode = "not_required", Reason = "Documentation-only change; no executable behavior changed." });
    }

    [Fact]
    public void Trusted_allowlist_is_bounded_and_service_readiness_requires_the_probe_adapter()
    {
        RemoteTaskRules.ValidateEnabledToolIds(["process.spawn", "developer.verify"]);
        Assert.Throws<ArgumentException>(() => RemoteTaskRules.ValidateEnabledToolIds(["process.spawn", "process.spawn"]));
        Assert.Throws<ArgumentException>(() => RemoteTaskRules.ValidateEnabledToolIds(["../forged"]));
        Assert.Throws<ArgumentException>(() => RemoteTaskRules.ValidateEnabledToolIds(Enumerable.Range(0, 513).Select(i => "tool." + i).ToArray()));
        var spec = Spec() with
        {
            Services = [new() { Id = "fixture", ReadyUrl = "http://localhost:4173/health",
                Step = new() { Id = "server", ToolId = "process.spawn", Arguments = WireJson.Element(new { argv = new[] { "fixture-server" } }) } }]
        };
        CodingVerificationRules.Validate(spec);
        Assert.Contains(CodingVerificationRules.ToolSteps(spec), step => step.ToolId == "developer.verify" &&
            step.Arguments.GetProperty("kind").GetString() == "http" && step.Arguments.GetProperty("url").GetString() == spec.Services[0].ReadyUrl);
    }

    [Fact]
    public async Task Generic_completion_requires_bound_required_receipts_and_reports_remain_owner_scoped()
    {
        await using var fixture = new CodingFixture();
        var page = await fixture.Host.HandleAsync("report", new("owner", fixture.Id, ArtifactId: fixture.Report.ArtifactId), CancellationToken.None);
        Assert.Null(page.Error);
        Assert.Equal(fixture.Report.Sha256, page.Report!.Sha256);
        Assert.Contains("\"assertions\"", page.Report.Text);
        var other = await fixture.Host.HandleAsync("report", new("other", fixture.Id, ArtifactId: fixture.Report.ArtifactId), CancellationToken.None);
        Assert.Equal("not_found", other.ErrorCode);
        var complete = await fixture.Host.HandleAsync("complete", new("owner", fixture.Id, AttemptId: Guid.NewGuid().ToString("N")), CancellationToken.None);
        Assert.Null(complete.Error);
        Assert.Equal("COMPLETED", complete.Task!.Status);
        Assert.True(complete.Task.CodingVerification!.Passed);
    }

    [Fact]
    public async Task Missing_required_receipt_cannot_be_laundered_by_a_passed_summary()
    {
        await using var fixture = new CodingFixture(missingCheck: true);
        var complete = await fixture.Host.HandleAsync("complete", new("owner", fixture.Id, AttemptId: Guid.NewGuid().ToString("N")), CancellationToken.None);
        Assert.Equal("conflict", complete.ErrorCode);
    }

    [Theory]
    [InlineData("not_run")]
    [InlineData("blocked")]
    [InlineData("not_supported")]
    [InlineData("invalid_report")]
    [InlineData("timed_out")]
    public async Task A_true_success_boolean_cannot_override_a_nonpassing_receipt_state(string state)
    {
        await using var fixture = new CodingFixture(receiptState: state);
        var complete = await fixture.Host.HandleAsync("complete", new("owner", fixture.Id, AttemptId: Guid.NewGuid().ToString("N")), CancellationToken.None);
        Assert.Equal("conflict", complete.ErrorCode);
    }

    [Fact]
    public async Task Exemption_cannot_clear_an_observed_failure_and_report_tampering_invalidates_completion()
    {
        await using var fixture = new CodingFixture(knownFailure: true);
        var skipped = await fixture.Host.HandleAsync("verify", new("owner", fixture.Id, AttemptId: Guid.NewGuid().ToString("N"),
            CodingVerification: new() { Mode = "not_required", Reason = "Skip the failing check." }), CancellationToken.None);
        Assert.Equal("invalid", skipped.ErrorCode);
        File.AppendAllText(fixture.Report.Path, "tampered");
        var complete = await fixture.Host.HandleAsync("complete", new("owner", fixture.Id, AttemptId: Guid.NewGuid().ToString("N")), CancellationToken.None);
        Assert.Equal("NEEDS_VERIFICATION", complete.Task!.Status);
        Assert.Equal("stale", complete.Task.CodingVerification!.State);
        Assert.False(complete.Task.CodingVerification.Passed);
    }

    [Fact]
    public async Task Event_cursor_marks_dropped_history_and_report_json_does_not_become_a_task_snapshot()
    {
        await using var fixture = new CodingFixture();
        var events = await fixture.Host.HandleAsync("events", new("owner", fixture.Id, Offset: 0, Limit: 1), CancellationToken.None);
        Assert.True(events.EventsTruncated);
        Assert.Equal(8, Assert.Single(events.Events!).Sequence);
        Assert.Equal(9, events.NextOffset);
        var rest = await fixture.Host.HandleAsync("events", new("owner", fixture.Id, Offset: 9), CancellationToken.None);
        Assert.False(rest.EventsTruncated);
        Assert.Equal("exit", Assert.Single(rest.Events!).Type);
        Assert.Equal(10, rest.NextOffset);
        Assert.Null((await fixture.Host.HandleAsync("get", new("owner", fixture.Id), CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Incomplete_retained_report_stays_marked_on_its_last_page()
    {
        await using var fixture = new CodingFixture(reportTruncated: true);
        var reply = await fixture.Host.HandleAsync("report", new("owner", fixture.Id, ArtifactId: fixture.Report.ArtifactId), CancellationToken.None);
        Assert.True(reply.Report!.Truncated);
        Assert.Null(reply.Report.NextOffset);
    }

    [Fact]
    public async Task Context_reads_only_scoped_instructions_and_manifests_through_guarded_read()
    {
        await using var fixture = new CodingFixture(enableContext: true);
        var context = await fixture.Host.HandleAsync("context", new("owner", fixture.Id, ContextPaths: ["src/module.cs"]), CancellationToken.None);
        Assert.Null(context.Error);
        Assert.Contains(context.Context!.Files, file => file.Path == Path.Combine(fixture.Workspace, "AGENTS.md"));
        Assert.Contains(context.Context.Files, file => file.Path == Path.Combine(fixture.Workspace, "src", "AGENTS.md"));
        Assert.DoesNotContain(context.Context.Files, file => file.Path.EndsWith("secrets.private.json", StringComparison.Ordinal));
        Assert.Equal(context.Context.Files.Count, fixture.ContextReads);
        var escaped = await fixture.Host.HandleAsync("context", new("owner", fixture.Id, ContextPaths: ["../outside"]), CancellationToken.None);
        Assert.Equal("invalid", escaped.ErrorCode);
    }

    private static CodingVerificationSpec Spec() => new()
    {
        Profiles = ["data"], Checks = [new() { Id = "schema", Kind = "json", ToolId = "developer.verify",
            Requirement = "The produced data has the required schema version.", Arguments = WireJson.Element(new
            { kind = "json", path = "result.json", assertions = new[] { new { path = "/version", op = "equals", expected = 1 } } }) }]
    };

    private sealed class CodingFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-coding-workflow-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Workspace { get; }
        public RemoteTaskHost Host { get; }
        public CodingReportArtifact Report { get; }
        public int ContextReads { get; private set; }
        public CodingFixture(bool missingCheck = false, bool knownFailure = false, bool enableContext = false, bool reportTruncated = false, string receiptState = "passed")
        {
            Workspace = Path.Combine(_root, "workspace"); Directory.CreateDirectory(Path.Combine(Workspace, "src"));
            File.WriteAllText(Path.Combine(Workspace, "AGENTS.md"), "Run focused checks for changed behavior.");
            File.WriteAllText(Path.Combine(Workspace, "src", "AGENTS.md"), "Use the project test fixture.");
            File.WriteAllText(Path.Combine(Workspace, "secrets.private.json"), "not context");
            File.WriteAllText(Path.Combine(Workspace, "result.json"), "{\"version\":1}");
            var source = FrontendWorkspaceState.Capture(Workspace);
            var runId = Guid.NewGuid().ToString("N");
            var plan = new RemoteTaskPlan { Goal = "Validate produced data", Project = Workspace, CodingVerification = Spec() };
            using (var store = new RemoteTaskStore(Path.Combine(_root, "tasks")))
            {
                var directory = store.EvidenceDirectory("owner", Id, runId);
                var path = Path.Combine(directory, "assertions.json");
                var measured = new DeveloperVerifyTool(Path.Combine(_root, "private-probes"))
                    .ExecuteAsync(plan.CodingVerification.Checks[0].Arguments, new(Workspace, "probe", "fixture"), CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert.False(measured.IsError, measured.Text);
                using var measuredResult = JsonDocument.Parse(measured.Text);
                var bytes = File.ReadAllBytes(measuredResult.RootElement.GetProperty("reportPath").GetString()!);
                File.WriteAllBytes(path, bytes);
                Report = new() { ArtifactId = runId + ":schema", Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), LengthBytes = bytes.Length, MediaType = "application/json", Truncated = reportTruncated };
                var check = plan.CodingVerification.Checks[0];
                var summary = new CodingVerificationSummary { State = "passed", Required = true, Passed = true, VerificationRunId = runId,
                    SourceBefore = source.Revision, SourceAfter = source.Revision, Checks = missingCheck ? [] :
                    [new() { CheckId = check.Id, State = receiptState, Kind = check.Kind, ToolId = check.ToolId, Requirement = check.Requirement,
                        ArgumentsDigest = RemoteTaskStore.Hash(check.Arguments.GetRawText()), WorkingDirectory = Workspace,
                        SourceBefore = source.Revision, SourceAfter = source.Revision, VerificationRunId = runId,
                        StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow, Required = true, Passed = true, Report = Report }] };
                var snapshot = new RemoteTaskSnapshot(Id, plan.Goal, Workspace, "READY_TO_COMPLETE", null, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                    { CodingVerification = summary };
                store.Save(new(1, "owner", RemoteTaskStore.Digest(plan), RemoteTaskStore.Digest(plan), plan, snapshot, [])
                {
                    SourceBaseline = source.Files, CodingEvidence = summary, KnownExecutionFailure = knownFailure,
                    Events = [new() { Sequence = 8, Type = "output", Text = "live output" }, new() { Sequence = 9, Type = "exit", ExitCode = 0 }], NextEventSequence = 10
                });
            }
            // Reopening also exercises that task-owned JSON reports are not mistaken for task snapshots.
            Host = new RemoteTaskHost(Path.Combine(_root, "tasks"), new WorkspaceDirectories(Workspace), new DynamicToolRegistry(enableContext ? [new ReadTool()] : []), () => true,
                (tool, arguments, _, _) =>
                {
                    Assert.Equal("filesystem.Read", tool); ContextReads++;
                    return Task.FromResult(new ToolReply(File.ReadAllText(arguments.GetProperty("file_path").GetString()!)));
                }, (_, _) => Task.CompletedTask);
        }
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); Directory.Delete(_root, true); }
    }

    private sealed class ReadTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("filesystem.Read", "filesystem__Read", "filesystem", "Guarded read fixture", WireJson.Element(new { type = "object" }), true);
        public Task<ToolReply> ExecuteAsync(System.Text.Json.JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolReply.Error("The fixture invokes its guarded delegate."));
    }
}
