# Codex-Parity Agent Harness Design

**Status:** Approved in conversation on 2026-09-16. The user requested implementation of all six staged upgrades.

## Goal

Raise Jarvis Agent from a governed client-planned executor to a state-aware, dynamically extensible and adaptively orchestrated local agent harness while preserving Jarvis's stricter local-control guarantees.

The target is not a Codex clone. The target is: Codex-class runtime ergonomics plus Jarvis-class governance.

## Non-negotiable security invariants

- Keep outbound-only remote control and device-bound server authorization.
- Keep local Arm/Pause as an execution prerequisite.
- Keep exact per-tool permissions; no wildcard Full Permission.
- Validate arguments against the locally installed schema immediately before execution.
- A dynamic catalog never grants permission by itself.
- Never automatically replay mutating/sensitive work after transport loss or uncertain completion.
- New orchestration layers must invoke tools through the same guarded invocation path as ordinary remote calls.
- Plugin/code-mode execution may call published tools only; it receives no unrestricted OS API by default.
- Any newly introduced mutating/sensitive tool remains subject to the normal local approval/standing-permission path.

## Patch 1.0.49 — Dynamic Tool Host and lifecycle

Introduce a mutable `DynamicToolRegistry` as the runtime source of truth. It exposes a stable catalog snapshot with a monotonically increasing generation and SHA-256 digest. AgentConnection resolves descriptors and schemas from the registry at call time rather than from constructor-frozen dictionaries.

Add correlation metadata (`threadId`, `turnId`, `callId`) to execution context without weakening existing `sessionId` semantics. Add lifecycle notifications for interrupt, stop and subagent-stop so registered runtime components can release state deterministically. Keep cancellation and bounded concurrency.

Release hygiene: make version documentation derive from the root version file in verification, and update README/architecture/build docs in every staged release.

## Patch 1.0.50 — Stateful Computer Use

Add a `ComputerStateTracker` that issues opaque snapshot IDs and generations whenever the agent observes desktop state. Every state-bound input action may carry `stateId`; stale IDs fail closed before input dispatch. Batch actions bind to the state captured at the start of the batch and invalidate it after the first mutating input unless a fresh observation is made.

Expose a lightweight UI Automation snapshot containing foreground window identity, focused element metadata and bounded accessibility nodes. Keep coordinate/app-grant checks already present in Jarvis. Prefer targeted window capture when available, with screen capture as a safe fallback; do not weaken denied-app behavior.

## Patch 1.0.51 — Adaptive planning and verification-driven repair

Replace the compatibility-only sequential autonomous loop with explicit async planning contracts. A plan is a bounded DAG of actions with dependencies, execution stage, verification rule and retry/repair budget. The runtime executes ready nodes in dependency order, records structured outcomes, then asks a replanner only after a failed verification.

The default Task Gateway remains deterministic for client-supplied plans. Adaptive execution is opt-in (`AUTONOMOUS`) and may only generate steps whose local tool descriptors already exist. Replanning cannot broaden permission: newly selected mutating/sensitive tools still pass the guarded invocation/approval path.

## Patch 1.0.52 — Restricted Tool Code Mode and Plugin SDK

Add a small policy-aware tool program interpreter rather than unrestricted Node/PowerShell. Programs are JSON instruction lists supporting tool calls, variable assignment, bounded `if`, bounded `forEach`, assertions and return. Limits cover instruction count, tool-call count, elapsed time and output bytes. All calls route through the guarded tool invoker.

Add plugin manifest contracts with plugin ID/version/min-agent-version, tool declarations, permissions, skills and lifecycle hooks. Plugins are discovered from local manifests, validated, hashed and projected into the dynamic registry; discovery does not load arbitrary remote code. A manifest's declared tools must map to locally supplied implementations.

## Patch 1.0.53 — Child-agent delegation and durable memory

Add child task lineage (`parentTaskId`, `rootTaskId`, depth) and bounded fork/join orchestration. Delegated work reuses device/owner/project scope and can never exceed the parent's execution mode or local permissions. Child count and depth are bounded.

Replace the misleading in-memory `SqliteMemoryStore` compatibility class with a real SQLite-backed memory store using the existing Microsoft.Data.Sqlite dependency if present, otherwise add that single package to Agent.Core. Memories are scoped by owner/project/namespace, include provenance/timestamps/TTL, support exact upsert plus bounded search, and redact expired entries on read.

## Patch 1.0.54 — Developer power tools and evaluation harness

Add developer-facing structured tools around existing local capabilities rather than embedding a debugger engine: project symbol search, structured test execution/report parsing, and a DAP adapter launcher contract with owned-process semantics. Add a benchmark/evaluation harness that runs deterministic scenarios against an `IAgentHarness` abstraction and records JSON metrics for success, tool calls, stale-state rejection, cancellation and unauthorized-action rejection.

The release must include regression scenarios for patch/test, retry after read-only failure, stale computer state, cancellation, reconnect/no-replay, permission denial and child task limits.

## Versioning and documentation

Each stage is a real release patch and must:

1. bump `VERSION`, root `Directory.Build.props` package/assembly/file versions;
2. add a top CHANGELOG entry;
3. update existing Markdown relevant to the changed subsystem (`README.md` plus architecture/harness/task/tool/security/build docs as applicable);
4. run targeted RED/GREEN tests and the full solution test suite before its local commit;
5. use a conventional commit message.

Vendor `jarvis-code` remains independently versioned unless a patch intentionally changes vendor code.

## Acceptance

The work is complete when releases 1.0.49 through 1.0.54 exist as sequential local commits on the feature branch, all source and docs compile, the full test suite is green at 1.0.54, and no patch weakens the existing local permission/arm/no-replay invariants.