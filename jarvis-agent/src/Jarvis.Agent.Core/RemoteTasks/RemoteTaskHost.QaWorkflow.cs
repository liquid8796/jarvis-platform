using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Agent.Core.Autonomous.Verification;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

internal sealed partial class RemoteTaskHost
{
    private RemoteTaskReply HandleQaWorkflow(string operation, RemoteTaskRequest request, StoredRemoteTask task,
        AgentExecutionContext? caller, CancellationToken sessionToken)
    {
        if (operation == "capture") return ReadQaCapture(task, request.CaptureId);
        var attemptId = RemoteTaskRules.TaskId(request.AttemptId ?? "");
        var digest = RemoteTaskStore.Hash(JsonSerializer.Serialize(new { operation, request.Plan, request.VerificationSpec, request.VisualReview }, WireJson.Options));
        var receipt = task.WorkflowReceipts.FirstOrDefault(item => item.AttemptId == attemptId);
        if (receipt is not null)
            return receipt.Operation == operation && receipt.Digest == digest
                ? new(Task: task.Snapshot)
                : RemoteTaskReply.Failure("conflict", "attemptId already belongs to a different workflow request.");
        if (task.WorkflowReceipts.Count >= FrontendQaRules.MaxWorkflowAttempts)
            return RemoteTaskReply.Failure("conflict", "The bounded QA workflow attempt budget is exhausted. Submit a new task after inspecting its evidence.");
        if (_active.ContainsKey(Key(task.OwnerId, task.Snapshot.TaskId)))
            return RemoteTaskReply.Failure("busy", "The task is still executing. Wait for a pending QA state before continuing.");
        if (task.Snapshot.Status is not ("NEEDS_VERIFICATION" or "NEEDS_REPAIR" or "NEEDS_REVIEW" or "READY_TO_COMPLETE"))
            return RemoteTaskReply.Failure("conflict", "Only a task awaiting QA can continue. Terminal or uncertain work is never replayed.");
        RequireArmed();
        if (task.Plan.ExecutionMode == "READ_ONLY")
            return RemoteTaskReply.Failure("forbidden", "READ_ONLY execution cannot start mutating browser verification or repairs.");
        var receipts = task.WorkflowReceipts.Append(new RemoteTaskWorkflowReceipt(attemptId, operation, digest)).ToArray();

        if (operation is "verify" or "repair")
        {
            if (_active.Count >= (long)_settings.MaxDurableTasks + _settings.MaxQueuedCalls)
                return RemoteTaskReply.Failure("busy", "TASK_QUEUE_FULL: The bounded durable task queue is full. No task was started.");
            if (request.VisualReview is not null) throw new ArgumentException("A visual review is only accepted by review.");
            var spec = request.VerificationSpec ?? task.Plan.VerificationSpec;
            if (spec is null) throw new ArgumentException("An explicit verificationSpec is required before rendered QA.");
            FrontendQaRules.Validate(spec);
            var plan = task.Plan with { VerificationSpec = spec };
            if (operation == "repair")
            {
                if (task.QaRepairCount >= FrontendQaRules.MaxRepairRounds)
                    return RemoteTaskReply.Failure("conflict", "Frontend repair budget exhausted. Inspect the remaining failures before creating a new task.");
                var repair = request.Plan ?? throw new ArgumentException("Repair requires an explicit plan containing only the new repair steps.");
                if (repair.Goal != task.Plan.Goal || repair.ExecutionMode != task.Plan.ExecutionMode || repair.TimeoutSeconds != task.Plan.TimeoutSeconds ||
                    !PathEquals(ResolveProject(repair.Project, task.OwnerSessionId is null ? null : StoredContext(task)), task.Snapshot.Project) || repair.Steps.Count == 0)
                    throw new ArgumentException("Repair must retain the original goal/project/mode/timeout and contain new explicit steps.");
                if (repair.VerificationSpec is not null && JsonSerializer.Serialize(repair.VerificationSpec, WireJson.Options) != JsonSerializer.Serialize(spec, WireJson.Options))
                    throw new ArgumentException("Use verificationSpec to change the QA specification consistently.");
                plan = repair with { VerificationSpec = spec };
                RemoteTaskRules.Validate(plan);
                ValidateTools(plan);
            }
            else if (request.Plan is not null) throw new ArgumentException("Verify never accepts executable steps; use repair for new work.");
            task = task with
            {
                Plan = Clone(plan), PlanDigest = RemoteTaskStore.Digest(plan), WorkflowReceipts = receipts,
                QaRepairCount = task.QaRepairCount + (operation == "repair" ? 1 : 0), VisualReview = null,
                QaRunId = null, QaSourceRevision = null, FrontendEvidence = [], DeliveredCaptureIds = [],
                Snapshot = task.Snapshot with
                {
                    Status = "QUEUED", CurrentStep = null, Error = null, Verification = null, UpdatedAt = DateTimeOffset.UtcNow,
                    TotalSteps = task.Snapshot.TotalSteps + (operation == "repair" ? plan.Steps.Count : 0)
                }
            };
            task = Save(task); // Durable receipt before starting: a lost acknowledgement cannot replay the work.
            Start(task, sessionToken, caller?.SessionCancellation ?? default, verificationOnly: operation == "verify");
            return new(Task: task.Snapshot);
        }

        if (request.Plan is not null || request.VerificationSpec is not null)
            throw new ArgumentException("Review/completion cannot change executable work or its verification specification.");
        var workspace = FrontendWorkspaceState.Capture(task.Snapshot.Project, sessionToken);
        if (!workspace.Complete || string.IsNullOrWhiteSpace(task.QaSourceRevision) || workspace.Revision != task.QaSourceRevision)
            return InvalidateQa(task, "Source files changed or could not be completely observed. Run verify again before reviewing or completing.");
        try { foreach (var capture in CurrentCaptures(task)) _ = ReadCaptureBytes(capture); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return InvalidateQa(task, "Screenshot artifacts changed or are unavailable. Run verify again before reviewing or completing."); }
        var requirement = FrontendChangeClassifier.Classify(task.Plan.Goal, task.Plan.Steps, workspace.ChangedSince(task.SourceBaseline), task.Plan.VerificationSpec, workspace.Files.Keys.ToArray());
        var visualReview = task.VisualReview;
        if (operation == "review")
        {
            visualReview = request.VisualReview ?? throw new ArgumentException("review requires visualReview with observations for the delivered screenshots.");
            FrontendVerificationGate.ValidateReview(visualReview, task.FrontendEvidence, task.Snapshot.TaskId,
                task.QaSourceRevision, task.QaRunId, task.Plan.VerificationSpec);
            if (CurrentCaptures(task).Any(capture => !task.DeliveredCaptureIds.Contains(capture.CaptureId, StringComparer.Ordinal)))
                throw new ArgumentException("Fetch every current screenshot with agent_task_capture before submitting visual observations.");
        }
        else if (request.VisualReview is not null) throw new ArgumentException("Submit visualReview through review before completion.");
        var result = FrontendVerificationGate.Evaluate(requirement, task.FrontendEvidence, task.Plan.VerificationSpec,
            visualReview, task.QaRunId, task.QaSourceRevision);
        if (operation == "complete" && (!result.Passed || !task.GoalVerificationPassed))
            return RemoteTaskReply.Failure("conflict", !task.GoalVerificationPassed ? "The goal verifier still requires a repair; a visual review cannot override it." : result.DescribeFailure());
        var allPassed = result.Passed && task.GoalVerificationPassed;
        var completed = operation == "complete";
        var status = completed ? "COMPLETED" : allPassed ? "READY_TO_COMPLETE" : "NEEDS_REPAIR";
        var summary = ToProtocolVerificationSummary(requirement, result) with
        {
            Passed = allPassed, State = allPassed ? "passed" : "failed", SourceRevision = task.QaSourceRevision,
            VerificationRunId = task.QaRunId, Captures = CurrentCaptures(task),
            VisualReview = visualReview,
            NextAction = completed ? null : allPassed ? "Call agent_task_complete with a new attemptId." : "Inspect failures and submit new bounded steps with agent_task_repair."
        };
        task = Save(task with { WorkflowReceipts = receipts, VisualReview = visualReview,
            Snapshot = task.Snapshot with { Status = status, CurrentStep = null, Verification = summary,
                Error = allPassed ? null : !task.GoalVerificationPassed ? "The goal verifier still requires a repair." : result.DescribeFailure(), UpdatedAt = DateTimeOffset.UtcNow } });
        return new(Task: task.Snapshot);
    }

    private RemoteTaskReply ReadQaCapture(StoredRemoteTask task, string? captureId)
    {
        if (string.IsNullOrWhiteSpace(captureId) || captureId.Length > 200) throw new ArgumentException("A current captureId is required.");
        var capture = CurrentCaptures(task).SingleOrDefault(item => item.CaptureId == captureId)
            ?? throw new ArgumentException("Capture was not produced by the current verification run of this task.");
        var bytes = ReadCaptureBytes(capture);
        if (!task.DeliveredCaptureIds.Contains(captureId, StringComparer.Ordinal))
            task = Save(task with { DeliveredCaptureIds = task.DeliveredCaptureIds.Append(captureId).ToArray() });
        return new(Task: task.Snapshot, Images: [new WireImage("image/png", Convert.ToBase64String(bytes))]);
    }

    private static FrontendQaCapture[] CurrentCaptures(StoredRemoteTask task) => task.FrontendEvidence
        .Where(item => item.Kind == FrontendEvidenceKind.Screenshot && item.Success && item.Provenance?.Producer == "collector" &&
            item.Provenance.TaskId == task.Snapshot.TaskId && item.Provenance.VerificationRunId == task.QaRunId &&
            item.Provenance.SourceRevision == task.QaSourceRevision && item.Capture is not null)
        .Select(item => item.Capture!).DistinctBy(item => item.CaptureId).ToArray();

    private static byte[] ReadCaptureBytes(FrontendQaCapture capture)
    {
        const int maxBytes = 4 * 1024 * 1024;
        if (!Path.IsPathFullyQualified(capture.ArtifactPath) || !FrontendQaRules.IsSha256(capture.Sha256))
            throw new ArgumentException("Capture path/hash is invalid.");
        for (var path = capture.ArtifactPath; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Capture path contains a reparse point.");
        using var stream = new FileStream(capture.ArtifactPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 24 or > maxBytes) throw new ArgumentException("Capture PNG exceeds the bounded 4 MiB image delivery limit or is invalid.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), capture.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Capture image changed or is not a PNG. Run verification again.");
        return bytes;
    }

    private RemoteTaskReply InvalidateQa(StoredRemoteTask task, string reason)
    {
        task = Save(task with { VisualReview = null, Snapshot = task.Snapshot with
        {
            Status = "NEEDS_VERIFICATION", Error = reason, UpdatedAt = DateTimeOffset.UtcNow,
            Verification = task.Snapshot.Verification is { } summary ? summary with { Passed = false, State = "stale", VisualReview = null, NextAction = "Run agent_task_verify for the current source revision." } : null
        } });
        return new(Task: task.Snapshot);
    }
}
