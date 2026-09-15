# Developer Tools and Evaluation Harness 1.0.54 Implementation Plan

> **For agentic workers:** execute inline with `superpowers:executing-plans` and `superpowers:test-driven-development`.

**Goal:** Add structured developer-oriented tools and a repeatable benchmark harness that measures the safety/reliability improvements introduced in 1.0.49–1.0.53.

**Architecture:** Reuse the existing filesystem/process primitives behind focused read-only developer tools: symbol search and structured test execution/report parsing. Add a DAP launch contract that owns the adapter process and never bypasses process permissions. `HarnessEvaluator` runs deterministic scenarios through an `IAgentHarness` abstraction and emits JSON metrics without requiring model credentials.

**Tech Stack:** .NET 10, existing process/filesystem tools, System.Text.Json, xUnit.

**Spec:** ../specs/2026-09-16-codex-parity-harness-design.md

## Global Constraints
- Developer tools are wrappers over existing locally governed operations; no privileged debugger injection or remote code download.
- Evaluation scenarios are deterministic and runnable offline.
- Bump package/assembly/file version to 1.0.54 and update Markdown/CHANGELOG.

### Task 1: Symbol and test tools
**Files:** create focused Core/Windows tool classes and tests; register through the dynamic registry.
- [ ] RED: symbol search bounds files/results and excludes build/VCS directories; test runner returns command, exit code, passed/failed/skipped counts and bounded output; invalid project paths fail closed.
- [ ] GREEN: implement on top of existing workspace/path/process guards.

### Task 2: DAP adapter contract
**Files:** create `jarvis-agent/src/Jarvis.Agent.Core/DeveloperTools/DapAdapterSession.cs`; tests.
- [ ] RED: owned adapter launch validates executable/workspace, captures port/stdio metadata, cancellation kills only the owned process, and arbitrary attach/injection is unsupported.
- [ ] GREEN: implement launch/session lifecycle using owned-process primitives.

### Task 3: Evaluation harness
**Files:** create `jarvis-agent/src/Jarvis.Agent.Core/Evaluation/*`; add tests and `scripts/Run-HarnessEvaluation.ps1` or cross-platform equivalent.
- [ ] Scenarios: dynamic catalog refresh, schema rejection, stale computer state, pause/permission denial, cancellation/no replay, read-only retry, adaptive repair bound, code-mode budget, child-task depth/count and SQLite persistence.
- [ ] Emit machine-readable JSON with scenario success, duration, tool calls, rejected unauthorized actions and notes.

### Task 4: Release and final verification
**Files:** update `VERSION`, `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `docs/AGENT-HARNESS.md`, `docs/TOOL-PARITY.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/BUILD-STATUS.md`.
- [ ] Run evaluator, `dotnet test Jarvis.slnx --nologo`, inspect final diff/log and ensure all six versions/commits are present.
- [ ] Commit `feat(devtools): add harness evaluation tooling`.