# Codex-Parity Runtime V2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Ship Jarvis Agent 1.0.55–1.0.60 with production-wired adaptive/plugins, constrained capability leases, protocol diagnostics, structured process runtime, durable thread state, hot-reload hooks, managed safe JavaScript code mode, and production-level harness coverage.

**Architecture:** Extend the existing guarded `AgentConnection`/`DynamicToolRegistry` composition rather than adding a second effect path. New runtime services expose focused contracts in `Jarvis.Agent.Core`; Windows-specific bootstrap/ConPTY stays in `Jarvis.Agent.Windows`; wire contracts stay in `Jarvis.Protocol`. Every release updates docs/version and remains independently buildable/testable.

**Tech Stack:** .NET 10/C# 14, xUnit, System.Net.WebSockets, Microsoft.Data.Sqlite, Windows ConPTY P/Invoke, Jint managed JavaScript engine.

**Spec:** `docs/superpowers/specs/2026-09-16-codex-parity-runtime-v2-design.md`

## Global Constraints

- Preserve outbound-only/device-bound remote control, local Arm/Pause, exact schema validation, no blind replay, owned-resource cleanup, and one guarded installed-tool invocation path.
- Catalog/plugin discovery never grants permission.
- Stale catalog generation/digest fails closed.
- Saved permissions are constrained capabilities, not wildcard arbitrary shell/path/network grants.
- Code mode has no CLR/OS/network/filesystem capability except `invokeTool`.
- Every patch bumps `VERSION`, `Directory.Build.props` Version/AssemblyVersion/FileVersion, `CHANGELOG.md`, and relevant existing Markdown.
- Full solution baseline before changes: Core 120 + Windows 28 + Server 77 = 225 passing tests.

---

### Task 1: Release 1.0.55 — Runtime Closure and Permissions V2

**Files:**
- Modify: `jarvis-agent/src/Jarvis.Agent.Windows/AgentRuntime.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/AgentConnection.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/DynamicToolRegistry.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/ToolPermissionPolicy.cs`
- Create: `jarvis-agent/src/Jarvis.Agent.Core/RemoteTasks/DefaultRemoteTaskAdaptiveCoordinator.cs`
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Permissions/ToolCapabilityLease.cs`
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Plugins/PluginRuntimeBootstrap.cs`
- Modify: `shared/Jarvis.Protocol/AgentProtocol.cs` (or existing wire-contract file containing `WireMessage`/`AgentHello`)
- Test: `tests/Jarvis.Core.Tests/RuntimeClosureTests.cs`
- Test: `tests/Jarvis.Agent.Windows.Tests/AgentRuntimeBootstrapTests.cs`

**Interfaces:**
- Produce `ToolCatalogIdentity(long Generation, string Digest)` from registry snapshot.
- Produce `ToolCapabilityLease` with `ToolId`, `Scope`, `ExpiresUtc`, workspace roots, command prefixes and `AllowNetwork`.
- Produce concrete `DefaultRemoteTaskAdaptiveCoordinator : IRemoteTaskAdaptiveCoordinator` backed by `AdaptiveAgentExecutionLoop`.
- `AgentRuntime` must create one registry/lifecycle/adaptive/plugin-bootstrap graph and pass it into `AgentConnection`.

- [x] **Step 1: Write failing production-wiring tests** asserting `AgentRuntime.Connection.ToolRegistry` contains bootstrapped plugin tools from a temp plugin root and autonomous task requests reach a non-null adaptive coordinator.
- [x] **Step 2: Run targeted tests**: `dotnet test tests/Jarvis.Agent.Windows.Tests/Jarvis.Agent.Windows.Tests.csproj --filter FullyQualifiedName~AgentRuntimeBootstrapTests` and confirm RED because runtime does not load/inject these services.
- [x] **Step 3: Write failing catalog identity tests** asserting registry `Changed` produces a new generation/digest and a call carrying an older expected generation is rejected before tool execution.
- [x] **Step 4: Run Core targeted tests** and confirm RED.
- [x] **Step 5: Implement minimal runtime composition** by constructing `DynamicToolRegistry`, `AgentLifecycleHub`, concrete adaptive coordinator and `PluginRuntimeBootstrap`, then pass them to `AgentConnection`.
- [x] **Step 6: Implement live catalog message**: subscribe to registry `Changed`; while connected send `catalog.changed` with generation/digest/descriptors. Extend `AgentHello` with protocol/catalog identity and `WireMessage` with expected catalog identity fields; reject stale calls before invocation.
- [x] **Step 7: Implement capability leases**: `ToolPermissionPolicy` evaluates active leases before granting FullPermission; `process.start` FullPermission requires matching command/workspace/network constraints. Revocation/expiry raises cancellation event.
- [x] **Step 8: Run targeted tests GREEN**, then `dotnet test Jarvis.slnx --nologo`.
- [x] **Step 9: Update `README.md`, `docs/ARCHITECTURE.md`, `docs/AGENT-HARNESS.md`, `docs/SECURITY.md`, `docs/BUILD-STATUS.md`, `docs/TOOL-PARITY.md`, `CHANGELOG.md`; bump version to 1.0.55 / assembly 1.0.55.0.**
- [x] **Step 10: Commit** `feat(agent): wire runtime capabilities and scoped permissions`.

### Task 2: Release 1.0.56 — Protocol V2 and Doctor

**Files:**
- Modify: `shared/Jarvis.Protocol/*`
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Diagnostics/AgentDoctor.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Cli/Program.cs`
- Test: `tests/Jarvis.Core.Tests/AgentDoctorTests.cs`
- Test: `tests/Jarvis.Server.Tests/AgentProtocolV2Tests.cs`

**Interfaces:**
- `AgentProtocolVersion.Current = 2`; hello advertises capability string set.
- `AgentDoctorSnapshot` is JSON-serializable and contains only redacted operational metadata.

- [x] **Step 1:** Write failing protocol compatibility tests: V2 hello carries protocol/capabilities/catalog identity while server still accepts legacy hello/call envelopes.
- [x] **Step 2:** Run Server targeted tests and confirm RED.
- [x] **Step 3:** Write failing doctor tests for versions, registry identity, plugin/permission/task/process status and redaction.
- [x] **Step 4:** Run Core targeted tests and confirm RED.
- [x] **Step 5:** Implement typed protocol metadata and backward-compatible parsing.
- [x] **Step 6:** Implement `AgentDoctor` and CLI `doctor --json`; no token/path secret content is emitted.
- [x] **Step 7:** Run targeted + full tests.
- [x] **Step 8:** Update `README.md`, `docs/API.md`, `docs/ARCHITECTURE.md`, `docs/BUILD-STATUS.md`, `CHANGELOG.md`; bump to 1.0.56 / 1.0.56.0.
- [x] **Step 9:** Commit `feat(protocol): add v2 capabilities and agent doctor`.

### Task 3: Release 1.0.57 — Process Runtime V2

**Files:**
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/ProcessTools.cs`
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Processes/StructuredProcessSession.cs`
- Create: `jarvis-agent/src/Jarvis.Agent.Windows/Processes/ConPtySession.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Windows/AgentRuntime.cs`
- Test: `tests/Jarvis.Core.Tests/StructuredProcessTests.cs`
- Test: `tests/Jarvis.Agent.Windows.Tests/ConPtySessionTests.cs`

**Interfaces:**
- Tools: `process.spawn`, `process.write_stdin`, `process.resize_pty`, plus existing `process.read`/`process.cancel` compatibility.
- Spawn input uses `fileName`, `arguments[]`, `workingDirectory`, bounded environment map, `pty`, `rows`, `columns`.

- [x] **Step 1:** Write RED tests for argv preservation (no shell interpolation), stdin write/read, output bounds, owned cleanup and lease enforcement.
- [x] **Step 2:** Write Windows RED test for PTY creation/resize when ConPTY is available; unsupported OS path must return explicit error.
- [x] **Step 3:** Implement structured redirected process session in Core.
- [x] **Step 4:** Implement ConPTY wrapper in Windows using `CreatePseudoConsole`, resize/close handles, and cancellation cleanup; no silent fallback when `pty=true`.
- [x] **Step 5:** Register tools in production runtime and ensure StopAll closes sessions.
- [x] **Step 6:** Run targeted + full tests.
- [x] **Step 7:** Update `README.md`, `docs/API.md`, `docs/SECURITY.md`, `docs/TOOL-PARITY.md`, `docs/BUILD-STATUS.md`, `CHANGELOG.md`; bump to 1.0.57 / 1.0.57.0.
- [x] **Step 8:** Commit `feat(process): add owned interactive runtime`.

### Task 4: Release 1.0.58 — Durable Thread Runtime

**Files:**
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Threads/ThreadStore.cs`
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Threads/ThreadModels.cs`
- Create: `jarvis-agent/src/Jarvis.Agent.Core/Threads/ThreadTools.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/AgentConnection.cs`
- Test: `tests/Jarvis.Core.Tests/ThreadStoreTests.cs`

**Interfaces:**
- Models: project, thread, turn, item, event, queued item revision, section, goal, spawn edge, compact checkpoint.
- SQLite store supports create/get/list/search/append/enqueue/reorder/delete/start/fork/timeline with bounded query sizes.

- [x] **Step 1:** Write RED persistence tests that close/reopen SQLite and recover thread/turn/item/event/queue/goal/fork data deterministically.
- [x] **Step 2:** Run targeted tests and confirm RED.
- [x] **Step 3:** Implement schema/migrations/transactions and bounded query APIs using existing Microsoft.Data.Sqlite dependency.
- [x] **Step 4:** Add guarded read/write thread tools and task/artifact references; mutating thread operations remain approval-governed.
- [x] **Step 5:** Run targeted + full tests.
- [x] **Step 6:** Update `README.md`, `docs/ARCHITECTURE.md`, `docs/AGENT-HARNESS.md`, `docs/API.md`, `docs/BUILD-STATUS.md`, `CHANGELOG.md`; bump to 1.0.58 / 1.0.58.0.
- [x] **Step 7:** Commit `feat(thread): add durable event-sourced runtime`.

### Task 5: Release 1.0.59 — Plugin and Hook Runtime

**Files:**
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/Plugins/PluginCatalog.cs`
- Modify/Create: `jarvis-agent/src/Jarvis.Agent.Core/Plugins/PluginRuntimeBootstrap.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/AgentLifecycleHub.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Windows/AgentRuntime.cs`
- Test: `tests/Jarvis.Core.Tests/PluginRuntimeTests.cs`
- Test: `tests/Jarvis.Agent.Windows.Tests/PluginHotReloadTests.cs`

**Interfaces:**
- `PluginRuntimeBootstrap` exposes current snapshot, diagnostics and `Changed` event; uses `FileSystemWatcher` with debounce and atomic catalog replacement.
- Hook dispatch is bounded, isolated and declarative; one plugin failure does not prevent later hooks or unload.

- [x] **Step 1:** Write RED tests for startup discovery, changed-manifest hot reload, removal, hash/version compatibility rejection and hook failure isolation.
- [x] **Step 2:** Run targeted tests and confirm RED.
- [x] **Step 3:** Implement watcher/debounce/atomic reload and lifecycle hook binding.
- [x] **Step 4:** Ensure every reload updates `DynamicToolRegistry`, which emits live catalog delta; unload removes plugin tools without touching built-ins.
- [x] **Step 5:** Run targeted + full tests.
- [x] **Step 6:** Update `README.md`, `docs/ARCHITECTURE.md`, `docs/AGENT-HARNESS.md`, `docs/SECURITY.md`, `docs/TOOL-PARITY.md`, `docs/BUILD-STATUS.md`, `CHANGELOG.md`; bump to 1.0.59 / 1.0.59.0.
- [x] **Step 7:** Commit `feat(plugins): activate managed lifecycle runtime`.

### Task 6: Release 1.0.60 — Safe JavaScript Code Mode, Parallel DAG and Harness V2

**Files:**
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/Jarvis.Agent.Core.csproj` to add pinned Jint package.
- Create: `jarvis-agent/src/Jarvis.Agent.Core/ToolPrograms/SafeScriptEngine.cs`
- Create: `jarvis-agent/src/Jarvis.Agent.Core/AgentCoreHostTools.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/AgentConnection.cs`
- Modify: `jarvis-agent/src/Jarvis.Agent.Core/Autonomous/AdaptiveAgentExecutionLoop.cs`
- Modify/Create evaluation code under existing `jarvis-agent/src/Jarvis.Agent.Core/Evaluation/` and scripts under `scripts/`.
- Test: `tests/Jarvis.Core.Tests/JavaScriptToolProgramTests.cs`
- Test: `tests/Jarvis.Core.Tests/AdaptiveParallelExecutionTests.cs`
- Test: production-composition evaluation tests in Core/Windows suites.

**Interfaces:**
- Tool `tool_script.run` accepts bounded JavaScript source and exposes only async `invokeTool(toolId,argsJson)` plus standard ECMAScript/JSON-safe values.
- No enabled CLR/Node/direct filesystem/network/process APIs; enforce 32 tool calls, 50K source, 128K nested-argument JSON, bounded statements/time/memory/output, and fail the whole script on any nested tool denial/failure.
- Adaptive execution may run independent read-only/non-sensitive ready nodes concurrently up to configured bound; mutating/sensitive nodes serialize through existing guarded invoker.

- [x] **Step 1:** Add Jint dependency only after writing RED tests that direct CLR/type access/import/network/process access fail while `invokeTool` succeeds through a fake guarded invoker.
- [x] **Step 2:** Run JavaScript targeted tests and confirm RED.
- [x] **Step 3:** Implement sandboxed Jint engine with timeout/memory/statement/cancellation limits and async tool bridge; serialize output through existing ToolReply bounds.
- [x] **Step 4:** Write RED parallel-DAG timing/order test: independent read-only nodes overlap; mutating nodes never overlap.
- [x] **Step 5:** Implement bounded parallel ready-node execution without creating a second permission path.
- [x] **Step 6:** Add production-composition harness scenarios for runtime bootstrap, live catalog reload, adaptive repair, lease expiry/revocation, stale catalog, process cleanup/stdin, thread reopen, hook isolation, protocol compatibility, DAG parallelism, doctor integrity.
- [x] **Step 7:** Run harness and full solution tests; run version verification.
- [x] **Step 8:** Update `README.md`, `docs/ARCHITECTURE.md`, `docs/AGENT-HARNESS.md`, `docs/SECURITY.md`, `docs/TOOL-PARITY.md`, `docs/BUILD-STATUS.md`, `CHANGELOG.md`; bump to 1.0.60 / 1.0.60.0.
- [x] **Step 9:** Commit `feat(agent): add safe javascript mode and harness v2`.

### Task 7: Whole-series verification

**Files:** all series files.

- [x] Run `dotnet test Jarvis.slnx --nologo` and require zero failures.
- [x] Run existing harness evaluation script/tool and require all scenarios pass.
- [x] Run version verification and confirm 1.0.60 / assembly 1.0.60.0 across root-owned projects.
- [x] Inspect `git diff master...HEAD --stat`, `git status`, and release commit sequence.
- [x] Perform final review for security invariants, stale-state/catalog handling, plugin unload, lease bypasses, process orphaning, SQLite bounds and code-mode escape hatches.
