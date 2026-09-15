# Adaptive Harness 1.0.51 Implementation Plan

> **For agentic workers:** execute inline with `superpowers:executing-plans` and `superpowers:test-driven-development`.

**Goal:** Turn the compatibility autonomous loop into a bounded async plan/execute/verify/replan engine while leaving deterministic client-planned task execution unchanged.

**Architecture:** Introduce immutable plan/action/outcome contracts with dependencies and verification rules. `AdaptiveAgentExecutionLoop` executes ready actions through an injected guarded executor, verifies each stage, and invokes a replanner only for failed verification within a bounded repair budget. The Task Gateway opts into this only for `AUTONOMOUS`; `NORMAL` and `READ_ONLY` plans keep current deterministic semantics.

**Tech Stack:** .NET 10, existing Agent.Core autonomous namespace, xUnit.

**Spec:** ../specs/2026-09-16-codex-parity-harness-design.md

## Global Constraints
- Planner/replanner may select only installed tool IDs and cannot bypass local permissions or schema validation.
- No automatic replay of uncertain mutating actions.
- Bump package/assembly/file version to 1.0.51 and update Markdown/CHANGELOG.

### Task 1: Async plan contracts
**Files:** replace/extend `jarvis-agent/src/Jarvis.Agent.Core/Autonomous/AgentExecutionContracts.cs`; create `Autonomous/Planning/AdaptivePlan.cs`; update autonomous tests.
- [ ] RED: reject duplicate action IDs, missing dependencies, dependency cycles, invalid repair budgets and invalid stage ordering.
- [ ] GREEN: implement validated immutable DAG contracts and structured verification rules.

### Task 2: Adaptive execution loop
**Files:** replace `Autonomous/AgentExecutionLoop.cs`; create `Autonomous/Runtime/AdaptiveAgentExecutionLoop.cs` if separation improves compatibility; update tests.
- [ ] RED: run ready dependencies in order, stop dependent nodes after failure, invoke verifier after each action, replan once on verification failure, and stop after repair budget.
- [ ] GREEN: implement async planner/replanner/verifier interfaces and structured outcomes.

### Task 3: Gateway opt-in
**Files:** modify `RemoteTasks/RemoteTaskHost.cs` plus supporting contracts/tests.
- [ ] RED: `NORMAL` remains deterministic; `AUTONOMOUS` can request a bounded repair plan from the injected adaptive coordinator; a repair selecting unknown/sensitive retry-invalid tools is rejected by existing validation.
- [ ] GREEN: wire an optional coordinator without making model inference a requirement; default remains no autonomous planner when none is configured.

### Task 4: Release
**Files:** update `VERSION`, `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `docs/AGENT-HARNESS.md`, `docs/AGENT-TASK-GATEWAY.md`, `docs/ARCHITECTURE.md`, `docs/BUILD-STATUS.md`.
- [ ] Run core/task targeted tests then full solution tests.
- [ ] Commit `feat(agent): add verification driven replanning`.