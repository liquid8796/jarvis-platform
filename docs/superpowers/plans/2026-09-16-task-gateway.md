# Agent Task Gateway Implementation Plan

> Execute inline using executing-plans and test-driven-development; approval for Approach A is recorded in the conversation.

**Goal:** Run and query owner/device-bound engineering tasks on the local agent through the existing WebSocket and expose HTTP plus MCP task operations.
**Architecture:** Server is a gateway and audit writer, not the executor or task database. Agent persists snapshots and bounded output atomically and invokes installed tools through the same local approval, schema and pause gates as ordinary calls.
**Tech Stack:** Existing .NET 10, ASP.NET Core, System.Text.Json, xUnit, WireSocket; no new packages.
**Spec:** ../../AGENT-TASK-GATEWAY.md

## Global Constraints
- Preserve OAuth device binding, active-user checks, CSRF for cookie APIs, local arm, exact per-tool permissions, deadlines and cancellation.
- No arbitrary automatic replay after transport loss, restart or uncertain process completion.
- No new model credentials, external inference calls, production deployment or vendor modifications.
- A goal without an explicit client-generated plan is NEEDS_PLAN. A plan is not an LLM and must not be represented as one.
- Preserve README and CHANGELOG history/UTF-8. Bump package and assembly once for this cohesive patch to 1.0.48.

## Tasks and verification
- [x] Gateway regression: add tests/Jarvis.Server.Tests/AgentTaskGatewayTests.cs. Anonymous GET /api/agent/tasks/{id} must return 401, authenticated offline device must return 409, missing CSRF must fail. Execute filtered tests and retain the RED log.
- [x] Shared protocol: add RemoteTaskContracts.cs and optional task protocol negotiation and request/reply fields to Messages.cs. Validate IDs, modes, stage ordering, argument and output limits on both ends.
- [x] Local executor: add RemoteTasks/{RemoteTaskStore,RemoteTaskHost,RemoteProcessRunner}. Persist before acknowledgement, reject conflicting repeated task IDs, poll owned jobs to actual exit status, stop on failed stages, retry only non-sensitive read-only tools. Interrupted tasks remain terminal and never resume side effects automatically.
- [x] Agent wiring: extend AgentConnection with negotiated task dispatch. Extract its existing guarded tool invocation for reuse without relaxing ordinary-call protections. Inject socket connection/store root for isolated E2E tests; defaults remain production outbound WebSocket and local application data.
- [x] Server gateway: add IAgentTaskRouter/WsAgentTaskRouter and Application/AgentTaskService. Check current user, exact device and every enabled installed tool before dispatch; keep state on agent. Add Transport/AgentTaskController and AgentTaskMcpTools, including create, submit plan, status, artifacts, cancel. MCP device comes only from the grant.
- [x] E2E: connect the real AgentConnection and ProcessToolSet to TestServer, execute commands in temporary directories, inspect artifacts, test failure, pause/deny, cancellation, duplicate IDs, incompatible peers, cross-user/device access and persisted status after restart.
- [x] Release: update existing docs/version, run full solution build and all three test suites, publish agent and the cached Linux ARM64 server RID (x64 runtime was not available locally), validate ZIP/tar members, binary versions, prohibited files, SHA-256 and smoke-run CLI. Record exact evidence in docs/BUILD-STATUS.md.
- [x] Review: inspect diff and new files, distinguish implemented task execution from unavailable model-driven planning; prepare conventional commit. Do not claim deployment or Codex parity.

## Completion evidence

Release 1.0.48: 83 Core + 17 Windows + 77 Server tests passed (177/177); package and binary verification succeeded. See ../../BUILD-STATUS.md. Review was inline, not a separate agent review. No production deployment or autonomous model calls were performed.
