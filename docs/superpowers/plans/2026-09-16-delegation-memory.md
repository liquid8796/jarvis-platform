# Delegation and Durable Memory 1.0.53 Implementation Plan

> **For agentic workers:** execute inline with `superpowers:executing-plans` and `superpowers:test-driven-development`.

**Goal:** Add bounded child-task lineage/fork-join orchestration and replace the fake SQLite compatibility memory with a durable scoped SQLite store.

**Architecture:** Parent/child metadata is stored in task snapshots and validated locally. Delegation inherits owner/device/project and can only narrow execution mode. A coordinator caps child count/depth and joins terminal child results. `SqliteMemoryStore` becomes a real SQLite implementation with owner/project/namespace scope, provenance, timestamps, expiry and bounded search.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite (reuse existing transitive/direct package when possible), xUnit.

**Spec:** ../specs/2026-09-16-codex-parity-harness-design.md

## Global Constraints
- Child tasks cannot broaden project, owner, device, mode or tool permission.
- Maximum default child depth 3 and children per parent 8; constants are locally enforced.
- Memory reads never return expired rows and all queries are bounded.
- Bump package/assembly/file version to 1.0.53 and update Markdown/CHANGELOG.

### Task 1: Lineage contracts
**Files:** extend shared remote task contracts and `RemoteTaskStore`; update Core/Server tests.
- [ ] RED: parent/root/depth persist across reload; invalid root/depth/self-parent rejects; child mode cannot be broader than parent.
- [ ] GREEN: add optional lineage fields without breaking existing protocol readers.

### Task 2: Fork/join coordinator
**Files:** create `jarvis-agent/src/Jarvis.Agent.Core/RemoteTasks/RemoteTaskDelegation.cs`; integrate narrowly with `RemoteTaskHost`.
- [ ] RED: enforce depth/count, inherit scope, wait for children, propagate cancellation, and never auto-replay a failed child mutation.
- [ ] GREEN: implement child creation through the same persisted host path.

### Task 3: Real SQLite memory
**Files:** inspect current `Autonomous/Memory/SqliteMemoryStore.cs`; replace implementation and add migrations/schema bootstrap; add `tests/Jarvis.Core.Tests/SqliteMemoryStoreTests.cs`.
- [ ] RED: persistence survives reopen, scope isolation, upsert, TTL expiry, bounded search, provenance retention and concurrent writers.
- [ ] GREEN: implement parameterized SQLite queries, WAL/busy timeout and deterministic limits.

### Task 4: Release
**Files:** update `VERSION`, `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `docs/AGENT-HARNESS.md`, `docs/AGENT-TASK-GATEWAY.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/BUILD-STATUS.md`.
- [ ] Run targeted task/memory tests then full solution tests.
- [ ] Commit `feat(agent): add bounded delegation durable memory`.