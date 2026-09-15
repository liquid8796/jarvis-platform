# Tool Code Mode and Plugin SDK 1.0.52 Implementation Plan

> **For agentic workers:** execute inline with `superpowers:executing-plans` and `superpowers:test-driven-development`.

**Goal:** Add conditional multi-tool orchestration without an unrestricted shell runtime and formalize locally installed plugin manifests/lifecycle hooks.

**Architecture:** `ToolProgramEngine` interprets a small JSON AST with call/set/if/forEach/assert/return instructions under hard instruction, call, time and output budgets. Every call delegates to the existing guarded invoker. `PluginManifest` discovery validates local signed/hash-pinned metadata and maps declared tool IDs to already-instantiated local implementations in `DynamicToolRegistry`; manifests never download or execute remote code.

**Tech Stack:** .NET 10, System.Text.Json, SHA-256, xUnit.

**Spec:** ../specs/2026-09-16-codex-parity-harness-design.md

## Global Constraints
- No arbitrary eval, reflection-based method invocation, shell escape or direct filesystem/network API in code mode.
- Plugin metadata cannot grant permission; local tool policy remains authoritative.
- Bump package/assembly/file version to 1.0.52 and update Markdown/CHANGELOG.

### Task 1: Tool program contracts/interpreter
**Files:** create `jarvis-agent/src/Jarvis.Agent.Core/ToolPrograms/ToolProgramContracts.cs`, `ToolProgramEngine.cs`; create `tests/Jarvis.Core.Tests/ToolProgramEngineTests.cs`.
- [ ] RED: sequential calls, conditional branch, bounded foreach, assertion failure, unknown variable/tool, max instruction/tool-call/time/output enforcement and cancellation.
- [ ] GREEN: implement deterministic JSON-value evaluation and guarded tool-callback invocation.

### Task 2: Expose code mode safely
**Files:** add a local `tool_program.run` IAgentTool and register it through the dynamic registry; tests assert nested calls keep original owner/session correlation and cannot mark FullPermission themselves.
- [ ] Prevent recursion into `tool_program.run` by default.

### Task 3: Plugin SDK
**Files:** create `jarvis-agent/src/Jarvis.Agent.Core/Plugins/PluginManifest.cs`, `PluginCatalog.cs`, `PluginValidation.cs`; tests under Core.
- [ ] RED: reject duplicate plugin/tool IDs, incompatible min-agent-version, malformed SHA-256, undeclared implementation, unsupported hook, path traversal and catalog conflicts.
- [ ] GREEN: discover JSON manifests from a local plugin directory, canonicalize/hash files, bind only supplied implementations, project lifecycle subscriptions and tool metadata into registry snapshots.

### Task 4: Release
**Files:** update `VERSION`, `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `docs/AGENT-HARNESS.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/TOOL-PARITY.md`, `docs/BUILD-STATUS.md`.
- [ ] Run targeted Core tests then full solution tests.
- [ ] Commit `feat(agent): add restricted tool code mode plugins`.