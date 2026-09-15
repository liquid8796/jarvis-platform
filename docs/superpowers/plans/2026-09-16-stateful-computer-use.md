# Stateful Computer Use 1.0.50 Implementation Plan

> **For agentic workers:** execute inline with `superpowers:executing-plans` and `superpowers:test-driven-development`.

**Goal:** Bind desktop actions to fresh observed state and expose bounded Windows accessibility state so stale coordinates/handles fail closed.

**Architecture:** A Jarvis-side `ComputerStateTracker` wraps the existing computer-use tool family. Observation tools mint opaque state IDs/generations; state-bound action envelopes are validated before the legacy tool executes. A bounded UI Automation observer reports foreground/focus metadata. Existing coordinate-frame/app-grant checks remain authoritative.

**Tech Stack:** .NET 10 Windows, existing JarvisCode computer-use service, UIAutomationClient/UIAutomationTypes, xUnit.

**Spec:** ../specs/2026-09-16-codex-parity-harness-design.md

## Global Constraints
- No input is sent when `stateId` is stale or belongs to another session.
- Denied-app, Arm/Pause and local approval behavior is unchanged.
- Bump package/assembly/file version to 1.0.50 and update existing Markdown/CHANGELOG.

### Task 1: State tracker
**Files:** create `jarvis-agent/src/Jarvis.Agent.Windows/ComputerStateTracker.cs`; create `tests/Jarvis.Agent.Windows.Tests/ComputerStateTrackerTests.cs`.
- [ ] RED: snapshot IDs are unique, generations increase after mutation/explicit invalidation, stale IDs reject, sessions cannot reuse each other's IDs, and bounded history evicts old states.
- [ ] GREEN: implement opaque cryptographic IDs, session scoping and generation validation.

### Task 2: Stateful tool adapter
**Files:** create `jarvis-agent/src/Jarvis.Agent.Windows/StatefulComputerToolAdapter.cs`; modify `ToolInventory.cs`; extend Windows tests.
- [ ] RED: screenshot/observation result includes state metadata; a click/batch with current state succeeds through the fake inner tool; stale state rejects before the inner tool runs; calls without state keep backward-compatible legacy behavior only for read-only observation tools.
- [ ] GREEN: inject optional `stateId` schema into mutating computer tools and wrap execution. Invalidate state after successful mutating input.

### Task 3: Accessibility observation
**Files:** create `jarvis-agent/src/Jarvis.Agent.Windows/WindowsAccessibilitySnapshot.cs`; expose a `computer.get_state` tool through the inventory; add tests around pure snapshot projection helpers.
- [ ] Return foreground process/window title, focused element role/name/value where available, and at most 200 bounded nodes/32 KB serialized output.
- [ ] Treat UIA access failures as a bounded partial snapshot, not permission escalation.

### Task 4: Release
**Files:** update `VERSION`, `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `docs/AGENT-HARNESS.md`, `docs/TOOL-PARITY.md`, `docs/SECURITY.md`, `docs/BUILD-STATUS.md`.
- [ ] Run Windows targeted tests then full `dotnet test Jarvis.slnx --nologo`.
- [ ] Commit `feat(computer): reject stale desktop state`.