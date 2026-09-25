using Jarvis.Agent.Core.Autonomous.Verification;
using Jarvis.Agent.Core.Plugins;
using System.Text.Json;
using System.Text.RegularExpressions;
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
                        if (step.ToolId == "unified_exec.exec_command")
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
        if (task.Plan.ExecutionMode != "AUTONOMOUS") return (task, true);

        var frontendRequirement = FrontendChangeClassifier.Classify(task.Plan.Goal, task.Plan.Steps);
        if (_agentic is null && !frontendRequirement.IsFrontend) return (task, true);

        var frontendEvidence = new List<FrontendEvidence>();
        for (var repairRound = 0; ; repairRound++)
        {
            active.Stop.Token.ThrowIfCancellationRequested();
            RequireArmed();
            task = Save(task with { Snapshot = task.Snapshot with { Status = "VERIFYING", CurrentStep = "goal.verify", UpdatedAt = DateTimeOffset.UtcNow } });

            if (frontendRequirement.IsFrontend)
                frontendEvidence.AddRange(await CollectRenderedFrontendEvidenceAsync(task, active, frontendRequirement, repairRound).ConfigureAwait(false));

            var outcomes = task.Artifacts.Select(ArtifactSummary).ToArray();
            var verificationDebt = FrontendVerificationGate.DescribeDebt(frontendRequirement, frontendEvidence);
            RemoteTaskGoalVerification verification;
            if (_agentic is null)
            {
                verification = RemoteTaskGoalVerification.Passed();
            }
            else
            {
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
                verification = await _agentic.VerifyGoalAsync(goalContext, active.Stop.Token).ConfigureAwait(false);
                if (verification.FrontendEvidence is { Count: > 0 } reportedEvidence)
                    frontendEvidence.AddRange(reportedEvidence);
                if (verification.VisualFidelity is { } fidelityLedger)
                    frontendEvidence.Add(fidelityLedger.ToEvidence());
            }

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

    private async Task<IReadOnlyList<FrontendEvidence>> CollectRenderedFrontendEvidenceAsync(
        StoredRemoteTask task, Active active, FrontendVerificationRequirement requirement, int verificationRound)
    {
        var evidence = new List<FrontendEvidence>();
        var target = FindLocalFrontendTarget(task);
        if (target is null)
        {
            evidence.Add(new(FrontendEvidenceKind.TargetIdentity, false,
                "No localhost/127.0.0.1 rendered target was found in the task goal, step arguments, or step output."));
            return evidence;
        }

        var available = _registry.Snapshot.Descriptors.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var requiredTools = new[]
        {
            "browser.navigate", "browser.read_page", "browser.javascript_tool",
            "browser.read_console_messages", "browser.computer", "browser.tabs_close_mcp"
        };
        if (requirement.IsVisual)
            requiredTools = requiredTools.Concat(["browser.resize_window"]).ToArray();
        var missingTools = requiredTools.Where(tool => !available.Contains(tool)).ToArray();
        if (missingTools.Length > 0)
        {
            evidence.Add(new(FrontendEvidenceKind.TargetIdentity, false,
                "Frontend browser unavailable: missing browser tools " + string.Join(", ", missingTools) + "."));
            return evidence;
        }

        var callSequence = 0;
        async Task<ToolReply> Call(string toolId, object arguments)
        {
            var context = active.Context with
            {
                CallId = $"{task.Snapshot.TaskId}:verify:{verificationRound}:{Interlocked.Increment(ref callSequence)}",
                SessionCancellation = active.Stop.Token
            };
            try
            {
                return await _invoke(toolId, WireJson.Element(arguments), context, active.Stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or TimeoutException)
            {
                return ToolReply.Error("Frontend browser unavailable: " + ex.Message);
            }
        }

        var navigate = await Call("browser.navigate", new { url = target, browserFamily = "dev" }).ConfigureAwait(false);
        if (navigate.IsError)
        {
            evidence.Add(new(FrontendEvidenceKind.TargetIdentity, false, navigate.Text, target));
            return evidence;
        }

        evidence.Add(new(FrontendEvidenceKind.TargetIdentity, true, "Rendered target opened in the isolated dev browser.", target));
        var tabId = ParseTabId(navigate.Text);
        if (tabId is null)
        {
            evidence.Add(new(FrontendEvidenceKind.RenderedDom, false,
                "Browser navigation succeeded but no owned tab id was returned.", target));
            return evidence;
        }

        try
        {
            var dom = await Call("browser.read_page", new
            {
                tabId = tabId.Value, filter = "all", max_chars = 20000, browserFamily = "dev"
            }).ConfigureAwait(false);
            evidence.Add(new(FrontendEvidenceKind.RenderedDom,
                !dom.IsError && !string.IsNullOrWhiteSpace(dom.Text),
                dom.IsError ? dom.Text : "Rendered accessibility DOM was captured.",
                ClipGoalText(dom.Text, 2000)));

            var overlay = await Call("browser.javascript_tool", new
            {
                action = "javascript_exec",
                tabId = tabId.Value,
                browserFamily = "dev",
                text = "JSON.stringify((()=>{const selectors=['nextjs-portal','vite-error-overlay','webpack-dev-server-client-overlay','react-error-overlay','[data-nextjs-dialog-overlay]','[data-vite-dev-id]'];const node=selectors.map(s=>document.querySelector(s)).find(Boolean);const body=(document.body?.innerText||'').slice(0,30000);const textError=/(Unhandled Runtime Error|Application error: a client-side exception|Failed to compile|Internal Server Error)/i.test(body);return {present:!!node||textError,marker:node?.tagName||null};})())"
            }).ConfigureAwait(false);
            var overlayOk = !overlay.IsError && overlay.Text.Contains("\"present\":false", StringComparison.OrdinalIgnoreCase);
            evidence.Add(new(FrontendEvidenceKind.FrameworkOverlay, overlayOk,
                overlayOk ? "No framework/runtime error overlay is present." : "Framework/runtime overlay check failed: " + ClipGoalText(overlay.Text, 1000)));

            var console = await Call("browser.read_console_messages", new
            {
                tabId = tabId.Value, onlyErrors = true, pattern = "error|exception|unhandled|failed",
                limit = 100, browserFamily = "dev"
            }).ConfigureAwait(false);
            if (!console.IsError && console.Text.Contains("capture just started", StringComparison.OrdinalIgnoreCase))
            {
                await Call("browser.navigate", new { url = target, tabId = tabId.Value, browserFamily = "dev" }).ConfigureAwait(false);
                console = await Call("browser.read_console_messages", new
                {
                    tabId = tabId.Value, onlyErrors = true, pattern = "error|exception|unhandled|failed",
                    limit = 100, browserFamily = "dev"
                }).ConfigureAwait(false);
            }
            var consoleOk = !console.IsError && console.Text.Contains("No console errors recorded.", StringComparison.OrdinalIgnoreCase);
            evidence.Add(new(FrontendEvidenceKind.ConsoleHealth, consoleOk,
                consoleOk ? "No browser console errors were recorded after page load." : ClipGoalText(console.Text, 1200)));

            var screenshot = await Call("browser.computer", new
            {
                action = "screenshot", tabId = tabId.Value, save_to_disk = true, scale = 0.75, browserFamily = "dev"
            }).ConfigureAwait(false);
            var screenshotOk = !screenshot.IsError && ((screenshot.Images?.Count ?? 0) > 0 ||
                screenshot.Text.Contains("saved", StringComparison.OrdinalIgnoreCase) ||
                screenshot.Text.Contains(".png", StringComparison.OrdinalIgnoreCase));
            evidence.Add(new(FrontendEvidenceKind.Screenshot, screenshotOk,
                screenshotOk ? "Fresh rendered screenshot captured from the dev browser." : ClipGoalText(screenshot.Text, 1200),
                screenshotOk ? ClipGoalText(screenshot.Text, 2000) : null));

            var interaction = await Call("browser.computer", new
            {
                action = "hover", coordinate = new[] { 8, 8 }, tabId = tabId.Value, browserFamily = "dev"
            }).ConfigureAwait(false);
            var postInteraction = interaction.IsError
                ? ToolReply.Error(interaction.Text)
                : await Call("browser.read_page", new
                {
                    tabId = tabId.Value, filter = "interactive", max_chars = 8000, browserFamily = "dev"
                }).ConfigureAwait(false);
            var interactionOk = !interaction.IsError && !postInteraction.IsError;
            evidence.Add(new(FrontendEvidenceKind.Interaction, interactionOk,
                interactionOk ? "A browser input interaction completed and fresh post-interaction DOM was observed."
                    : ClipGoalText(interaction.IsError ? interaction.Text : postInteraction.Text, 1200)));

            if (requirement.IsVisual)
            {
                var desktop = await Call("browser.resize_window", new
                {
                    tabId = tabId.Value, width = 1440, height = 900, browserFamily = "dev"
                }).ConfigureAwait(false);
                evidence.Add(new(FrontendEvidenceKind.ResponsiveDesktop, !desktop.IsError,
                    desktop.IsError ? ClipGoalText(desktop.Text, 1000) : "Desktop viewport 1440x900 rendered successfully."));

                var mobile = await Call("browser.resize_window", new
                {
                    tabId = tabId.Value, width = 390, height = 844, browserFamily = "dev"
                }).ConfigureAwait(false);
                evidence.Add(new(FrontendEvidenceKind.ResponsiveMobile, !mobile.IsError,
                    mobile.IsError ? ClipGoalText(mobile.Text, 1000) : "Mobile viewport 390x844 rendered successfully."));

                var overflow = await Call("browser.javascript_tool", new
                {
                    action = "javascript_exec",
                    tabId = tabId.Value,
                    browserFamily = "dev",
                    text = "JSON.stringify((()=>{const e=document.documentElement;const b=document.body;const width=Math.max(e?.scrollWidth||0,b?.scrollWidth||0);const viewport=e?.clientWidth||window.innerWidth;return {horizontal:width>viewport+1,scrollWidth:width,viewport};})())"
                }).ConfigureAwait(false);
                var overflowOk = !overflow.IsError && overflow.Text.Contains("\"horizontal\":false", StringComparison.OrdinalIgnoreCase);
                evidence.Add(new(FrontendEvidenceKind.Overflow, overflowOk,
                    overflowOk ? "No horizontal overflow detected at the mobile viewport."
                        : "Overflow verification failed: " + ClipGoalText(overflow.Text, 1000)));

                var explicitReference = HasExplicitVisualReference(task.Plan.Goal);
                var structuralFidelity = !desktop.IsError && !mobile.IsError && overflowOk && screenshotOk && overlayOk;
                evidence.Add(new(FrontendEvidenceKind.VisualFidelity,
                    structuralFidelity && !explicitReference,
                    explicitReference
                        ? "An explicit visual reference was requested; semantic reference comparison evidence is still required."
                        : structuralFidelity
                            ? "Structural visual baseline passed using fresh screenshot, desktop/mobile render, overlay and overflow evidence."
                            : "Structural visual baseline failed; inspect responsive, screenshot, overlay and overflow evidence.",
                    screenshotOk ? ClipGoalText(screenshot.Text, 2000) : null));

                await Call("browser.resize_window", new
                {
                    tabId = tabId.Value, width = 1440, height = 900, browserFamily = "dev"
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            await Call("browser.tabs_close_mcp", new { tabId = tabId.Value, browserFamily = "dev" }).ConfigureAwait(false);
        }

        return evidence;
    }

    private static readonly Regex LocalFrontendUrl = new(
        @"(?<url>(?:https?://)?(?:localhost|127\.0\.0\.1|\[::1\])(?::\d{2,5})?(?:/[^\s""'<>]*)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string? FindLocalFrontendTarget(StoredRemoteTask task)
    {
        IEnumerable<string> Candidates()
        {
            yield return task.Plan.Goal;
            foreach (var step in task.Plan.Steps) yield return step.Arguments.GetRawText();
            foreach (var artifact in task.Artifacts)
            {
                yield return artifact.Output;
                if (!string.IsNullOrWhiteSpace(artifact.Error)) yield return artifact.Error!;
            }
        }

        foreach (var candidate in Candidates())
        {
            var match = LocalFrontendUrl.Match(candidate ?? string.Empty);
            if (!match.Success) continue;
            var target = match.Groups["url"].Value.TrimEnd('.', ',', ';', ')', ']', '}');
            if (!target.Contains("://", StringComparison.Ordinal)) target = "http://" + target;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsLoopback)
                return uri.ToString();
        }
        return null;
    }

    private static int? ParseTabId(string text)
    {
        var match = Regex.Match(text ?? string.Empty, @"(?:\btab\s+|\[)(?<id>\d+)(?:\]|\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["id"].Value, out var id) ? id : null;
    }

    private static bool HasExplicitVisualReference(string goal)
    {
        var text = " " + (goal ?? string.Empty).ToLowerInvariant() + " ";
        return text.Contains(" figma ", StringComparison.Ordinal) ||
               text.Contains(" pixel-perfect ", StringComparison.Ordinal) ||
               text.Contains(" pixel perfect ", StringComparison.Ordinal) ||
               text.Contains(" reference image ", StringComparison.Ordinal) ||
               text.Contains(" design reference ", StringComparison.Ordinal) ||
               text.Contains(" match the screenshot ", StringComparison.Ordinal) ||
               text.Contains(" match this screenshot ", StringComparison.Ordinal);
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
