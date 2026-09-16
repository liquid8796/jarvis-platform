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

## Restricted tool programs and plugins in 1.0.52

`ToolProgramEngine` is a deterministic composite orchestrator with explicit instruction, nested-call, elapsed-time, loop-size and accumulated-output limits. It releases the outer execution/interactive semaphores after the program itself is approved, preventing nested tool deadlock; nested tools then acquire their own slots and authorization. `tool_program.run` remains mutating/sensitive so READ_ONLY task plans cannot use it as a policy escape.

`PluginCatalog` validates only local metadata and SHA-256-pinned entry files. It never loads an assembly or downloads code. Declared tool IDs must match implementations explicitly supplied by the local host, and `AgentConnection.ApplyPluginCatalog` replaces only the prior plugin projection while preserving built-in tools.

## Delegation and durable memory in 1.0.53

Task lineage is persisted in the same local snapshots as ordinary task state. A child is created only from an existing same-owner parent on the same bound device; the Agent resolves the child project and rejects any difference from the parent, rejects broader execution modes, caps depth at 3 and direct children at 8. `RemoteTaskDelegation.JoinAsync` polls at most eight child IDs until terminal state and propagates caller cancellation.

The former dictionary-backed `SqliteMemoryStore` now uses parameterized SQLite operations. `MemoryPartition` separates owner/project/namespace; `DurableMemoryEntry` carries provenance plus created/updated/expiry timestamps. WAL and bounded busy retry support concurrent writers, expired rows are removed/excluded on read, and result/query/value sizes are bounded.

## Developer tools and evaluation in 1.0.54

The host bootstrap now adds `developer.symbol_search` and `developer.test` to the same dynamic registry as `tool_program.run`. Symbol search applies a selected-workspace path boundary plus file/result/line-size caps. The test tool uses a fixed `dotnet test` process shape, owns cancellation/cleanup and parses bounded structured counts; because tests may build/write, it is marked mutating+sensitive.

`DapAdapterLauncher` is a non-tool Core contract: an adapter path must be an existing absolute local file, working directory must remain inside selected workspace directories, arguments are bounded, shell execution is disabled and the returned session can kill only its owned process. `HarnessEvaluator` records scenario success/duration/tool/security metrics, while the PowerShell harness runs real test groups offline and emits JSON.

## Production runtime closure in 1.0.55

`AgentRuntime` now constructs the production `DynamicToolRegistry` itself, injects `DefaultRemoteTaskAdaptiveCoordinator`, loads the local pinned plugin catalog and applies it before transport startup. The default coordinator is deliberately conservative: it may repair only currently installed read-only/non-sensitive steps, keeps the same logical step and arguments, and cannot broaden permission or replay an ambiguous mutating action.

Catalog changes are now a live control-plane protocol rather than a local-only event. Agent registry changes emit `catalog.changed`; the server validates/persists the descriptor set and returns `catalog.ack`; future calls are pinned to the acknowledged generation/digest. Invocation checks this identity before tool/schema execution. Capability leases add invocation-time session/turn/workspace/command constraints for dangerous process authority, and expiry/revocation cancels guarded work through the existing policy event.
