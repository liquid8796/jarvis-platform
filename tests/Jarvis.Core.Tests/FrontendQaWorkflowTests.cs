using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Autonomous.Verification;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class FrontendQaWorkflowTests
{
    [Fact]
    public async Task External_review_requires_delivered_bound_images_then_completion_is_idempotent()
    {
        await using var fixture = new QaFixture();
        var reviewAttempt = Guid.NewGuid().ToString("N");
        var beforeImages = await fixture.Call("review", reviewAttempt, review: fixture.Review);
        Assert.Equal("invalid", beforeImages.ErrorCode);
        foreach (var capture in fixture.Captures)
        {
            var delivered = await fixture.Host.HandleAsync("capture", new("owner", fixture.Id, CaptureId: capture.CaptureId), CancellationToken.None);
            Assert.Single(delivered.Images!);
            Assert.Equal("image/png", delivered.Images![0].MimeType);
            Assert.Equal(capture.Sha256, Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(delivered.Images[0].Base64))));
        }
        var reviewed = await fixture.Call("review", reviewAttempt, review: fixture.Review);
        Assert.Null(reviewed.Error);
        Assert.Equal("READY_TO_COMPLETE", reviewed.Task!.Status);
        var completeAttempt = Guid.NewGuid().ToString("N");
        var completed = await fixture.Call("complete", completeAttempt);
        Assert.Equal("COMPLETED", completed.Task!.Status);
        Assert.True(completed.Task.Verification!.Passed);
        Assert.Equal(JsonSerializer.Serialize(completed.Task, WireJson.Options),
            JsonSerializer.Serialize((await fixture.Call("complete", completeAttempt)).Task, WireJson.Options));
        var collision = await fixture.Call("review", completeAttempt, review: fixture.Review);
        Assert.Equal("conflict", collision.ErrorCode);
        Assert.Equal(0, fixture.Invocations);
    }

    [Fact]
    public async Task Changed_source_invalidates_review_and_cannot_complete()
    {
        await using var fixture = new QaFixture();
        foreach (var capture in fixture.Captures)
            await fixture.Host.HandleAsync("capture", new("owner", fixture.Id, CaptureId: capture.CaptureId), CancellationToken.None);
        Assert.Equal("READY_TO_COMPLETE", (await fixture.Call("review", Guid.NewGuid().ToString("N"), review: fixture.Review)).Task!.Status);
        File.AppendAllText(Path.Combine(fixture.Workspace, "app.tsx"), "\n// changed after QA");
        var completion = await fixture.Call("complete", Guid.NewGuid().ToString("N"));
        Assert.Equal("NEEDS_VERIFICATION", completion.Task!.Status);
        Assert.False(completion.Task.Verification!.Passed);
        Assert.Equal("stale", completion.Task.Verification.State);
    }

    [Fact]
    public async Task Capture_is_owner_bound_and_rejects_tampered_images()
    {
        await using var fixture = new QaFixture();
        Assert.Equal("not_found", (await fixture.Host.HandleAsync("capture", new("other-owner", fixture.Id, CaptureId: fixture.Captures[0].CaptureId), CancellationToken.None)).ErrorCode);
        Assert.Equal("invalid", (await fixture.Host.HandleAsync("capture", new("owner", fixture.Id, CaptureId: "../../secret"), CancellationToken.None)).ErrorCode);
        File.AppendAllText(fixture.Captures[0].ArtifactPath, "changed");
        var tampered = await fixture.Host.HandleAsync("capture", new("owner", fixture.Id, CaptureId: fixture.Captures[0].CaptureId), CancellationToken.None);
        Assert.Equal("invalid", tampered.ErrorCode);
        Assert.Null(tampered.Images);
    }

    [Fact]
    public async Task Normal_spec_only_create_runs_verification_without_an_unrelated_edit_step()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-qa-spec-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var calls = 0;
            await using var host = new RemoteTaskHost(Path.Combine(root, "tasks"), new WorkspaceDirectories(root), new DynamicToolRegistry([]), () => true,
                (_, _, _, _) => { calls++; return Task.FromResult(ToolReply.Error("No tools installed.")); }, (_, _) => Task.CompletedTask);
            var id = Guid.NewGuid().ToString("N");
            var created = await host.HandleAsync("create", new("owner", id, new RemoteTaskPlan { Goal = "Verify the frontend", VerificationSpec = Spec() }), CancellationToken.None);
            Assert.Equal("QUEUED", created.Task!.Status);
            RemoteTaskSnapshot? pending = null;
            for (var i = 0; i < 100; i++)
            {
                pending = (await host.HandleAsync("get", new("owner", id), CancellationToken.None)).Task;
                if (pending!.Status.StartsWith("NEEDS_", StringComparison.Ordinal)) break;
                await Task.Delay(10);
            }
            Assert.NotEqual("NEEDS_PLAN", pending!.Status);
            Assert.False(pending.Verification!.Passed);
            Assert.Equal(0, calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Spec_rejects_nominal_hover_and_requires_a_real_postcondition()
    {
        var spec = Spec();
        FrontendQaRules.Validate(spec);
        Assert.Throws<ArgumentException>(() => FrontendQaRules.Validate(spec with { Steps = [spec.Steps[0] with { Action = "hover" }] }));
        Assert.Throws<ArgumentException>(() => FrontendQaRules.Validate(spec with { Steps = [spec.Steps[0] with { Expect = null }] }));
        Assert.Throws<ArgumentException>(() => FrontendQaRules.Validate(spec with { Viewports = [spec.Viewports[0] with { Height = 2161 }] }));
    }

    [Fact]
    public async Task Repair_submissions_are_bounded_idempotent_and_preserve_task_scope()
    {
        await using var fixture = new QaFixture(allowRepair: true);
        var repairPlan = fixture.Plan with { Steps = [new() { Id = "repair", ToolId = "test.repair" }] };
        var changedGoal = await fixture.Host.HandleAsync("repair", new("owner", fixture.Id, repairPlan with { Goal = "Different work" },
            AttemptId: Guid.NewGuid().ToString("N")), CancellationToken.None);
        Assert.Equal("invalid", changedGoal.ErrorCode);
        for (var round = 1; round <= FrontendQaRules.MaxRepairRounds; round++)
        {
            var request = new RemoteTaskRequest("owner", fixture.Id, repairPlan, AttemptId: Guid.NewGuid().ToString("N"));
            var started = await WhenIdle("repair", request);
            Assert.Null(started.Error);
            var duplicate = await fixture.Host.HandleAsync("repair", request, CancellationToken.None);
            Assert.Null(duplicate.Error);
            for (var wait = 0; wait < 150; wait++)
            {
                var task = (await fixture.Host.HandleAsync("get", new("owner", fixture.Id), CancellationToken.None)).Task!;
                if (task.Status.StartsWith("NEEDS_", StringComparison.Ordinal) && fixture.Invocations == round) break;
                await Task.Delay(10);
            }
            Assert.Equal(round, fixture.Invocations);
        }
        var exhausted = await WhenIdle("repair", new("owner", fixture.Id, repairPlan, AttemptId: Guid.NewGuid().ToString("N")));
        Assert.Equal("conflict", exhausted.ErrorCode);
        Assert.Equal(FrontendQaRules.MaxRepairRounds, fixture.Invocations);

        async Task<RemoteTaskReply> WhenIdle(string operation, RemoteTaskRequest request)
        {
            for (var i = 0; i < 150; i++)
            {
                var reply = await fixture.Host.HandleAsync(operation, request, CancellationToken.None);
                if (reply.ErrorCode != "busy") return reply;
                await Task.Delay(10);
            }
            throw new TimeoutException("Workflow did not release its prior activity.");
        }
    }

    [Fact]
    public async Task Interrupted_task_cannot_replay_work_through_repair()
    {
        await using var fixture = new QaFixture(allowRepair: true, initialStatus: "INTERRUPTED");
        var reply = await fixture.Host.HandleAsync("repair", new("owner", fixture.Id,
            fixture.Plan with { Steps = [new() { Id = "repair", ToolId = "test.repair" }] }, AttemptId: Guid.NewGuid().ToString("N")), CancellationToken.None);
        Assert.Equal("conflict", reply.ErrorCode);
        Assert.Equal(0, fixture.Invocations);
    }

    private static FrontendQaSpec Spec() => new()
    {
        Url = "http://localhost:4173/", RequireVisualReview = true,
        Viewports = [new() { Name = "desktop", Width = 1440, Height = 900 }, new() { Name = "mobile", Width = 390, Height = 844, Mobile = true }],
        Steps = [new() { Action = "click", Locator = new() { TestId = "open-panel" }, Expect = new() { Kind = "visible", Locator = new() { TestId = "panel" } } }]
    };

    private sealed class QaFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-qa-workflow-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Workspace { get; }
        public RemoteTaskHost Host { get; }
        public FrontendQaCapture[] Captures { get; }
        public FrontendQaVisualReview Review { get; }
        public RemoteTaskPlan Plan { get; }
        public int Invocations { get; private set; }
        public QaFixture(bool allowRepair = false, string initialStatus = "NEEDS_REVIEW")
        {
            Workspace = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(Path.Combine(Workspace, "artifacts"));
            File.WriteAllText(Path.Combine(Workspace, "app.tsx"), "export const App = () => <button>Open</button>;");
            var source = FrontendWorkspaceState.Capture(Workspace);
            var spec = Spec();
            var now = DateTimeOffset.UtcNow;
            var run = Guid.NewGuid().ToString("N");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aR1sAAAAASUVORK5CYII=");
            Captures = spec.Viewports.Select(viewport =>
            {
                var path = Path.Combine(Workspace, "artifacts", viewport.Name + ".png");
                File.WriteAllBytes(path, png);
                return new FrontendQaCapture { CaptureId = run + "-" + viewport.Name, ArtifactPath = path,
                    Sha256 = Convert.ToHexString(SHA256.HashData(png)), Width = viewport.Width, Height = viewport.Height,
                    SourceRevision = source.Revision, VerificationRunId = run, TargetUrl = spec.Url, ViewportId = viewport.Name, CapturedAt = now };
            }).ToArray();
            var requirement = FrontendVerificationRequirement.ForFrontend(true);
            var evidence = Captures.SelectMany(capture => requirement.Required.Where(kind => kind != FrontendEvidenceKind.VisualFidelity).Select(kind =>
                new FrontendEvidence(kind, true, "Collector fixture observed " + kind, Provenance: new FrontendQaProvenance
                {
                    TaskId = Id, SourceRevision = source.Revision, VerificationRunId = run, TargetUrl = spec.Url,
                    ObservationId = capture.CaptureId, CapturedAt = now, Producer = "collector", ViewportId = capture.ViewportId
                }, Capture: kind == FrontendEvidenceKind.Screenshot ? capture : null))).ToArray();
            Review = new() { VerificationRunId = run, SourceRevision = source.Revision, Reviewer = "external-test-reviewer", Summary = "Inspected both delivered images.",
                Checks = Captures.SelectMany(capture => FrontendQaRules.ReviewCategories.Select(category => new FrontendQaReviewCheck
                { CaptureId = capture.CaptureId, ScreenshotSha256 = capture.Sha256, Category = category, Passed = true, Observation = "Observed " + category + " in this capture." })).ToArray() };
            var plan = new RemoteTaskPlan { Goal = "Fix frontend layout", Project = Workspace, VerificationSpec = spec };
            Plan = plan;
            var snapshot = new RemoteTaskSnapshot(Id, plan.Goal, Workspace, initialStatus, null, 0, 0, now, now);
            using (var store = new RemoteTaskStore(Path.Combine(_root, "tasks")))
                store.Save(new StoredRemoteTask(1, "owner", RemoteTaskStore.Digest(plan), RemoteTaskStore.Digest(plan), plan, snapshot, [])
                { SourceBaseline = source.Files, QaRunId = run, QaSourceRevision = source.Revision, FrontendEvidence = evidence });
            Host = new RemoteTaskHost(Path.Combine(_root, "tasks"), new WorkspaceDirectories(Workspace),
                new DynamicToolRegistry(allowRepair ? [new RepairProbeTool()] : []), () => true,
                (_, _, _, _) => { Invocations++; return Task.FromResult(allowRepair ? new ToolReply("REPAIRED") : ToolReply.Error("Unexpected tool invocation.")); }, (_, _) => Task.CompletedTask);
        }
        public Task<RemoteTaskReply> Call(string operation, string attemptId, FrontendQaVisualReview? review = null) =>
            Host.HandleAsync(operation, new("owner", Id, AttemptId: attemptId, VisualReview: review), CancellationToken.None);
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); Directory.Delete(_root, true); }
    }

    private sealed class RepairProbeTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("test.repair", "test_repair", "test", "Repair fixture",
            WireJson.Element(new { type = "object", additionalProperties = false }), false);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply("REPAIRED"));
    }
}
