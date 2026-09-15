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
