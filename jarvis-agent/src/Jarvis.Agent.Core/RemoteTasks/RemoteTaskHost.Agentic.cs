using Jarvis.Agent.Core.Autonomous.Verification;
using Jarvis.Agent.Core.Plugins;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Buffers.Binary;
using Jarvis.Agent.Core.Prompting;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

internal sealed partial class RemoteTaskHost
{
    private async Task<StoredRemoteTask> EnsureAgenticPlanAsync(StoredRemoteTask task, Active active)
    {
        if (task.Plan.Steps.Count > 0 || task.Plan.ExecutionMode != "AUTONOMOUS" || _agentic is null) return task;
        active.Stop.Token.ThrowIfCancellationRequested();
        RequireArmed();
        task = Save(task with { Snapshot = task.Snapshot with { Status = "PLANNING", CurrentStep = null, UpdatedAt = DateTimeOffset.UtcNow } });
        var tools = _registry.Snapshot.Descriptors;
        var skillMetadata = _availableSkills.Select(skill => $"{skill.Id}: {skill.Description}").ToArray();
        var promptLayers = new CodingPromptAssembler().Assemble(new CodingPromptRequest(
            task.Plan.Goal, task.Snapshot.Project, tools, SkillMetadata: skillMetadata));
        var planningContext = new RemoteTaskPlanningContext(task.Plan, task.Snapshot.Project, tools)
        {
            PromptLayers = promptLayers,
            AvailableSkills = _availableSkills
        }.WithSkillLoader(_skillLoader);
        var generated = await _agentic.PlanAsync(planningContext, active.Stop.Token).ConfigureAwait(false);
        ValidateAgenticPlan(task.Plan, generated, task.Snapshot.Project);
        var persisted = Clone(generated);
        return Save(task with
        {
            Plan = persisted,
            PlanDigest = RemoteTaskStore.Digest(persisted),
            Snapshot = task.Snapshot with
            {
                Status = "QUEUED",
                CurrentStep = null,
                TotalSteps = persisted.Steps.Count,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        });
    }

    private void ValidateAgenticPlan(RemoteTaskPlan draft, RemoteTaskPlan generated, string project)
    {
        ArgumentNullException.ThrowIfNull(generated);
        if (!StringComparer.Ordinal.Equals(draft.Goal, generated.Goal) ||
            !StringComparer.Ordinal.Equals(draft.ExecutionMode, generated.ExecutionMode) ||
            draft.TimeoutSeconds != generated.TimeoutSeconds)
            throw new ArgumentException("Agentic planning must retain the task goal, execution mode and timeout.");
        if (generated.Steps.Count == 0) throw new ArgumentException("Agentic planning must return at least one executable step.");
        if (!string.IsNullOrWhiteSpace(generated.Project) && !PathEquals(ResolveProject(generated.Project), project))
            throw new ArgumentException("Agentic planning cannot change the resolved project.");
        RemoteTaskRules.Validate(generated);
        ValidateTools(generated);
    }

    private async Task<(StoredRemoteTask Task, bool Success)> ExecuteStepsAsync(
        StoredRemoteTask task, Active active, IReadOnlyList<RemoteTaskStep> steps)
    {
        if (task.SourceBaseline is null)
        {
            var baseline = FrontendWorkspaceState.Capture(task.Snapshot.Project, active.Stop.Token);
            task = Save(task with { SourceBaseline = baseline.Complete ? baseline.Files : new Dictionary<string, string>() });
        }
        foreach (var originalStep in steps)
        {
            var step = originalStep;
            var repairs = 0;
            while (true)
            {
                active.Stop.Token.ThrowIfCancellationRequested();
                RequireArmed();
                task = Save(task with { Snapshot = task.Snapshot with { Status = "RUNNING", CurrentStep = step.Id, UpdatedAt = DateTimeOffset.UtcNow } });
                var succeeded = false;
                var cancelledOrTimedOut = false;
                var knownFailure = false;
                RemoteTaskArtifact? failureArtifact = null;

                for (var attempt = 1; attempt <= step.MaxAttempts; attempt++)
                {
                    using var stepStop = CancellationTokenSource.CreateLinkedTokenSource(active.Stop.Token);
                    stepStop.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));
                    var context = active.Context with
                    {
                        CallId = task.Snapshot.TaskId + ":" + step.Id + ":r" + repairs + ":" + attempt,
                        SessionCancellation = active.Stop.Token,
                        ReportOutput = (stream, text) => EmitCodingEvent(task, new() { Type = "step.output", StepId = step.Id,
                            ToolId = step.ToolId, Stream = stream, Text = text })
                    };
                    RemoteStepResult result;
                    try
                    {
                        if (step.ToolId is "process.start" or "process.spawn")
                            result = await RemoteProcessRunner.RunAsync(step, context, _invoke, _cancelJob, stepStop.Token,
                                e => EmitCodingEvent(task, e)).ConfigureAwait(false);
                        else
                        {
                            var reply = await _invoke(step.ToolId, step.Arguments, context, stepStop.Token).ConfigureAwait(false);
                            result = new(!reply.IsError, reply.Text, Error: reply.IsError ? reply.Text : null);
                            if (step.ToolId == "developer.test")
                            {
                                using var testResult = JsonDocument.Parse(reply.Text);
                                if (testResult.RootElement.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var exitCode))
                                    result = result with { ExitCode = exitCode, KnownCompletion = true };
                            }
                        }
                        if (result.Output.Length > RemoteTaskRules.OutputLimit)
                            result = result with { Output = result.Output[^RemoteTaskRules.OutputLimit..], Truncated = true };
                        if (stepStop.IsCancellationRequested)
                            result = result with { Success = false, Error = result.Error ?? "Step cancelled or deadline exceeded; partial output retained." };
                        if (result.Success && !string.IsNullOrEmpty(step.ExpectedText) && !result.Output.Contains(step.ExpectedText, StringComparison.Ordinal))
                            result = result with { Success = false, Error = "Expected text was not found in the bounded step output." };
                    }
                    catch (Exception ex)
                    {
                        result = new(false, "", Error: ex is OperationCanceledException
                            ? "Step cancelled or deadline exceeded; mutating actions were not replayed."
                            : ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException ? ex.Message : "Step failed: " + ex.GetType().Name);
                    }

                    cancelledOrTimedOut = stepStop.IsCancellationRequested;
                    knownFailure = result.KnownCompletion && !cancelledOrTimedOut;
                    var output = result.Output ?? "";
                    var artifactAttempt = repairs * 3 + attempt;
                    var artifact = new RemoteTaskArtifact(task.Artifacts.Count, step.Id, step.Stage, step.ToolId, artifactAttempt,
                        result.Success, output.Length <= RemoteTaskRules.OutputLimit ? output : output[^RemoteTaskRules.OutputLimit..],
                        result.Truncated || output.Length > RemoteTaskRules.OutputLimit, result.ExitCode, DateTimeOffset.UtcNow,
                        result.Error is { Length: > 2000 } e ? e[..2000] : result.Error);
                    task = Save(task with { Artifacts = task.Artifacts.Append(artifact).ToArray() });
                    active.Stop.Token.ThrowIfCancellationRequested();
                    if (result.Success)
                    {
                        task = Save(task with { Snapshot = task.Snapshot with { CompletedSteps = task.Snapshot.CompletedSteps + 1, UpdatedAt = DateTimeOffset.UtcNow } });
                        succeeded = true;
                        break;
                    }

                    failureArtifact = artifact;
                    if (cancelledOrTimedOut) break;
                    if (attempt < step.MaxAttempts) await Task.Delay(200 * attempt, active.Stop.Token).ConfigureAwait(false);
                }

                if (succeeded) break;

                if (failureArtifact is not null && RemoteTaskAdaptiveRules.CanRepair(
                        task.Plan.ExecutionMode, repairs, cancelledOrTimedOut, _adaptive is not null))
                {
                    var replacement = await _adaptive!.RepairAsync(task.Plan, step, failureArtifact, repairs + 1, active.Stop.Token).ConfigureAwait(false);
                    if (replacement is not null)
                    {
                        RemoteTaskAdaptiveRules.ValidateReplacement(step, replacement);
                        var repairPlan = task.Plan with { Steps = [replacement] };
                        RemoteTaskRules.Validate(repairPlan);
                        ValidateTools(repairPlan);
                        step = replacement;
                        repairs++;
                        continue;
                    }
                }

                var failedSource = FrontendWorkspaceState.Capture(task.Snapshot.Project, active.Stop.Token);
                var frontendFailure = FrontendChangeClassifier.Classify(task.Plan.Goal, task.Plan.Steps,
                    failedSource.ChangedSince(task.SourceBaseline).ToArray(), task.Plan.VerificationSpec, failedSource.Files.Keys.ToArray()).IsFrontend;
                var repairable = knownFailure && (RequiresCodingQa(task, failedSource, frontendFailure) || task.Plan.VerificationSpec is not null);
                task = Save(task with { KnownExecutionFailure = repairable,
                    Snapshot = task.Snapshot with { Status = repairable ? "NEEDS_REPAIR" : "FAILED", CurrentStep = null,
                        Error = failureArtifact?.Error, UpdatedAt = DateTimeOffset.UtcNow } });
                return (task, false);
            }
        }
        return (task, true);
    }

    private async Task<(StoredRemoteTask Task, bool Success)> VerifyAgenticGoalAsync(StoredRemoteTask task, Active active)
    {
        for (var repairRound = 0; ; repairRound++)
        {
            active.Stop.Token.ThrowIfCancellationRequested();
            var source = FrontendWorkspaceState.Capture(task.Snapshot.Project, active.Stop.Token);
            var requirement = FrontendChangeClassifier.Classify(task.Plan.Goal, task.Plan.Steps,
                source.ChangedSince(task.SourceBaseline).ToArray(), task.Plan.VerificationSpec, source.Files.Keys.ToArray());
            var spec = task.Plan.VerificationSpec;
            List<FrontendEvidence>? mixedEvidence = null;
            var mixedRunId = Guid.NewGuid().ToString("N");
            if (RequiresCodingQa(task, source, requirement.IsFrontend))
            {
                Func<AgentExecutionContext, Task>? beforeChecks = spec is not null && task.Plan.CodingVerification?.Mode == "required"
                    ? async context => { mixedEvidence = await CollectRenderedFrontendEvidenceAsync(task, active, spec, mixedRunId, source.Revision, context).ConfigureAwait(false); }
                    : null;
                var coding = await VerifyCodingGoalAsync(task, active, source, beforeChecks).ConfigureAwait(false);
                task = coding.Task;
                if (mixedEvidence is not null)
                {
                    var mixedResult = FrontendVerificationGate.Evaluate(requirement, mixedEvidence, spec, null, mixedRunId, source.Revision);
                    task = Save(task with { FrontendEvidence = mixedEvidence, QaRunId = mixedRunId, QaSourceRevision = source.Revision,
                        Snapshot = task.Snapshot with { Verification = ToProtocolVerificationSummary(requirement, mixedResult) } });
                }
                if (!coding.Success) return (task, false);
                source = FrontendWorkspaceState.Capture(task.Snapshot.Project, active.Stop.Token);
                var artifactsFresh = true;
                try { ValidateCodingArtifactTargets(task); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException) { artifactsFresh = false; }
                if (!source.Complete || task.CodingEvidence?.SourceAfter != source.Revision || !artifactsFresh)
                {
                    task = SaveCoding(task, task.CodingEvidence! with { Passed = false, State = "stale",
                        SourceAfter = source.Complete ? source.Revision : null,
                        Reason = "Source or a measured artifact changed after coding checks; fresh verification is required." }, "NEEDS_VERIFICATION");
                    return (task, false);
                }
            }
            if (!requirement.IsFrontend && (_agentic is null || task.Plan.ExecutionMode != "AUTONOMOUS"))
            {
                var none = new RemoteTaskVerificationSummary(false, false, "none", [], [], [])
                { State = "not_required", SourceRevision = source.Complete ? source.Revision : null };
                return (Save(task with { GoalVerificationPassed = true, Snapshot = task.Snapshot with { Verification = none } }), true);
            }
            if (task.Plan.ExecutionMode == "READ_ONLY")
            {
                var skipped = new RemoteTaskVerificationSummary(requirement.IsFrontend, false, "frontend", [], [], [])
                { State = "not_run", NextAction = "Read-only execution did not perform browser QA. Create a NORMAL verification task to exercise the UI." };
                return (Save(task with { Snapshot = task.Snapshot with { Verification = skipped } }), true);
            }

            RequireArmed();
            var runId = mixedEvidence is null ? Guid.NewGuid().ToString("N") : mixedRunId;
            task = Save(task with
            {
                QaRunId = runId, QaSourceRevision = source.Complete ? source.Revision : null,
                FrontendEvidence = [], VisualReview = null, GoalVerificationPassed = false,
                Snapshot = task.Snapshot with { Status = "VERIFYING", CurrentStep = "goal.verify", Error = null, UpdatedAt = DateTimeOffset.UtcNow }
            });
            if (requirement.IsFrontend && (spec is null || !source.Complete))
            {
                var reason = spec is null
                    ? "Provide verificationSpec with the real target, readiness locator, target interaction/postcondition and viewport matrix."
                    : source.Error ?? "A complete source revision is required.";
                var summary = new RemoteTaskVerificationSummary(true, false, requirement.IsVisual ? "frontend-visual" : "frontend",
                    [], requirement.Required.Select(k => k.ToString()).ToArray(), [])
                { State = "not_run", SourceRevision = task.QaSourceRevision, VerificationRunId = runId, NextAction = reason };
                return (Save(task with { Snapshot = task.Snapshot with { Status = "NEEDS_VERIFICATION", CurrentStep = null,
                    Verification = summary, Error = reason, UpdatedAt = DateTimeOffset.UtcNow } }), false);
            }

            var evidence = mixedEvidence ?? (requirement.IsFrontend
                ? await CollectRenderedFrontendEvidenceAsync(task, active, spec!, runId, source.Revision).ConfigureAwait(false)
                : new List<FrontendEvidence>());
            var after = FrontendWorkspaceState.Capture(task.Snapshot.Project, active.Stop.Token);
            var sourceStable = after.Complete && source.Revision == after.Revision;
            if (requirement.IsFrontend && !sourceStable)
                evidence.Add(Measured(task, runId, source.Revision, spec!.Url, FrontendEvidenceKind.TargetIdentity,
                    false, "Source changed during browser verification. Capture new evidence after the edit completes."));
            task = Save(task with { FrontendEvidence = evidence });

            var verification = RemoteTaskGoalVerification.Passed();
            if (_agentic is not null && task.Plan.ExecutionMode == "AUTONOMOUS")
            {
                var debt = FrontendVerificationGate.DescribeDebt(requirement, evidence, spec, null, runId, source.Revision);
                var promptLayers = new CodingPromptAssembler().Assemble(new CodingPromptRequest(task.Plan.Goal,
                    task.Snapshot.Project, _registry.Snapshot.Descriptors,
                    SkillMetadata: _availableSkills.Select(s => $"{s.Id}: {s.Description}").ToArray(),
                    OutcomeSummaries: task.Artifacts.Select(ArtifactSummary).ToArray(), VerificationDebt: debt));
                verification = await _agentic.VerifyGoalAsync(new RemoteTaskGoalContext(task.Plan, task.Snapshot.Project, task.Artifacts)
                {
                    PromptLayers = promptLayers, AvailableSkills = _availableSkills,
                    FrontendRequirement = requirement, FrontendEvidence = evidence
                }.WithSkillLoader(_skillLoader), active.Stop.Token).ConfigureAwait(false);
                // Model assertions are planning/review input, never replacements for measured browser failures.
            }

            var finalSource = FrontendWorkspaceState.Capture(task.Snapshot.Project, active.Stop.Token);
            var finalArtifactsFresh = true;
            try { ValidateCodingArtifactTargets(task); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException) { finalArtifactsFresh = false; }
            if (!finalSource.Complete || finalSource.Revision != source.Revision || !finalArtifactsFresh ||
                task.CodingEvidence is { } codingEvidence && codingEvidence.SourceAfter != finalSource.Revision)
            {
                sourceStable = false;
                if (requirement.IsFrontend)
                    evidence.Add(Measured(task, runId, source.Revision, spec!.Url, FrontendEvidenceKind.TargetIdentity,
                        false, "Source or a measured artifact changed during verification. Fresh evidence is required."));
                if (task.CodingEvidence is { } previousCoding)
                    task = SaveCoding(task, previousCoding with { Passed = false, State = "stale",
                        SourceAfter = finalSource.Complete ? finalSource.Revision : null,
                        Reason = "Source or a measured artifact changed after checks; coding evidence is stale." }, "NEEDS_VERIFICATION");
            }

            var result = FrontendVerificationGate.Evaluate(requirement, evidence, spec, null, runId, source.Revision);
            var passed = verification.Success && result.Passed && sourceStable;
            var reviewOnly = !passed && verification.Success && sourceStable && result.Failed.Count == 0 &&
                result.Missing.Count > 0 && result.Missing.All(k => k == FrontendEvidenceKind.VisualFidelity);
            var nextState = passed ? "passed" : reviewOnly ? "needs_review" : !sourceStable ? "stale" : "failed";
            var repairSteps = verification.RepairSteps?.ToArray() ?? [];
            var willRepair = !passed && !reviewOnly && repairSteps.Length > 0 && repairRound < RemoteTaskAdaptiveRules.MaxRepairs;
            var status = willRepair ? "REPAIRING" : passed ? "COMPLETED" : !sourceStable ? "NEEDS_VERIFICATION" : reviewOnly ? "NEEDS_REVIEW" :
                requirement.IsFrontend || task.CodingEvidence is not null ? "NEEDS_REPAIR" : "FAILED";
            var error = passed ? null : !sourceStable ? "Source or a measured artifact changed after verification; current evidence is stale." : reviewOnly ? "Measured checks passed. Inspect every captured image and submit a bound visual review."
                : verification.Error ?? result.DescribeFailure();
            var summaryResult = ToProtocolVerificationSummary(requirement, result) with
            {
                State = nextState, SourceRevision = task.QaSourceRevision, VerificationRunId = runId,
                Captures = evidence.Where(e => e.Capture is not null).Select(e => e.Capture!).DistinctBy(c => c.CaptureId).ToArray(),
                NextAction = passed ? null : reviewOnly
                    ? "Read each capture with agent_task_capture, submit agent_task_review, then agent_task_complete."
                    : "Inspect failed evidence, submit bounded repair steps with agent_task_repair, or correct the spec and call agent_task_verify."
            };
            var artifact = new RemoteTaskArtifact(task.Artifacts.Count, "goal.verify", "VERIFY", "agent.goal_verifier",
                repairRound + 1, passed, passed ? "Goal verification passed." : ClipGoalText(error ?? "Verification incomplete.", 4000),
                false, null, DateTimeOffset.UtcNow, error);
            task = Save(task with
            {
                GoalVerificationPassed = verification.Success && sourceStable,
                KnownExecutionFailure = passed ? false : task.KnownExecutionFailure,
                Artifacts = task.Artifacts.Append(artifact).ToArray(),
                Snapshot = task.Snapshot with { Verification = summaryResult, Status = status, CurrentStep = null,
                    Error = error, UpdatedAt = DateTimeOffset.UtcNow }
            });
            if (passed) return (task, true);

            if (!willRepair)
                return (task, false);
            // Validate repairs as a fresh ordered sequence; completed stages are not replayed.
            var repairPlan = task.Plan with { Steps = repairSteps };
            RemoteTaskRules.Validate(repairPlan);
            ValidateTools(repairPlan);
            if (task.Plan.Steps.Count + repairSteps.Length > RemoteTaskRules.MaxSteps ||
                task.Plan.Steps.Select(s => s.Id).Intersect(repairSteps.Select(s => s.Id), StringComparer.Ordinal).Any())
                throw new ArgumentException("Repair steps require unique IDs within the bounded task.");
            // Execution history is retained in artifacts; keep the current executable plan stage-valid.
            task = Save(task with
            {
                Plan = Clone(repairPlan), PlanDigest = RemoteTaskStore.Digest(repairPlan),
                FrontendEvidence = [], VisualReview = null, QaRunId = null, QaSourceRevision = null,
                Snapshot = task.Snapshot with { Status = "QUEUED", TotalSteps = task.Snapshot.TotalSteps + repairSteps.Length, Verification = null }
            });
            var execution = await ExecuteStepsAsync(task, active, repairSteps).ConfigureAwait(false);
            task = execution.Task;
            if (!execution.Success) return (task, false);
        }
    }

    private async Task<List<FrontendEvidence>> CollectRenderedFrontendEvidenceAsync(StoredRemoteTask task, Active active,
        FrontendQaSpec spec, string runId, string revision, AgentExecutionContext? verificationContext = null)
    {
        var evidence = new List<FrontendEvidence>();
        if (!_registry.Snapshot.Tools.ContainsKey("browser.qa"))
        {
            evidence.Add(Measured(task, runId, revision, spec.Url, FrontendEvidenceKind.TargetIdentity, false,
                "The installed browser runtime does not expose browser.qa. Update Agent, BrowserService and extension together."));
            return evidence;
        }
        ToolReply reply;
        try
        {
            reply = await _invoke("browser.qa", WireJson.Element(new { spec, browserFamily = "dev" }),
                (verificationContext ?? active.Context) with { CallId = task.Snapshot.TaskId + ":qa:" + runId, SessionCancellation = active.Stop.Token },
                active.Stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or TimeoutException)
        {
            evidence.Add(Measured(task, runId, revision, spec.Url, FrontendEvidenceKind.TargetIdentity, false,
                "Browser QA unavailable: " + ClipGoalText(ex.Message, 1000)));
            return evidence;
        }
        try
        {
            using var parsed = JsonDocument.Parse(reply.Text);
            var data = parsed.RootElement;
            if (!data.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
                !data.TryGetProperty("snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Browser QA returned an incompatible result.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots.EnumerateArray())
            {
                var name = String(snapshot, "name");
                var viewport = spec.Viewports.SingleOrDefault(v => v.Name == name);
                if (viewport is null || !seen.Add(name)) throw new InvalidDataException("Unexpected or duplicate QA viewport.");
                var url = String(snapshot, "url");
                var captureTime = DateTimeOffset.UtcNow;
                FrontendQaCapture? capture = null;
                string? imageError = null;
                try
                {
                    var path = String(snapshot, "artifactPath");
                    var expectedHash = String(snapshot, "screenshotSha256");
                    var actualHash = ValidatePngCapture(path);
                    if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Screenshot hash does not match captured bytes.");
                    capture = new FrontendQaCapture
                    {
                        CaptureId = runId + ":" + name, ArtifactPath = path, Sha256 = actualHash,
                        Width = Int(snapshot, "observedWidth"), Height = Int(snapshot, "observedHeight"),
                        SourceRevision = revision, VerificationRunId = runId, TargetUrl = url,
                        ViewportId = name, CapturedAt = captureTime
                    };
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                { imageError = ex.Message; }

                void Add(FrontendEvidenceKind kind, bool pass, string summary) =>
                    evidence.Add(Measured(task, runId, revision, url, kind, pass, summary, name, capture, captureTime));
                Add(FrontendEvidenceKind.TargetIdentity, Bool(snapshot, "identityPassed"),
                    Bool(snapshot, "identityPassed") ? "Expected route and readiness target were observed." : "Target identity or readiness failed.");
                Add(FrontendEvidenceKind.RenderedDom, Bool(snapshot, "domPresent"),
                    Bool(snapshot, "domPresent") ? "Nonempty rendered state captured after target interactions." : "Rendered state is absent.");
                Add(FrontendEvidenceKind.Screenshot, capture is not null,
                    capture is not null ? "PNG bytes, dimensions and SHA-256 verified." : "Screenshot invalid: " + imageError);
                var dimensions = Int(snapshot, "observedWidth") == viewport.Width && Int(snapshot, "observedHeight") == viewport.Height;
                Add(viewport.Width < 768 ? FrontendEvidenceKind.ResponsiveMobile : FrontendEvidenceKind.ResponsiveDesktop,
                    dimensions && capture is not null && Bool(snapshot, "domPresent"),
                    dimensions ? $"Rendered {viewport.Width}x{viewport.Height} with a separate capture." : "Observed viewport differs from requested dimensions.");
                Add(FrontendEvidenceKind.FrameworkOverlay,
                    snapshot.TryGetProperty("frameworkOverlay", out var overlay) && overlay.ValueKind == JsonValueKind.False,
                    Bool(snapshot, "frameworkOverlay") ? "A visible framework error overlay was detected." : "Visible framework error overlay inspection completed.");
                var consoleHealthy = EmptyArray(snapshot, "consoleErrors") && EmptyArray(snapshot, "consoleWarnings");
                Add(FrontendEvidenceKind.ConsoleHealth, consoleHealthy,
                    consoleHealthy ? "No console errors, warnings or uncaught exceptions during this scenario."
                        : "Console errors: " + JsonSummary(snapshot, "consoleErrors") + "; warnings: " + JsonSummary(snapshot, "consoleWarnings"));
                Add(FrontendEvidenceKind.NetworkHealth, EmptyArray(snapshot, "networkFailures"),
                    EmptyArray(snapshot, "networkFailures") ? "No failed requests were recorded during this scenario."
                        : "Network failures: " + JsonSummary(snapshot, "networkFailures"));
                var stepsOk = snapshot.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array &&
                    steps.GetArrayLength() == spec.Steps.Count && steps.EnumerateArray()
                        .Select((step, index) => Bool(step, "passed") && Int(step, "index") == index && String(step, "action") == spec.Steps[index].Action).All(pass => pass);
                Add(FrontendEvidenceKind.Interaction, stepsOk,
                    stepsOk ? "Every target action and expected postcondition passed for this viewport." : "Target action/postcondition failed: " + JsonSummary(snapshot, "steps"));
                var overflowOk = snapshot.TryGetProperty("overflow", out var overflow) && overflow.ValueKind == JsonValueKind.Object &&
                    overflow.TryGetProperty("horizontal", out var horizontal) && horizontal.ValueKind == JsonValueKind.False &&
                    overflow.TryGetProperty("clipped", out var clipped) &&
                    (clipped.ValueKind == JsonValueKind.False || clipped.ValueKind == JsonValueKind.Array && clipped.GetArrayLength() == 0);
                Add(FrontendEvidenceKind.Overflow, overflowOk,
                    overflowOk ? "No horizontal overflow or clipped target was observed." : "Layout bounds failed: " + JsonSummary(snapshot, "overflow"));
            }
            if (seen.Count != spec.Viewports.Count)
                evidence.Add(Measured(task, runId, revision, spec.Url, FrontendEvidenceKind.Screenshot, false,
                    "Browser QA did not return every requested viewport."));
            if (reply.IsError || !Bool(data, "passed") || !Bool(data, "cleanedUp") || !EmptyArray(data, "errors"))
                evidence.Add(Measured(task, runId, revision, spec.Url, FrontendEvidenceKind.Interaction, false,
                    "Browser QA did not complete successfully: " + JsonSummary(data, "errors")));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            evidence.Add(Measured(task, runId, revision, spec.Url, FrontendEvidenceKind.TargetIdentity, false,
                "Invalid browser QA result: " + ClipGoalText(ex.Message, 1000)));
        }
        return evidence;
    }

    private static FrontendEvidence Measured(StoredRemoteTask task, string runId, string revision, string url,
        FrontendEvidenceKind kind, bool success, string summary, string? viewport = null,
        FrontendQaCapture? capture = null, DateTimeOffset? timestamp = null)
    {
        var observedAt = timestamp ?? DateTimeOffset.UtcNow;
        return new(kind, success, ClipGoalText(summary, 2000), capture?.ArtifactPath,
            new FrontendQaProvenance
            {
                TaskId = task.Snapshot.TaskId, SourceRevision = revision, VerificationRunId = runId,
                TargetUrl = url, ObservationId = capture?.CaptureId ?? runId + ":" + (viewport ?? "run"),
                CapturedAt = observedAt, Producer = "collector", ViewportId = viewport
            }, capture);
    }

    internal static string ValidatePngCapture(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Capture path is not absolute.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 24 or > 4 * 1024 * 1024 || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Capture is missing, linked or exceeds the image size limit.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!header[..8].SequenceEqual(png) || !header[12..16].SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(header[16..20]) <= 0 || BinaryPrimitives.ReadInt32BigEndian(header[20..24]) <= 0)
            throw new InvalidDataException("Capture does not contain a valid PNG header.");
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string String(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int Int(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : -1;
    private static bool Bool(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool EmptyArray(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;
    private static string JsonSummary(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) ? ClipGoalText(value.GetRawText(), 1200) : "(missing)";

    private static RemoteTaskVerificationSummary ToProtocolVerificationSummary(
        FrontendVerificationRequirement requirement, FrontendVerificationResult result) =>
        new(requirement.IsFrontend, result.Passed,
            requirement.IsFrontend ? (requirement.IsVisual ? "frontend-visual" : "frontend") : "goal",
            result.Evidence.Select(item => new RemoteTaskEvidenceSummary(
                item.Kind.ToString(), item.Success, item.Summary, item.Artifact, item.Provenance, item.Capture)).ToArray(),
            result.Missing.Select(item => item.ToString()).ToArray(),
            result.Failed.Select(item => item.ToString()).ToArray());

    private static string ArtifactSummary(RemoteTaskArtifact artifact)
    {
        var output = string.IsNullOrWhiteSpace(artifact.Output) ? "(no output)" : ClipGoalText(artifact.Output, 2000);
        var error = string.IsNullOrWhiteSpace(artifact.Error) ? "" : "; error=" + ClipGoalText(artifact.Error, 1000);
        return $"{artifact.Stage}/{artifact.StepId} [{artifact.ToolId}] success={artifact.Success.ToString().ToLowerInvariant()}; output={output}{error}";
    }

    private static string ClipGoalText(string text, int length) => text.Length <= length ? text : text[..length];
    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
