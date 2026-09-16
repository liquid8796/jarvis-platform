# Codex-Parity Runtime V2 Design

**Status:** Approved in conversation on 2026-09-16 after the local Codex-vs-Jarvis reverse-engineering audit. The user requested implementation of all staged upgrades.

## Goal

Close the production/runtime gaps discovered after Jarvis 1.0.54 and add the highest-value Codex-class orchestration capabilities without weakening Jarvis's local governance model.

Target: **Codex-class orchestration with Jarvis-class local governance**.

## Global security invariants

- Remote control remains outbound-only and device-bound.
- Local Arm/Pause remains authoritative and fail-closed.
- Mutating/sensitive effects always pass through the guarded installed-tool invocation path.
- Catalog/plugin discovery never grants permission.
- Unknown or stale catalog generations fail closed.
- Transport loss never blindly replays mutating/sensitive work.
- Process ownership is connection/task scoped and disconnect/revocation cleanup remains deterministic.
- Code mode receives no CLR/OS/network/filesystem capability except explicit installed-tool calls.
- Saved permissions are constrained capabilities, not wildcard permission to arbitrary command/path/network behavior.

## Patch 1.0.55 — Runtime Closure and Permissions V2

Wire existing adaptive planning and plugin catalog support into the Windows production composition root. Add a concrete adaptive coordinator backed by the existing adaptive execution loop. Load local plugin manifests at runtime, validate/hash them, apply catalog tools, and bind manifest lifecycle hooks to the lifecycle hub.

Make dynamic catalog changes observable over the live agent connection. Handshake includes catalog generation/digest; connected agents send a `catalog.changed` notification when the registry changes. Calls may include an expected catalog generation/digest and are rejected when stale.

Introduce capability leases for sensitive tools. A lease has a scope (`turn` or `session`), expiration, and optional constraints for workspace roots, executable/argument prefixes, and network permission. `process.start` no longer receives unconstrained saved Full Permission; it must satisfy the active capability constraints before execution. Revocation cancels in-flight work.

## Patch 1.0.56 — Protocol V2 and Doctor

Version the control protocol independently of task protocol. Handshake advertises protocol version and capability names. Add typed catalog/process/thread protocol messages while preserving backward compatibility with the existing call/task envelopes.

Add a diagnostic service and CLI `doctor --json` command. Doctor reports version/protocol, registry generation/digest/tool count, plugin catalog state, permission-store state, task-store integrity, owned-process count, computer/browser readiness, and version drift. Sensitive values are redacted.

## Patch 1.0.57 — Process Runtime V2

Add structured process spawning alongside legacy shell `process.start`: executable + argv, working directory, environment overrides, redirected standard streams, bounded output, owned process handles, stdin writes, cancellation, and background event snapshots. Add a PTY contract (`pty`, rows, columns, resize) and implement ConPTY on Windows; unsupported platforms fail explicitly instead of silently downgrading.

Every process remains owned by the Jarvis runtime and is killed on Pause, disconnect, permission revocation, or disposal. Structured spawn respects capability leases for executable prefix, workspace roots and network eligibility.

## Patch 1.0.58 — Durable Thread Runtime

Add a SQLite-backed thread store with explicit Project → Thread → Turn → Item relationships plus append-only events. Persist queue items/revisions, sections, goals, spawn/fork lineage and compact checkpoints. APIs support create/get/list/search, enqueue/reorder/start/delete, append turn/item/event, set goals/sections, fork metadata and bounded timeline reads.

Remote tasks remain execution primitives; threads become the durable orchestration workspace that references task IDs/artifacts rather than replacing task safety rules.

## Patch 1.0.59 — Plugin and Hook Runtime

Turn the manifest SDK into a production runtime. Add startup discovery, filesystem watcher/hot reload, catalog compatibility checking, content hash/provenance, lifecycle hook execution, plugin diagnostics and deterministic unload/reload. Tool additions/removals propagate through dynamic catalog notifications without reconnect.

Hooks are declarative and may only invoke installed tools or registered in-process hook handlers; manifests never cause arbitrary assembly/script loading.

## Patch 1.0.60 — Safe Code Mode and Harness V2

Keep `tool_program.run` and add a second managed JavaScript tool-program runtime using Jint. CLR interop stays disabled. Expose only JSON values plus an async `invokeTool(toolId,args)` bridge that routes through the normal guarded invoker. Enforce source length, statement/time/memory/output/tool-call limits and cancellation.

Improve adaptive execution by running independent ready read-only nodes with bounded concurrency while serializing mutating/sensitive nodes.

Extend the evaluation harness with production-composition scenarios: plugin bootstrap, live catalog hot reload, real adaptive repair, capability lease expiration/revocation, stale catalog rejection, process stdin/cleanup, thread crash/reopen persistence, plugin hook failure isolation, protocol backward compatibility, DAG parallelism and doctor integrity checks. Benchmarks must exercise the shipping composition root, not only isolated engine classes.

## Versioning and documentation

Each release patch 1.0.55 through 1.0.60 must:

1. bump `VERSION`, `Directory.Build.props` `Version`, `AssemblyVersion`, and `FileVersion`;
2. add a top `CHANGELOG.md` entry;
3. update existing Markdown relevant to the patch (`README.md`, `docs/ARCHITECTURE.md`, `docs/AGENT-HARNESS.md`, `docs/SECURITY.md`, `docs/BUILD-STATUS.md`, `docs/TOOL-PARITY.md`, or `docs/API.md` as applicable);
4. follow TDD for behavior changes and run targeted tests plus the full solution verification before completion;
5. produce a conventional commit message.

Vendor `jarvis-code` remains independently versioned unless a patch intentionally changes vendor source.

## Acceptance

The series is complete when 1.0.60 builds and all tests pass, production `AgentRuntime` actually boots the adaptive/plugin runtime, live catalog changes reach connected control-plane peers, capability leases constrain dangerous process operations, protocol/doctor/process/thread/plugin/code-mode surfaces are tested, and the harness contains integration coverage for the production wiring gaps that 1.0.54 missed.