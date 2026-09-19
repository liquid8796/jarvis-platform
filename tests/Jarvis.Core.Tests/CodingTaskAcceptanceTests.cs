using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.DeveloperTools;
using Jarvis.Agent.Core.Execution;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

/// <summary>
/// Production task host, schemas, resource leases, process jobs, probes and test runner.
/// This does not replace the separate server/AgentConnection authentication acceptance layer.
/// </summary>
public sealed class CodingTaskAcceptanceTests
{
    [Fact]
    public async Task Mixed_frontend_boundary_observes_live_owned_service_and_shares_its_resource_group()
    {
        // The FE tool is deliberately synthetic and returns no rendering evidence. This tests only
        // mixed lifetime/context composition; the separate live browser corpus proves rendered QA.
        var port = FreePort(); var origin = "http://127.0.0.1:" + port;
        var browserBoundary = new FrontendLifetimeBoundary(origin);
        await using var fixture = new Fixture(initiallyCorrect: true, extra: browserBoundary);
        var coding = new CodingVerificationSpec
        {
            Profiles = ["backend", "api"],
            Services = [new() { Id = "mixed-api", ReadyUrl = origin + "/health", ReadyTimeoutSeconds = 10,
                Step = fixture.Step("mixed-service", "serve", port.ToString()) }],
            Checks = [Probe("actual-value", "http", "The actual owned API responds with the required value after the frontend boundary.",
                new { kind = "http", url = origin + "/value", expectedStatus = 200,
                    assertions = new[] { new { path = "/value", op = "equals", expected = 42 } } })]
        };
        var plan = fixture.Plan("Check frontend and backend fixture lifetime", coding) with
        {
            VerificationSpec = new()
            {
                Url = origin + "/value", RequireVisualReview = false,
                Viewports = [new() { Name = "desktop", Width = 1440, Height = 900 }],
                Steps = [new() { Action = "click", Locator = new() { Role = "button", Name = "Boundary only" },
                    Expect = new() { Kind = "visible", Locator = new() { Css = "body" } } }]
            }
        };
        var snapshot = await fixture.Create(Guid.NewGuid().ToString("N"), plan);
        Assert.Equal(1, browserBoundary.Calls);
        Assert.False(string.IsNullOrWhiteSpace(browserBoundary.ResourceGroup));
        Assert.True(browserBoundary.ObservedLiveHealth);
        Assert.True(snapshot.CodingVerification!.Passed);
        Assert.Contains(snapshot.CodingVerification.Checks, check => check.CheckId == "actual-value" && check.Passed);
        Assert.Equal("NEEDS_REPAIR", snapshot.Status); // The synthetic FE boundary must never certify rendering.
        Assert.False(snapshot.Verification!.Passed);
        await fixture.AssertNoProcessesOrListener(port);
    }

    private sealed class FrontendLifetimeBoundary(string origin) : IAgentTool
    {
        public int Calls;
        public string? ResourceGroup;
        public bool ObservedLiveHealth;
        public ToolDescriptor Descriptor { get; } = new("browser.qa", "browser__qa", "browser", "Synthetic mixed-lifetime boundary; no rendered acceptance.",
            WireJson.Element(new { type = "object", properties = new { spec = new { type = "object" }, browserFamily = new { type = "string" } },
                required = new[] { "spec" }, additionalProperties = false }), false);
        public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            ResourceGroup = context.CooperativeResourceGroup;
            Assert.False(string.IsNullOrWhiteSpace(ResourceGroup));
            using var http = new HttpClient();
            using var response = await http.GetAsync(origin + "/health", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            ObservedLiveHealth = health.RootElement.GetProperty("ready").GetBoolean();
            return new ToolReply(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, passed = false, cleanedUp = true, snapshots = Array.Empty<object>(),
                errors = new[] { "Synthetic lifetime boundary intentionally supplies no rendered frontend evidence." }
            }, WireJson.Options));
        }
    }

    [Fact]
    public async Task Real_api_and_sqlite_failures_repair_and_retest_in_the_same_task_without_replay()
    {
        await using var fixture = new Fixture();
        var port = FreePort(); var origin = "http://127.0.0.1:" + port;
        var spec = new CodingVerificationSpec
        {
            Profiles = ["backend", "api", "database", "worker", "security"],
            Services = [new() { Id = "api-service", ReadyUrl = origin + "/health", ReadyTimeoutSeconds = 10,
                Step = fixture.Step("service", "serve", port.ToString()) }],
            Checks =
            [
                Probe("value", "http", "GET /value returns the required business value, not merely HTTP 200.",
                    new { kind = "http", url = origin + "/value", expectedStatus = 200, assertions = new[] { new { path = "/value", op = "equals", expected = 42 } } }),
                Probe("auth", "http", "A request without a credential cannot read authorized data.",
                    new { kind = "http", url = origin + "/auth", expectedStatus = 401, assertions = new[] { new { path = "/error", op = "equals", expected = "unauthorized" } } }),
                Probe("enqueue-first", "http", "First queue submission is accepted.",
                    new { kind = "http", url = origin + "/enqueue", method = "POST", body = new { key = "same-request" }, expectedStatus = 200,
                        assertions = new[] { new { path = "/accepted", op = "equals", expected = true } } }),
                Probe("enqueue-retry", "http", "Retrying the same request is accepted without duplicating persisted work.",
                    new { kind = "http", url = origin + "/enqueue", method = "POST", body = new { key = "same-request" }, expectedStatus = 200,
                        assertions = new[] { new { path = "/accepted", op = "equals", expected = true } } }),
                Probe("persisted-idempotency", "sqlite", "Two identical queue submissions persist exactly one row.",
                    new { kind = "sqlite", path = ".jarvis-qa/jobs.db", query = "SELECT count(*) AS count FROM jobs",
                        assertions = new[] { new { path = "/rows/0/count", op = "equals", expected = 1 } } })
            ]
        };
        var plan = fixture.Plan("Repair backend API and queue idempotency", spec, fixture.Step("inspect", "cli"));
        var taskId = Guid.NewGuid().ToString("N");
        var failed = await fixture.Create(taskId, plan, requireLiveOutput: true);
        Assert.Equal("NEEDS_REPAIR", failed.Status);
        Assert.Equal("failed", failed.CodingVerification!.State);
        Assert.Contains(failed.CodingVerification.Checks, receipt => receipt.CheckId == "value" && !receipt.Passed);
        Assert.Contains(failed.CodingVerification.Checks, receipt => receipt.CheckId == "persisted-idempotency" && !receipt.Passed);
        await fixture.AssertNoProcessesOrListener(port);

        var failedReport = failed.CodingVerification.Checks.First(receipt => receipt.CheckId == "value").Report!;
        var content = await fixture.Request("report", taskId, artifactId: failedReport.ArtifactId);
        Assert.Null(content.Error); Assert.Contains("\"passed\":false", content.Report!.Text);
        var repair = new RemoteTaskRequest("owner", taskId, plan with { Steps = [fixture.Step("fix", "fix")] }, AttemptId: Guid.NewGuid().ToString("N"));
        Assert.Null((await fixture.Host.HandleAsync("repair", repair, default)).Error);
        // Duplicate acknowledgement while running must use the persisted attempt receipt.
        Assert.Null((await fixture.Host.HandleAsync("repair", repair, default)).Error);
        var completed = await fixture.Settle(taskId);
        Assert.Equal("COMPLETED", completed.Status); Assert.Equal(taskId, completed.TaskId);
        Assert.True(completed.CodingVerification!.Passed);
        Assert.All(completed.CodingVerification.Checks, receipt =>
        {
            Assert.True(receipt.Passed); Assert.Equal("passed", receipt.State);
            Assert.Equal(receipt.SourceBefore, receipt.SourceAfter);
            Assert.False(string.IsNullOrWhiteSpace(receipt.Requirement));
        });
        Assert.NotEqual(failed.CodingVerification.SourceBefore, completed.CodingVerification.SourceBefore);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace, ".jarvis-qa/fix-invocations.txt")));
        Assert.Null((await fixture.Host.HandleAsync("repair", repair, default)).Error);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace, ".jarvis-qa/fix-invocations.txt")));
        Assert.NotNull((await fixture.Request("report", taskId, artifactId: failedReport.ArtifactId)).Error);
        await fixture.AssertNoProcessesOrListener(port);
        var events = await fixture.AllEvents(taskId);
        Assert.Contains(events, item => item.Type == "service.stopped");
        Assert.Contains(events, item => item.Type == "check.completed" && item.StepId == "persisted-idempotency");
    }

    [Fact]
    public async Task Actual_cli_junit_and_packaged_resource_checks_detect_failure_then_pass_after_new_repairs()
    {
        await using var fixture = new Fixture();
        var spec = new CodingVerificationSpec
        {
            Profiles = ["cli", "native"],
            Checks =
            [
                new() { Id = "cli-output", Kind = "command", ToolId = "process.spawn", Requirement = "CLI emits VALUE=42; exit zero alone is insufficient.",
                    Arguments = fixture.Arguments("cli"), ExpectedText = "VALUE=42", TimeoutSeconds = 10 },
                fixture.TestCheck(),
                Probe("package", "file", "The built package includes the executable assembly and required runtime resource.",
                    new { kind = "file", path = ".jarvis-qa/fixture.zip", archiveMembers = new[] { "lib/fixture.dll", "assets/runtime.json" } })
            ]
        };
        var plan = fixture.Plan("Repair CLI output and native package acceptance", spec, fixture.Step("package", "package"));
        var taskId = Guid.NewGuid().ToString("N");
        var failed = await fixture.Create(taskId, plan);
        Assert.Equal("NEEDS_REPAIR", failed.Status);
        Assert.False(failed.CodingVerification!.Checks.Single(item => item.CheckId == "cli-output").Passed);
        Assert.Equal(0, failed.CodingVerification.Checks.Single(item => item.CheckId == "cli-output").ExitCode);
        Assert.True(failed.CodingVerification.Checks.Single(item => item.CheckId == "real-tests").TestCounts!.Failed > 0);
        Assert.False(failed.CodingVerification.Checks.Single(item => item.CheckId == "package").Passed);
        var repair = plan with { Steps = [fixture.Step("fix", "fix"), fixture.Step("repackage", "package")] };
        Assert.Null((await fixture.Host.HandleAsync("repair", new("owner", taskId, repair, AttemptId: Guid.NewGuid().ToString("N")), default)).Error);
        var passed = await fixture.Settle(taskId);
        Assert.Equal("COMPLETED", passed.Status);
        var receipt = passed.CodingVerification!.Checks.Single(item => item.CheckId == "real-tests");
        Assert.Equal(2, receipt.TestCounts!.Executed); Assert.Equal(2, receipt.TestCounts.Passed); Assert.Equal(0, receipt.TestCounts.Failed);
        var read = await fixture.Request("report", taskId, artifactId: receipt.Report!.ArtifactId);
        Assert.Null(read.Error); Assert.Contains("testsuite", read.Report!.Text);
        var foreign = await fixture.Host.HandleAsync("report", new("different-owner", taskId, ArtifactId: receipt.Report.ArtifactId), default);
        Assert.NotNull(foreign.Error);
        await File.AppendAllTextAsync(receipt.Report.Path, "\ntampered");
        var tampered = await fixture.Request("report", taskId, artifactId: receipt.Report.ArtifactId);
        Assert.NotNull(tampered.Error); Assert.Null(tampered.Report);
        await fixture.AssertNoProcessesOrListener();
    }

    [Fact]
    public async Task A_real_source_edit_during_an_exit_zero_check_invalidates_its_receipt()
    {
        await using var fixture = new Fixture();
        var spec = new CodingVerificationSpec { Profiles = ["cli"], Checks = [new()
        {
            Id = "mutation", Kind = "command", ToolId = "process.spawn", Requirement = "Only evidence from an unchanged source revision can pass.",
            Arguments = fixture.Arguments("mutate"), ExpectedText = "MUTATED", TimeoutSeconds = 10
        }] };
        var taskId = Guid.NewGuid().ToString("N");
        var stale = await fixture.Create(taskId, fixture.Plan("Check CLI source freshness", spec));
        Assert.Equal("NEEDS_VERIFICATION", stale.Status); Assert.Equal("stale", stale.CodingVerification!.State);
        Assert.False(stale.CodingVerification.Passed);
        Assert.NotEqual(stale.CodingVerification.SourceBefore, stale.CodingVerification.SourceAfter);
        Assert.Equal(0, stale.CodingVerification.Checks.First().ExitCode);
        Assert.False(stale.CodingVerification.Checks.First().Passed);
        var completion = await fixture.Request("complete", taskId, attemptId: Guid.NewGuid().ToString("N"));
        Assert.NotEqual("COMPLETED", completion.Task?.Status);
        await fixture.AssertNoProcessesOrListener();
    }

    [Fact]
    public async Task Known_test_process_failure_can_accept_new_repair_steps_without_replaying_the_failed_process()
    {
        await using var fixture = new Fixture();
        var spec = new CodingVerificationSpec { Profiles = ["cli"], Checks = [fixture.TestCheck()] };
        var plan = fixture.Plan("Fix CLI tests", spec, fixture.Step("initial-test", "selftest", Path.Combine(fixture.Workspace, ".jarvis-qa/execution.xml")));
        var taskId = Guid.NewGuid().ToString("N");
        var failed = await fixture.Create(taskId, plan);
        Assert.Equal("NEEDS_REPAIR", failed.Status);
        var artifacts = await fixture.Request("artifacts", taskId);
        Assert.Contains(artifacts.Artifacts!, item => item.StepId == "initial-test" && item.ExitCode == 1 && !item.Success);
        var repair = new RemoteTaskRequest("owner", taskId, plan with { Steps = [fixture.Step("fix-known-failure", "fix")] }, AttemptId: Guid.NewGuid().ToString("N"));
        Assert.Null((await fixture.Host.HandleAsync("repair", repair, default)).Error);
        var passed = await fixture.Settle(taskId);
        Assert.Equal("COMPLETED", passed.Status); Assert.True(passed.CodingVerification!.Passed);
        artifacts = await fixture.Request("artifacts", taskId);
        Assert.Single(artifacts.Artifacts!.Where(item => item.StepId == "initial-test"));
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace, ".jarvis-qa/fix-invocations.txt")));
        await fixture.AssertNoProcessesOrListener();
    }

    [Fact]
    public async Task Autonomous_coordinator_cannot_complete_after_editing_source_beyond_real_passing_coding_checks()
    {
        var coordinator = new MutatingGoalCoordinator();
        await using var fixture = new Fixture(coordinator, initiallyCorrect: true);
        var spec = new CodingVerificationSpec { Profiles = ["cli"], Checks = [new()
        {
            Id = "required-cli", Kind = "command", ToolId = "process.spawn", Requirement = "The actual executable emits VALUE=42 for the verified source revision.",
            Arguments = fixture.Arguments("cli"), ExpectedText = "VALUE=42", TimeoutSeconds = 10
        }] };
        var plan = fixture.Plan("Verify backend CLI behavior", spec, fixture.Step("inspect-correct-state", "cli")) with { ExecutionMode = "AUTONOMOUS" };
        var snapshot = await fixture.Create(Guid.NewGuid().ToString("N"), plan);
        Assert.Equal(1, coordinator.Calls);
        Assert.Equal("NEEDS_VERIFICATION", snapshot.Status);
        Assert.Equal("stale", snapshot.CodingVerification!.State);
        Assert.False(snapshot.CodingVerification.Passed);
        Assert.Contains(snapshot.CodingVerification.Checks, check => check.CheckId == "required-cli" &&
            check.ExitCode == 0 && check.Output?.Contains("VALUE=42", StringComparison.Ordinal) == true);
        using var changed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "state.json")));
        Assert.Equal(99, changed.RootElement.GetProperty("value").GetInt32());
        await fixture.AssertNoProcessesOrListener();
    }

    private sealed class MutatingGoalCoordinator : IRemoteTaskAgenticCoordinator
    {
        public int Calls;
        public Task<RemoteTaskPlan> PlanAsync(RemoteTaskPlanningContext context, CancellationToken cancellationToken) => Task.FromResult(context.Draft);
        public Task<RemoteTaskStep?> RepairAsync(RemoteTaskPlan plan, RemoteTaskStep failedStep, RemoteTaskArtifact failure,
            int repairNumber, CancellationToken cancellationToken) => Task.FromResult<RemoteTaskStep?>(null);
        public async Task<RemoteTaskGoalVerification> VerifyGoalAsync(RemoteTaskGoalContext context, CancellationToken cancellationToken)
        {
            Calls++;
            await File.WriteAllTextAsync(Path.Combine(context.Project, "state.json"), "{\"value\":99,\"deduplicate\":false}", cancellationToken);
            return RemoteTaskGoalVerification.Passed();
        }
    }

    [Fact]
    public async Task Coordinator_mutating_an_ignored_package_after_real_file_acceptance_invalidates_completion()
    {
        var coordinator = new MutatingPackageCoordinator();
        await using var fixture = new Fixture(coordinator, initiallyCorrect: true);
        var spec = new CodingVerificationSpec
        {
            Profiles = ["native"], Checks =
            [
                new() { Id = "required-cli", Kind = "command", ToolId = "process.spawn",
                    Requirement = "The packaged component's executable behavior emits VALUE=42.",
                    Arguments = fixture.Arguments("cli"), ExpectedText = "VALUE=42", TimeoutSeconds = 10 },
                Probe("required-package", "file", "The delivered package contains both assembly and runtime resource at the verified bytes.",
                    new { kind = "file", path = ".jarvis-qa/fixture.zip", archiveMembers = new[] { "lib/fixture.dll", "assets/runtime.json" } })
            ]
        };
        var plan = fixture.Plan("Verify native package acceptance", spec, fixture.Step("create-package", "package")) with { ExecutionMode = "AUTONOMOUS" };
        var snapshot = await fixture.Create(Guid.NewGuid().ToString("N"), plan);
        Assert.Equal(1, coordinator.Calls);
        Assert.Equal("NEEDS_VERIFICATION", snapshot.Status);
        Assert.Equal("stale", snapshot.CodingVerification!.State);
        Assert.False(snapshot.CodingVerification.Passed);
        Assert.Equal(snapshot.CodingVerification.SourceBefore, snapshot.CodingVerification.SourceAfter);
        Assert.Contains(snapshot.CodingVerification.Checks, check => check.CheckId == "required-package" && check.Report is not null);
        var changedPackage = await File.ReadAllBytesAsync(Path.Combine(fixture.Workspace, ".jarvis-qa/fixture.zip"));
        Assert.True(changedPackage.AsSpan().EndsWith("post-verification-mutation"u8));
        await fixture.AssertNoProcessesOrListener();
    }

    private sealed class MutatingPackageCoordinator : IRemoteTaskAgenticCoordinator
    {
        public int Calls;
        public Task<RemoteTaskPlan> PlanAsync(RemoteTaskPlanningContext context, CancellationToken cancellationToken) => Task.FromResult(context.Draft);
        public Task<RemoteTaskStep?> RepairAsync(RemoteTaskPlan plan, RemoteTaskStep failedStep, RemoteTaskArtifact failure,
            int repairNumber, CancellationToken cancellationToken) => Task.FromResult<RemoteTaskStep?>(null);
        public async Task<RemoteTaskGoalVerification> VerifyGoalAsync(RemoteTaskGoalContext context, CancellationToken cancellationToken)
        {
            Calls++;
            await File.AppendAllTextAsync(Path.Combine(context.Project, ".jarvis-qa/fixture.zip"), "post-verification-mutation", cancellationToken);
            return RemoteTaskGoalVerification.Passed();
        }
    }

    private static CodingVerificationCheck Probe(string id, string kind, string requirement, object arguments) => new()
    { Id = id, Kind = kind, ToolId = "developer.verify", Requirement = requirement, Arguments = WireJson.Element(arguments), TimeoutSeconds = 10 };

    private static int FreePort()
    { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-task-acceptance-" + Guid.NewGuid().ToString("N"));
        private readonly ProcessToolSet _processes = new();
        private readonly ExecutionResourceCoordinator _resources = new();
        private readonly Dictionary<string, IAgentTool> _tools;
        private readonly string _program = Path.Combine(AppContext.BaseDirectory, "qa-fixture", "Jarvis.QaFixture.dll");
        public string Workspace { get; }
        public RemoteTaskHost Host { get; }
        private string StatePath => Path.Combine(Workspace, "state.json");

        public Fixture(IRemoteTaskAdaptiveCoordinator? coordinator = null, bool initiallyCorrect = false, IAgentTool? extra = null)
        {
            Assert.True(File.Exists(_program), "QaFixture must be built by the Core.Tests project reference: " + _program);
            Workspace = Path.Combine(_root, "project"); Directory.CreateDirectory(Workspace);
            File.WriteAllText(StatePath, initiallyCorrect ? "{\"value\":42,\"deduplicate\":true}" : "{\"value\":41,\"deduplicate\":false}");
            var privateArtifacts = Path.Combine(_root, "private-artifacts");
            _tools = _processes.Tools.Concat(new IAgentTool[]
            {
                new DeveloperVerifyTool(privateArtifacts), new DeveloperTestTool(BoundedDeveloperTestRunner.RunAsync, privateArtifacts)
            }).Concat(extra is null ? Array.Empty<IAgentTool>() : [extra]).ToDictionary(tool => tool.Descriptor.Id, StringComparer.Ordinal);
            Host = new RemoteTaskHost(Path.Combine(_root, "tasks"), new WorkspaceDirectories(Workspace),
                new DynamicToolRegistry(_tools.Values), () => true, Invoke, Cancel, adaptive: coordinator);
        }

        public JsonElement Arguments(string mode, params string[] more) => WireJson.Element(new
        { argv = new[] { "dotnet", _program, mode, StatePath }.Concat(more).ToArray(), workingDirectory = Workspace, timeoutSeconds = 60 });
        public RemoteTaskStep Step(string id, string mode, params string[] more) => new()
        { Id = id, ToolId = "process.spawn", Arguments = Arguments(mode, more), TimeoutSeconds = 15 };
        public CodingVerificationCheck TestCheck() => new()
        {
            Id = "real-tests", Kind = "test", ToolId = "developer.test", Requirement = "Both executable fixture assertions pass in a fresh JUnit report.",
            MinimumTests = 2, TimeoutSeconds = 15,
            Arguments = WireJson.Element(new { framework = "custom", reportFormat = "junit", project = Workspace, timeoutSeconds = 10,
                argv = new[] { "dotnet", _program, "selftest", StatePath, "{report}" } })
        };
        public RemoteTaskPlan Plan(string goal, CodingVerificationSpec spec, params RemoteTaskStep[] steps) => new()
        { Goal = goal, Project = Workspace, ExecutionMode = "NORMAL", TimeoutSeconds = 60, Steps = steps, CodingVerification = spec };

        private async Task<ToolReply> Invoke(string id, JsonElement arguments, AgentExecutionContext context, CancellationToken ct)
        {
            var tool = _tools[id];
            if (!SchemaGuard.Matches(SchemaGuard.Compile(tool.Descriptor.InputSchema), arguments)) return ToolReply.Error("Fixture rejected arguments using the production tool schema: " + id);
            var claims = ToolExecutionResources.For(tool.Descriptor, arguments, context);
            var owner = (context.OwnerId ?? "legacy") + "|" + context.IsolationScopeId;
            using var lease = context.CooperativeResourceGroup is { } group
                ? await _resources.AcquireGroupAsync(owner, group, claims.Resources, claims.Exclusive, ct)
                : await _resources.AcquireAsync(owner, claims.Resources, claims.Exclusive, ct);
            return await tool.ExecuteAsync(arguments, context with { RetainResources = ((IExecutionResourceLease)lease).Retain }, ct);
        }
        private async Task Cancel(string jobId, AgentExecutionContext context)
        {
            var reply = await Invoke("process.cancel", WireJson.Element(new { jobId }), context, default);
            if (reply.IsError) throw new InvalidOperationException(reply.Text);
        }
        public Task<RemoteTaskReply> Request(string operation, string taskId, string? artifactId = null, string? attemptId = null) =>
            Host.HandleAsync(operation, new("owner", taskId, ArtifactId: artifactId, AttemptId: attemptId), default);

        public async Task<RemoteTaskSnapshot> Create(string taskId, RemoteTaskPlan plan, bool requireLiveOutput = false)
        {
            var created = await Host.HandleAsync("create", new("owner", taskId, plan), default);
            Assert.Null(created.Error);
            return await Settle(taskId, requireLiveOutput);
        }
        public async Task<RemoteTaskSnapshot> Settle(string taskId, bool requireLiveOutput = false)
        {
            var until = DateTimeOffset.UtcNow.AddSeconds(45); var liveOutput = false;
            while (DateTimeOffset.UtcNow < until)
            {
                var snapshot = (await Request("get", taskId)).Task!;
                var terminal = snapshot.Status is "COMPLETED" or "FAILED" or "CANCELLED" or "NEEDS_REPAIR" or "NEEDS_VERIFICATION";
                var events = await AllEvents(taskId);
                if (!terminal && events.Any(item => item.Type == "process.output" && item.Text?.Contains("CLI_STARTED", StringComparison.Ordinal) == true)) liveOutput = true;
                if (terminal)
                {
                    if (requireLiveOutput) Assert.True(liveOutput, "The task must expose partial process output while execution is still running.");
                    // The persisted pending state can precede active-slot release by a scheduler turn.
                    await Task.Delay(50);
                    return snapshot;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("Real production task did not settle: " + JsonSerializer.Serialize((await Request("get", taskId)).Task));
        }
        public async Task<IReadOnlyList<RemoteTaskEvent>> AllEvents(string taskId)
        {
            var result = new List<RemoteTaskEvent>(); var cursor = 0;
            while (true)
            {
                var page = await Host.HandleAsync("events", new("owner", taskId, Offset: cursor, Limit: 20), default);
                Assert.Null(page.Error); result.AddRange(page.Events!);
                if (page.Events!.Count < 20 || page.NextOffset is not { } next || next == cursor) return result;
                cursor = next;
            }
        }
        public async Task AssertNoProcessesOrListener(int? port = null)
        {
            var until = DateTimeOffset.UtcNow.AddSeconds(5);
            while (_processes.RunningCount != 0 && DateTimeOffset.UtcNow < until) await Task.Delay(25);
            Assert.Equal(0, _processes.RunningCount);
            if (port is not { } endpoint) return;
            using var socket = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(() => socket.ConnectAsync(IPAddress.Loopback, endpoint));
        }
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync(); _processes.Dispose(); _resources.Dispose();
            var resolved = Path.GetFullPath(_root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved);
            Assert.StartsWith("jarvis-task-acceptance-", Path.GetFileName(resolved));
            Directory.Delete(resolved, true);
        }
    }
}
