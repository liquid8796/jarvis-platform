# Dynamic Tool Host 1.0.49 Implementation Plan

> **For agentic workers:** execute inline with `superpowers:executing-plans` and `superpowers:test-driven-development`.

**Goal:** Make the installed tool catalog dynamically refreshable, correlated and lifecycle-aware without weakening local execution policy.

**Architecture:** `DynamicToolRegistry` owns immutable catalog snapshots and compiled schemas. `AgentConnection` reads a fresh snapshot for handshakes and each invocation, while all calls still pass Arm/Pause, approval and exact-permission checks. `AgentLifecycleHub` provides bounded interrupt/stop/subagent-stop notifications to local components.

**Tech Stack:** .NET 10, System.Text.Json, System.Security.Cryptography, xUnit.

**Spec:** ../specs/2026-09-16-codex-parity-harness-design.md

## Global Constraints
- Preserve no-replay, local Arm/Pause and exact per-tool permission semantics.
- Dynamic discovery changes visibility only; it never grants permission.
- Bump root version/assembly/file version to 1.0.49 and update existing Markdown/CHANGELOG.

### Task 1: Dynamic registry
**Files:** create `jarvis-agent/src/Jarvis.Agent.Core/DynamicToolRegistry.cs`; create `tests/Jarvis.Core.Tests/DynamicToolRegistryTests.cs`.
- [ ] RED: assert generation starts at 1, digest is stable for the same descriptor set, replace increments generation only when the catalog changes, duplicate IDs fail, and schema lookup follows the replaced implementation.
- [ ] GREEN: implement immutable snapshot replacement, SHA-256 digest over canonical ordered descriptors and compiled schemas.
- [ ] Run `dotnet test tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj --filter DynamicToolRegistryTests`.

### Task 2: Correlation and lifecycle
**Files:** modify `jarvis-agent/src/Jarvis.Agent.Core/AgentContracts.cs`; create `jarvis-agent/src/Jarvis.Agent.Core/AgentLifecycleHub.cs`; create `tests/Jarvis.Core.Tests/AgentLifecycleTests.cs`.
- [ ] RED: assert lifecycle observers receive Interrupt/Stop/SubagentStop once, exceptions are isolated, and `AgentExecutionContext` carries optional ThreadId/TurnId/CallId.
- [ ] GREEN: add lifecycle enum/event record plus async notification hub with per-listener isolation.

### Task 3: Wire the dynamic registry
**Files:** modify `jarvis-agent/src/Jarvis.Agent.Core/AgentConnection.cs`, `jarvis-agent/src/Jarvis.Agent.Windows/AgentRuntime.cs` and task host construction as needed; extend existing connection tests.
- [ ] RED: replace a tool after connection construction and assert a subsequent invocation sees the new descriptor/schema/implementation while an old-schema call is rejected.
- [ ] GREEN: resolve registry snapshot at call time; handshake uses the current snapshot; ordinary and task calls share the same guarded invocation path.

### Task 4: Release hygiene
**Files:** update `VERSION`, `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `docs/ARCHITECTURE.md`, `docs/AGENT-HARNESS.md`, `docs/BUILD-STATUS.md`; add/update a verification script so root version, assembly version and documented current version cannot drift.
- [ ] Run targeted tests, then `dotnet test Jarvis.slnx --nologo`.
- [ ] Inspect `git diff` and commit `feat(agent): add dynamic tool host lifecycle`.