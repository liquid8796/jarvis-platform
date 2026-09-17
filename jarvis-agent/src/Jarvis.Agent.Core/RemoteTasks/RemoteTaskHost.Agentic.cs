using Jarvis.Agent.Core.Autonomous.Verification;
using Jarvis.Agent.Core.Plugins;
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
                RemoteTaskArtifact? failureArtifact = null;

                for (var attempt = 1; attempt <= step.MaxAttempts; attempt++)
                {
                    using var stepStop = CancellationTokenSource.CreateLinkedTokenSource(active.Stop.Token);
                    stepStop.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));
                    var context = active.Context with
                    {
                        CallId = task.Snapshot.TaskId + ":" + step.Id + ":r" + repairs + ":" + attempt,
                        SessionCancellation = active.Stop.Token
                    };
                    RemoteStepResult result;
                    try
                    {
                        if (step.ToolId is "process.start" or "process.spawn")
                            result = await RemoteProcessRunner.RunAsync(step, context, _invoke, _cancelJob, stepStop.Token).ConfigureAwait(false);
                        else
                        {
                            var reply = await _invoke(step.ToolId, step.Arguments, context, stepStop.Token).ConfigureAwait(false);
                            result = new(!reply.IsError, reply.Text, Error: reply.IsError ? reply.Text : null);
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

                Save(task with { Snapshot = task.Snapshot with { Status = "FAILED", Error = failureArtifact?.Error, UpdatedAt = DateTimeOffset.UtcNow } });
                return (task, false);
            }
        }
        return (task, true);
    }

    private async Task<(StoredRemoteTask Task, bool Success)> VerifyAgenticGoalAsync(StoredRemoteTask task, Active active)
    {
        if (_agentic is null || task.Plan.ExecutionMode != "AUTONOMOUS") return (task, true);

        var frontendRequirement = FrontendChangeClassifier.Classify(task.Plan.Goal, task.Plan.Steps);
        var frontendEvidence = new List<FrontendEvidence>();
        for (var repairRound = 0; ; repairRound++)
        {
            active.Stop.Token.ThrowIfCancellationRequested();
            RequireArmed();
            task = Save(task with { Snapshot = task.Snapshot with { Status = "VERIFYING", CurrentStep = "goal.verify", UpdatedAt = DateTimeOffset.UtcNow } });
            var outcomes = task.Artifacts.Select(ArtifactSummary).ToArray();
            var verificationDebt = FrontendVerificationGate.DescribeDebt(frontendRequirement, frontendEvidence);
            var skillMetadata = _availableSkills.Select(skill => $"{skill.Id}: {skill.Description}").ToArray();
            var promptLayers = new CodingPromptAssembler().Assemble(new CodingPromptRequest(
                task.Plan.Goal, task.Snapshot.Project, _registry.Snapshot.Descriptors,
                SkillMetadata: skillMetadata, OutcomeSummaries: outcomes, VerificationDebt: verificationDebt));
            var goalContext = new RemoteTaskGoalContext(task.Plan, task.Snapshot.Project, task.Artifacts)
            {
                PromptLayers = promptLayers,
                AvailableSkills = _availableSkills,
                FrontendRequirement = frontendRequirement,
                FrontendEvidence = frontendEvidence.ToArray()
            }.WithSkillLoader(_skillLoader);
            var verification = await _agentic.VerifyGoalAsync(goalContext, active.Stop.Token).ConfigureAwait(false);
            if (verification.FrontendEvidence is { Count: > 0 } reportedEvidence)
                frontendEvidence.AddRange(reportedEvidence);
            if (verification.VisualFidelity is { } fidelityLedger)
                frontendEvidence.Add(fidelityLedger.ToEvidence());

            var frontendResult = FrontendVerificationGate.Evaluate(frontendRequirement, frontendEvidence);
            var goalPassed = verification.Success && frontendResult.Passed;
            var errors = new List<string>();
            if (!verification.Success) errors.Add(verification.Error ?? "Goal verification failed.");
            if (!frontendResult.Passed) errors.Add(frontendResult.DescribeFailure());
            var goalError = errors.Count == 0 ? null : string.Join(" ", errors);
            var summary = ToProtocolVerificationSummary(frontendRequirement, frontendResult);
            task = Save(task with { Snapshot = task.Snapshot with { Verification = summary, UpdatedAt = DateTimeOffset.UtcNow } });

            var detail = ClipGoalText(goalPassed ? "Goal verification passed." : goalError ?? "Goal verification failed.", RemoteTaskRules.OutputLimit);
            var artifact = new RemoteTaskArtifact(task.Artifacts.Count, "goal.verify", "VERIFY", "agent.goal_verifier", repairRound + 1,
                goalPassed, detail, false, null, DateTimeOffset.UtcNow,
                goalPassed ? null : ClipGoalText(goalError ?? "Goal verification failed.", 2000));
            task = Save(task with { Artifacts = task.Artifacts.Append(artifact).ToArray() });
            if (goalPassed) return (task, true);

            var repairSteps = verification.RepairSteps?.ToArray() ?? [];
            if (repairSteps.Length == 0 || repairRound >= RemoteTaskAdaptiveRules.MaxRepairs)
            {
                task = Save(task with { Snapshot = task.Snapshot with { Status = "FAILED", CurrentStep = null,
                    Error = ClipGoalText(goalError ?? "Goal verification failed.", 2000), UpdatedAt = DateTimeOffset.UtcNow } });
                return (task, false);
            }

            var expanded = task.Plan with { Steps = task.Plan.Steps.Concat(repairSteps).ToArray() };
            RemoteTaskRules.Validate(expanded);
            ValidateTools(expanded);
            task = Save(task with
            {
                Plan = Clone(expanded),
                PlanDigest = RemoteTaskStore.Digest(expanded),
                Snapshot = task.Snapshot with { Status = "QUEUED", CurrentStep = null, TotalSteps = expanded.Steps.Count, UpdatedAt = DateTimeOffset.UtcNow }
            });
            var execution = await ExecuteStepsAsync(task, active, repairSteps).ConfigureAwait(false);
            task = execution.Task;
            if (!execution.Success) return (task, false);
        }
    }

    private static RemoteTaskVerificationSummary ToProtocolVerificationSummary(
        FrontendVerificationRequirement requirement, FrontendVerificationResult result) =>
        new(requirement.IsFrontend, result.Passed,
            requirement.IsFrontend ? (requirement.IsVisual ? "frontend-visual" : "frontend") : "goal",
            result.Evidence.Select(item => new RemoteTaskEvidenceSummary(
                item.Kind.ToString(), item.Success, item.Summary, item.Artifact)).ToArray(),
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
