# Codex-Parity Agent Harness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans, superpowers:test-driven-development and superpowers:verification-before-completion. The user approved the design on 2026-09-17 and explicitly authorizes each completed patch to be committed and pushed to `master`.

**Goal:** Make Jarvis coding tasks demand agent-owned planning and rendered verification evidence comparable to the observed Codex Desktop harness while preserving existing safety and deterministic task execution.

**Architecture:** Keep vendor/model access outside `Jarvis.Agent.Core`; Core receives bounded coordinator interfaces and assembles deterministic coding context. Layer frontend/visual evidence into completion semantics, load plugin `SKILL.md` instructions without executing plugin code, track browser observation generations, and extend deterministic harness evaluation with engineering-quality signals.

**Tech Stack:** .NET 10, C# 14, ASP.NET Core MCP gateway, existing Windows Chrome bridge, xUnit, existing plugin/task/session stores.

**Spec:** `docs/superpowers/specs/2026-09-17-codex-parity-agent-harness-design.md`

## Global Constraints

- Preserve READ_ONLY/NORMAL explicit-plan semantics and all existing local permission/Arm/session/no-replay gates.
- Goal-only AUTONOMOUS execution is enabled only when an `IRemoteTaskAgenticCoordinator` is configured; otherwise it remains `NEEDS_PLAN`.
- Planner/repair output is always revalidated against `RemoteTaskRules`, current schemas and installed tools before execution.
- Required frontend evidence blocks completion when missing or failed.
- Skills are bounded Markdown instructions and never executable plugin code.
- Browser refs/observations are invalid after navigation or material mutation until refreshed.
- Core remains vendor-neutral; no API keys or silent paid-model selection.
- Patch versions: 1.0.67, 1.0.68, 1.0.69, 1.0.70 with matching assembly/file `.0` versions.
- Every patch updates existing `README.md` and focused existing docs, verifies, commits `type(scope): message`, and pushes `origin master`.

---

### Task 1: Agent-owned planning, prompt context and goal verification — v1.0.67

**Files:**
- Create: `jarvis-agent/src/Jarvis.Agent.Core/RemoteTasks/RemoteTaskAgenticCoordinator.cs`
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Prompting/CodingPromptAssembler.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/RemoteTasks/RemoteTaskHost.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/AgentConnection.cs`
- Modify: `tests/Jarvis.Core.Tests/RemoteTaskAgenticTests.cs` (create)
- Modify: existing `README.md`, `docs/AGENT-TASK-GATEWAY.md`, `CHANGELOG.md`
- Modify: `VERSION`, `Directory.Build.props`

**Interfaces:**

```csharp
public interface IRemoteTaskAgenticCoordinator : IRemoteTaskAdaptiveCoordinator
{
    Task<RemoteTaskPlan> PlanAsync(RemoteTaskPlanningContext context, CancellationToken cancellationToken);
    Task<RemoteTaskGoalVerification> VerifyGoalAsync(RemoteTaskGoalContext context, CancellationToken cancellationToken);
}

public sealed record RemoteTaskGoalVerification(bool Success, string? Error = null, IReadOnlyList<RemoteTaskStep>? RepairSteps = null);
```

`CodingPromptAssembler.Assemble(...)` returns deterministic ordered `CodingPromptLayer` records with bounded total characters.

- [ ] **Step 1:** Add RED Core test: goal-only AUTONOMOUS + injected coordinator enters execution instead of `NEEDS_PLAN`, while goal-only NORMAL still returns `NEEDS_PLAN`.
- [ ] **Step 2:** Run focused test and confirm the current host returns `NEEDS_PLAN`.
- [ ] **Step 3:** Implement coordinator contracts, prompt layers and constructor wiring; preserve legacy coordinator compatibility.
- [ ] **Step 4:** Add RED test where step execution succeeds but goal verifier returns failure; assert task is not `COMPLETED`.
- [ ] **Step 5:** Implement completion goal verification and persist a synthetic VERIFY artifact with bounded error/evidence text.
- [ ] **Step 6:** Add RED test where goal verifier returns bounded repair steps; execute only validated repair steps, then verify again, with max two goal-repair rounds.
- [ ] **Step 7:** Implement bounded goal-repair append loop and snapshot `TotalSteps` updates.
- [ ] **Step 8:** Add prompt-assembler tests for stable layer ordering, tool-summary inclusion and size clipping; implement minimal assembler.
- [ ] **Step 9:** Run focused Core tests and existing task server tests.
- [ ] **Step 10:** Update existing docs; bump 1.0.66 -> 1.0.67; run build/tests/version checks.
- [ ] **Step 11:** Review diff; commit `feat(agent): add goal-driven adaptive task execution`; push `master`.

### Task 2: Frontend rendered verification gate and evidence contract — v1.0.68

**Files:**
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Autonomous/Verification/FrontendVerification.cs`
- Modify: `shared/Jarvis.Protocol/RemoteTaskContracts.cs` to add optional verification summary/evidence metadata without breaking old JSON.
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/RemoteTasks/RemoteTaskHost.cs`
- Create: `tests/Jarvis.Core.Tests/FrontendVerificationTests.cs`
- Modify: `tests/Jarvis.Server.Tests/AgentTaskMcpOutputTests.cs` for additive schema compatibility if protocol DTO output changes.
- Modify: existing `README.md`, `docs/AGENT-TASK-GATEWAY.md`, `CHANGELOG.md`
- Modify: `VERSION`, `Directory.Build.props`

**Interfaces:**

```csharp
public enum FrontendEvidenceKind { TargetIdentity, RenderedDom, FrameworkOverlay, ConsoleHealth, Screenshot, Interaction, ResponsiveDesktop, ResponsiveMobile, Overflow }
public sealed record FrontendEvidence(FrontendEvidenceKind Kind, bool Success, string Summary, string? Artifact = null);
public sealed record FrontendVerificationRequirement(bool IsFrontend, bool IsVisual, IReadOnlySet<FrontendEvidenceKind> Required);
```

- [ ] **Step 1:** Add RED tests for classifier recognizing `.tsx/.jsx/.vue/.svelte/.css/.scss/.html` and visual/layout intent.
- [ ] **Step 2:** Implement deterministic frontend classifier from changed paths + goal text.
- [ ] **Step 3:** Add RED tests proving frontend completion fails without URL/DOM/overlay/console/screenshot/interaction evidence; visual completion additionally requires desktop/mobile/overflow evidence.
- [ ] **Step 4:** Implement `FrontendVerificationGate` and typed evidence/result summaries.
- [ ] **Step 5:** Wire goal-completion verification context so coordinator/host can attach evidence and completion checks enforce required kinds for autonomous coding tasks.
- [ ] **Step 6:** Add additive protocol verification summary fields and MCP structured-output tests.
- [ ] **Step 7:** Run focused Core + Server tests.
- [ ] **Step 8:** Update docs; bump 1.0.67 -> 1.0.68; run build/tests/version checks.
- [ ] **Step 9:** Review diff; commit `feat(frontend): require rendered verification evidence`; push `master`.

### Task 3: Skill instruction loader and persistent browser observation state — v1.0.69

**Files:**
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Plugins/PluginSkillLoader.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/Plugins/PluginRuntimeBootstrap.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/AgentConnection.cs` / prompt context projection to expose compact skill metadata.
- Create: `jarvis-agent/src/Jarvis.Agent.Windows/BrowserObservationTracker.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Windows/SessionBrowserToolSet.cs`
- Modify: `tests/Jarvis.Core.Tests/PluginRuntimeTests.cs`, `PluginCatalogTests.cs`
- Modify: `tests/Jarvis.Agent.Windows.Tests/SessionIsolationTests.cs`
- Modify: existing `README.md`, `docs/AGENT.md`, `CHANGELOG.md`
- Modify: `VERSION`, `Directory.Build.props`

**Interfaces:**

```csharp
public sealed record PluginSkillDescriptor(string PluginId, string Name, string Description, string Path, long Length);
public sealed record PluginSkillDocument(PluginSkillDescriptor Descriptor, string Instructions);
public sealed class BrowserObservationTracker { long Observe(...); void Invalidate(...); bool IsCurrent(...); }
```

- [ ] **Step 1:** Add RED skill tests for metadata discovery from `SKILL.md`, full instruction load, 256 KiB bound, root containment and reparse/symlink escape rejection.
- [ ] **Step 2:** Implement safe loader and expose snapshot metadata/full-load APIs from `PluginRuntimeBootstrap`.
- [ ] **Step 3:** Add RED prompt-context test proving compact metadata is included first and selected full skill instructions are included only when requested.
- [ ] **Step 4:** Implement prompt projection integration.
- [ ] **Step 5:** Add RED Windows tests: read/find creates current observation generation; navigate/click/type/form input invalidates it; stale ref-bearing action is rejected until a fresh observation.
- [ ] **Step 6:** Implement per-suite `BrowserObservationTracker` wrapping existing serialized browser tools without changing tab/session ownership.
- [ ] **Step 7:** Run focused Core + Windows tests.
- [ ] **Step 8:** Update docs; bump 1.0.68 -> 1.0.69; run build/tests/version checks.
- [ ] **Step 9:** Review diff; commit `feat(skills): load coding instructions and track browser state`; push `master`.

### Task 4: Visual-fidelity ledger and Codex-parity engineering benchmarks — v1.0.70

**Files:**
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Autonomous/Verification/VisualFidelityLedger.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/Evaluation/HarnessEvaluator.cs`
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Evaluation/CodingHarnessScenarios.cs`
- Create: `tests/Jarvis.Core.Tests/VisualFidelityTests.cs`
- Modify: `tests/Jarvis.Core.Tests/HarnessEvaluatorTests.cs`
- Create: `tests/Jarvis.Core.Tests/CodingHarnessScenarioTests.cs`
- Modify: existing `README.md`, `docs/AGENT.md`, `docs/BUILD-STATUS.md`, `CHANGELOG.md`
- Modify: `VERSION`, `Directory.Build.props`

**Interfaces:**

```csharp
public enum VisualMismatchCategory { Layout, Typography, Color, Iconography, Overflow, InteractionState }
public sealed record VisualMismatch(VisualMismatchCategory Category, string Description, bool Required = true);
public sealed record EngineeringEvidence(bool Build, bool Tests, bool Browser, bool Console, bool Interaction, bool Visual, int CorrectiveLoops, int EvidenceCount);
```

- [ ] **Step 1:** Add RED visual-ledger tests proving required unresolved mismatches block fidelity completion while resolved/optional items do not.
- [ ] **Step 2:** Implement immutable/bounded ledger and summary projection into frontend verification.
- [ ] **Step 3:** Add RED evaluator tests for engineering evidence aggregation and corrective-loop/evidence metrics.
- [ ] **Step 4:** Extend `HarnessScenarioResult`/metric additively and keep existing latency/security metrics intact.
- [ ] **Step 5:** Add deterministic fixture scenarios for modal repair, responsive clipping, API error UI, stale loading UI, visual regression and backend regression. Each fixture asserts required evidence rather than calling a live paid model.
- [ ] **Step 6:** Run focused evaluation/verification tests.
- [ ] **Step 7:** Update docs; bump 1.0.69 -> 1.0.70; run full `dotnet test Jarvis.slnx -c Release --no-restore`, build/current-version/release checks.
- [ ] **Step 8:** Review entire cumulative diff/history and working tree.
- [ ] **Step 9:** Commit `test(harness): add codex parity coding benchmarks`; push `master`.

## Final verification

- Fresh full test run has zero failures.
- `VERSION` and `Directory.Build.props` both report 1.0.70 / 1.0.70.0.
- `git status` is clean and `master` matches `origin/master`.
- Four implementation commits exist in order and each includes an existing Markdown update plus its version bump.
