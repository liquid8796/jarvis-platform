# Jarvis Agent Harness

## Execution lifecycle

Goal -> Planner -> Executor -> Tool Router -> Artifact -> Verification -> Recovery -> Memory

## Verification pipeline

Stages:

- Build
- Test
- Package

Each verification stage produces artifacts for analysis and recovery.

## Autonomous runtime

Runtime coordinates task execution and stores execution snapshots.

## Test coverage

Harness tests cover execution loop completion and retry policy behavior.

## Architecture rule

Core contains orchestration contracts only. OS and runtime integrations are provided through adapters.

## Executable integration in 1.0.48

`AgentConnection` now hosts `RemoteTasks/RemoteTaskHost` and shares its guarded installed-tool invocation path with ordinary MCP calls. `RemoteProcessRunner` starts, polls and cancels real owned process jobs; `RemoteTaskStore` persists local JSON snapshots and bounded artifacts atomically. The server routes lifecycle RPCs and records audit metadata, without hosting the executor or task-state database.

The older `Autonomous/` folder remains compatibility scaffolding; in particular its `SqliteMemoryStore` is not SQLite and must not be cited as persistence evidence. New task persistence is the separate atomic JSON store. Planning is supplied by the client, not invented by the gateway. See [Task Gateway](AGENT-TASK-GATEWAY.md) for the supported API and limitations.

## Dynamic tool host in 1.0.49

`DynamicToolRegistry` is now the runtime source of truth for installed tools. A snapshot carries the tool implementations, precompiled local schemas, ordered descriptors, a monotonic generation and a canonical SHA-256 descriptor digest. `AgentConnection` and `RemoteTaskHost` resolve the same current snapshot instead of keeping independent constructor-frozen dictionaries. Replacing a catalog changes visibility/schema only; it does not grant standing permission or bypass local approval.

Execution context now has optional `ThreadId`/`TurnId` correlation alongside the existing call/session identity. `AgentLifecycleHub` provides isolated `Interrupt`, `Stop` and `SubagentStop` notifications so browser/computer/plugin runtimes can clean up without one failing listener blocking another.

## Stateful computer execution in 1.0.50

The Windows adapter adds a `ComputerStateTracker` in front of the reused baseline computer tools. Observation states are opaque, session-scoped, bounded and generation-tracked. `computer.computer_batch` is schema-extended with `stateId`; validation happens before baseline execution and the state is invalidated after dispatch because success/failure does not prove the desktop stayed unchanged.

`computer.get_state` provides bounded foreground/focus/accessibility context, while `computer.screenshot` appends fresh state metadata to its normal result. `ToolInventory.Pause()` invalidates every outstanding state. This layer does not weaken baseline frontmost-app checks, app grants or local consent.

## Adaptive execution in 1.0.51

`AdaptivePlan` validates 1..32 actions, dependency existence/cycles, stage names and a 0..3 repair budget. `AdaptiveAgentExecutionLoop` executes dependency-ready actions, verifies each result and asks an injected replanner only for the failed action. A replacement must keep the logical action ID, have no unmet dependencies and consumes the bounded repair budget. Completed actions are never replayed by this loop.

Task Gateway has a separate optional `IRemoteTaskAdaptiveCoordinator` with an even tighter two-repair cap. It is active only for `AUTONOMOUS`, never for cancellation/timeout, and replacement steps are passed through `RemoteTaskRules`, installed schema validation and the guarded local tool invoker. No coordinator is configured by default.
