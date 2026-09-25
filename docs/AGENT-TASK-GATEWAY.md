# Agent Task Gateway (Approach A)

## 1.0.68 rendered frontend verification

Agentic autonomous tasks now classify frontend-affecting work from goal intent and step argument paths. Common rendered extensions such as `.tsx`, `.jsx`, `.vue`, `.svelte`, `.css`, `.scss`, `.sass`, `.less` and `.html` trigger a typed frontend verification requirement. Visual/layout intent or stylesheet changes add responsive desktop/mobile and overflow checks.

The configured goal verifier can attach `FrontendEvidence` items for target identity, rendered DOM/accessibility state, framework-overlay absence, console health, screenshot capture, target interaction/post-state proof, desktop/mobile viewport checks and overflow/clipping. The latest evidence of each kind is authoritative. Missing or failed required kinds make the effective goal verdict fail even if the coordinator otherwise reports success; those kinds are fed into the next agentic prompt as verification debt when repair steps are available.

`RemoteTaskSnapshot.verification` is an additive optional wire field containing `required`, `passed`, `type`, typed evidence, missing kinds and failed kinds. MCP task output schemas advertise the same strict nested shape while legacy/deterministic tasks may omit the field entirely. This preserves existing task JSON compatibility and lets clients display completion evidence without parsing retained tool text.

## 1.0.67 goal-owned autonomous execution

Goal-only `AUTONOMOUS` tasks can now execute when the Windows/embedding host explicitly injects an `IRemoteTaskAgenticCoordinator`. `READ_ONLY` and `NORMAL` goal-only requests keep the existing `NEEDS_PLAN` behavior, and an autonomous request without an agentic coordinator also stays `NEEDS_PLAN`. Core does not select a model provider, store model credentials, or treat this coordinator as a permission grant.

The coordinator receives bounded deterministic coding prompt layers plus the resolved project and current installed-tool descriptors. Its generated plan must retain the original goal, execution mode, timeout and resolved project, contain at least one step, satisfy the normal 32-step/stage/schema bounds, and reference only current installed tools. The plan is durably saved before execution and all steps continue through the existing Arm/Pause, owner/session, workspace, permission, approval, timeout and no-replay paths.

For agentic autonomous tasks, successful tool calls are no longer sufficient for `COMPLETED`. After planned steps succeed, the coordinator receives bounded task artifacts and independently verifies the goal. A failed goal verdict either fails the task or may request up to two bounded repair rounds. Repair steps are appended to the persisted plan, revalidated as a complete plan, executed by the same guarded runner, and followed by another goal-verification pass. Goal-verifier outcomes are retained as bounded VERIFY artifacts.

`CodingPromptAssembler` orders and bounds base coding policy, rendered frontend/browser policy, workspace/goal context, sorted tool capability metadata, optional skill instructions, execution outcomes and verification debt. These layers are instruction/evidence context only; they never bypass local policy or cause a paid model to be selected automatically.

## 1.0.65 prompt-continuity task ownership

The `agent_task_*` MCP surface now treats `_jarvis.sessionHandle` as optional. With a validated explicit session, task ownership remains owner+agent+session and sibling chats cannot read/cancel that task. Without a handle, task calls remain usable across later prompts under the authenticated owner+enrolled-agent sessionless scope; task IDs stay opaque and owner/device checks still apply. The gateway never treats a missing model-carried handle as loss of the OAuth/device binding.

Creation snapshots the selected project/workspace exactly as before. Explicit-session stop/close only cancels tasks owned by that explicit session; sessionless tasks are not accidentally captured by a later `js_...` cleanup. Transport/reconnect still never replays task mutations with unknown completion.

## 1.0.64 session-aware task ownership

The `agent_task_*` MCP surface now carries the same validated `_jarvis.sessionHandle` as ordinary tools. Requests are scoped to authenticated owner, enrolled agent and creating application session. Task/project/workspace context is snapshotted at creation; child tasks retain parent ownership and may only narrow execution mode. Read, artifact and cancel operations from another chat are denied even when the account matches. Local operator dashboard compatibility is separate from scoped MCP.

Durable task concurrency defaults to 5 and follows live local execution settings; task steps still pass the normal tool permission/resource/call-slot path. Composite calls do not hold a slot while waiting for child calls. Stopping or closing a chat cooperatively cancels its own tasks and process jobs, not siblings. COMPLETED still means submitted steps passed; it is not an independent model verification or an automatic chat notification.

The server owns authentication, exact device routing and audit events. It stores no authoritative task execution state. All task snapshots, plans and bounded result artifacts live in the local agent application-data directory, partitioned by server origin, device and owner. GET/cancel/plan therefore require the device ID in the cookie API; MCP derives it from the OAuth grant.

Transport reuses the authenticated outbound WebSocket with additive task-v1 negotiation. Old peers continue ordinary tool calls; task calls to them fail explicitly. A new task is acknowledged only after durable storage. Queries return persisted status; disconnect cancels active work and does not replay it. Restart marks unfinished work INTERRUPTED. Identical request IDs are idempotent; changed payloads with the same ID conflict.

The previous Autonomous folder contains disconnected abstractions, including an in-memory class named SqliteMemoryStore. This gateway does not pretend those are a configured planner or durable database. Goal-only requests enter NEEDS_PLAN. The client LLM can then supply an ordered installed-tool plan. The agent enforces that plan, local permissions and verification outcomes; it does not generate code or repair patches by itself.

Task modes READ_ONLY, NORMAL and AUTONOMOUS never grant permissions. Each step uses the existing installed-tool schema and local approval gate. READ_ONLY rejects mutation/sensitive tools. Retry applies only to read-only, non-sensitive operations. Long process steps use owned process start/spawn + read/cancel and actual exitCode, not successful process creation. Failed stages stop subsequent stages. No new public agent listener, broker or model endpoint is introduced.

Limits: 2 running tasks per agent, 32 steps, 3 attempts only for safe reads, 1..1800 seconds per step, 1..3600 seconds per task, bounded arguments and artifact output. State and logs can contain private project information: keep the directory local and outside source exports and release packages.

HTTP cookie APIs keep the existing CSRF protection. MCP task tools keep OAuth resource/scope/stamp/device checks. Per-task authorization is checked again on the local agent. New tasks can use selected-device capabilities in Auto or Published policy and cannot use Hidden or uninstalled capabilities; nested task submission is not an installed tool.

Without an injected agentic coordinator this remains supervised client-planned execution. With one, Core supplies bounded planning/verification contracts but does not prove a particular model provider, autonomous coding quality, or superiority to Codex; production restart/deployment and live paid model configuration remain separate concerns.

## Running a task through MCP

Use `agent_task_tools` first to obtain the visible installed descriptors for the OAuth-bound selected device. `id` is the canonical step `toolId`; a catalog display alias is not a tool ID. The client chooses the project, steps and acceptance checks. For a long build prefer `process.spawn` with exact argv; use `process.start` only when shell syntax is intentionally required. The runner treats both as owned process jobs, polls `process.read` until exit and drains the final log pages.

Example arguments to `agent_task_create` (synthetic example, not an automatically executed command):

```json
{
  "taskId": "bbec6d2e-aef5-407f-b6ca-2bbd8fceca95",
  "goal": "Build and test the selected project using cached dependencies",
  "project": "jarvis-platform",
  "executionMode": "NORMAL",
  "timeoutSeconds": 1800,
  "steps": [
    {
      "id": "build",
      "toolId": "process.start",
      "stage": "BUILD",
      "timeoutSeconds": 300,
      "arguments": {
        "command": "dotnet build Jarvis.slnx -c Release --no-restore; exit $LASTEXITCODE",
        "timeoutSeconds": 300
      }
    },
    {
      "id": "test",
      "toolId": "process.start",
      "stage": "TEST",
      "timeoutSeconds": 300,
      "arguments": {
        "command": "dotnet test tests/Jarvis.Core.Tests -c Release --no-restore; exit $LASTEXITCODE",
        "timeoutSeconds": 300
      }
    }
  ]
}
```

Poll `agent_task_get` with `taskId`; fetch `agent_task_artifacts` after progress or completion. The latter returns text artifacts (not arbitrary binary file downloads), at most 20 entries/page and 16,000 retained characters/attempt. Follow `nextOffset` until absent. `expectedText` matches the retained output only. A successfully started process is not a successful build: actual nonzero exitCode fails the step and stops subsequent stages. On Windows explicitly propagate native command exit codes in PowerShell (`exit $LASTEXITCODE`) when composing commands.

Creating without `steps` persists `NEEDS_PLAN` for READ_ONLY/NORMAL and for AUTONOMOUS when no agentic coordinator is configured. With an injected `IRemoteTaskAgenticCoordinator`, goal-only AUTONOMOUS creation queues local planning; otherwise submit `agent_task_plan` with the same taskId, goal, project, mode and overall timeout plus non-empty steps. Existing executing/terminal tasks cannot be externally replanned. Agentic step/goal repairs are bounded to the current task and are always revalidated before execution.

For the cookie HTTP APIs, include `deviceId` in create/plan bodies and in query/cancel URLs. HTTP create/plan return the snapshot directly; MCP returns the same `RemoteTaskReply` as both structured content and legacy JSON-encoded text (since 1.0.62). The server logs operation/outcome/correlation IDs, not goal, command text or artifact contents.

## Structured MCP results - 1.0.62

Every advertised tool has an object-root `outputSchema`, validated against `structuredContent` in OAuth/WebSocket integration tests. Ordinary installed tools use `{ "text": "...", "isError": false }`; image data remains exclusively in image content blocks, encoded using the SDK image factory. No local widget HTML is copied to structured output.

For `agent_task_create`, `agent_task_plan`, `agent_task_get` and `agent_task_cancel`, structured content is the existing task reply: `{ "task": { ... } }` on success, or `{ "error": "...", "errorCode": "..." }` for a remote failure. Gateway validation/exception errors may omit `errorCode` and retain their original plain text. A successful read of a FAILED task is not itself a tool-call error.

`agent_task_artifacts` describes the snapshot and paged artifacts. Each artifact requires `sequence`, `stepId`, `stage`, `toolId`, `attempt`, `success`, `output`, `truncated` and `createdAt`; `exitCode` and `error` are omitted when unavailable. Process exit codes are integers, including negative codes. `nextOffset` is present only when another page exists. Follow it until absent; an empty artifact page is valid. `currentStep`, task error and optional lineage fields follow the same null-omission rules as the wire DTOs.

`agent_task_tools` returns `{ "tools": [ ... ] }` as structured content while its legacy text remains the original JSON descriptor array. Descriptor fields are `id`, `name`, `category`, `description`, `inputSchema`, `readOnly` and `sensitive`. They describe availability, not permission grants.

Tool inputs, OAuth owner/device routing, local Arm/Pause/approval enforcement and the agent wire protocol are unchanged. Since 1.0.88, selected-device capabilities default to Auto, stateful MCP sessions can receive `tools/list_changed`, and permanent `jarvis__tool_search`/`jarvis__tool_call` tools cover stale direct surfaces. The server must still be deployed separately; a source push alone does not update a running service or Agent. See [server README](../jarvis-mcp-server/README.md) for the live-tool contract.

## Runtime, recovery and local controls

Deterministic tasks use `NEEDS_PLAN -> QUEUED -> RUNNING -> COMPLETED|FAILED|CANCELLED|INTERRUPTED`. Agentic AUTONOMOUS tasks may additionally pass through `PLANNING` and `VERIFYING`, and bounded goal repairs return to `QUEUED/RUNNING` before another verification. Cancellation may first be `CANCELLING`. Task budget expiry is FAILED with a deadline error; transport loss is INTERRUPTED. Timeout/cancel preserves already-read partial output. Local Pause, Disconnect and standing-permission revocation cancel active task work and stop owned process jobs. Queries and cancellation remain available when control is paused; new work does not.

Task files are stored in `%LOCALAPPDATA%/JarvisAgent/TaskRuns/<server-and-device-hash>/<owner-hash>/`. Storage uses UTF-8 JSON, flushed temporary files and atomic replacement, not the old in-memory class named SQLite. A per-device lease prevents concurrent writers. There is a cap of 128 records; archive terminal records locally when needed. Plans and outputs may contain private project data and are not encrypted by this task store. Do not upload the task directory or include it in release/source archives.

Server catalog availability is checked at plan acceptance; local permissions are checked for every step. To stop already accepted work, use task cancel, local Pause, or revoke device authorization. A task is not a sandbox: each installed tool retains the workspace/OS access semantics and permission prompts it already had. READ_ONLY rejects sensitive/mutating descriptors and AUTONOMOUS does not confer extra privileges.

Upgrade the server and agent together. Task-v1 is negotiated separately from the base wire protocol; old agents are rejected for task RPC rather than disconnected from ordinary tool use. No incoming agent port or extra event bus is required. This gateway requires the selected agent online for state queries; it does not promise high availability or resumable side effects after crash.

## Verification

Release 1.0.48 passed 177 tests across Core, Windows and Server suites, including real process execution and OAuth MCP task flow. Run `python .\scripts\Verify-TaskGatewayRelease.py` after packaging to reproduce binary-version, CLI smoke, ZIP/gzip integrity, publish-directory SHA-256 parity and prohibited-file checks. Full evidence is in [Build status](BUILD-STATUS.md).

## Optional adaptive repair - 1.0.51

`AUTONOMOUS` now has an optional local coordinator extension point. If a configured coordinator returns a replacement after a known failed step/verification, the replacement must retain the logical step ID, cannot regress stage, is revalidated against the current installed-tool registry, and still requires local authorization. At most two adaptive repairs are attempted.

This does not change goal creation: Jarvis still does not ship or silently configure an LLM planner. Without an injected coordinator, `AUTONOMOUS` behaves like the existing explicit client plan. NORMAL/READ_ONLY execution is unchanged. Cancellation, task deadline or uncertain interruption never enters adaptive repair.

## Child-task lineage - 1.0.53

`agent_task_create` may include optional `parentTaskId`. Child creation remains device-bound and owner-bound by the existing router and additionally requires the parent to exist in that same local task store. The child inherits the parent's resolved project, may only keep or narrow `executionMode`, has maximum lineage depth 3, and each parent can have at most 8 direct children.

Snapshots expose `parentTaskId`, `rootTaskId` and `depth`; these fields survive agent restart because they are part of the durable snapshot. A child task is still an ordinary task for Arm/Pause, schema/permission checks, cancellation, process ownership and no-replay behavior.

## Task plans with the consolidated process surface

Remote task plans start commands with `unified_exec.exec_command`. When a command remains active, the runner observes it through `unified_exec.write_stdin` until an exit code is known; cancellation sends Ctrl+C through the same owned session. Plans referencing the retired `process.start`, `process.spawn`, `process.read`, or `process.cancel` IDs are rejected by live-schema validation. Empty polling does not open an approval prompt, but input and cancellation keep the normal mutation gate.
