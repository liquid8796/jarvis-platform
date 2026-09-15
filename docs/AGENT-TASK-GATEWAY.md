# Agent Task Gateway (Approach A)

The server owns authentication, exact device routing and audit events. It stores no authoritative task execution state. All task snapshots, plans and bounded result artifacts live in the local agent application-data directory, partitioned by server origin, device and owner. GET/cancel/plan therefore require the device ID in the cookie API; MCP derives it from the OAuth grant.

Transport reuses the authenticated outbound WebSocket with additive task-v1 negotiation. Old peers continue ordinary tool calls; task calls to them fail explicitly. A new task is acknowledged only after durable storage. Queries return persisted status; disconnect cancels active work and does not replay it. Restart marks unfinished work INTERRUPTED. Identical request IDs are idempotent; changed payloads with the same ID conflict.

The previous Autonomous folder contains disconnected abstractions, including an in-memory class named SqliteMemoryStore. This gateway does not pretend those are a configured planner or durable database. Goal-only requests enter NEEDS_PLAN. The client LLM can then supply an ordered installed-tool plan. The agent enforces that plan, local permissions and verification outcomes; it does not generate code or repair patches by itself.

Task modes READ_ONLY, NORMAL and AUTONOMOUS never grant permissions. Each step uses the existing installed-tool schema and local approval gate. READ_ONLY rejects mutation/sensitive tools. Retry applies only to read-only, non-sensitive operations. Long process steps use owned process start/read/cancel and actual exitCode, not successful process creation. Failed stages stop subsequent stages. No new public agent listener, broker or model endpoint is introduced.

Limits: 2 running tasks per agent, 32 steps, 3 attempts only for safe reads, 1..1800 seconds per step, 1..3600 seconds per task, bounded arguments and artifact output. State and logs can contain private project information: keep the directory local and outside source exports and release packages.

HTTP cookie APIs keep the existing CSRF protection. MCP task tools keep OAuth resource/scope/stamp/device checks. Per-task authorization is checked again on the local agent. New tasks cannot run unpublished tool capabilities, and nested task submission is not an installed tool.

This is supervised client-planned execution, not proof of full autonomous LLM engineering or superiority to Codex. Production restart/deployment and live paid model calls are outside this patch.

## Running a task through MCP

Use `agent_task_tools` first to obtain the enabled installed descriptors. `id` is the canonical step `toolId`; a catalog display alias is not a tool ID. The client chooses the project, steps and acceptance checks. For a long build use `process.start` (not an asynchronous shell wrapper); the runner polls `process.read` until the job exits and drains the final log pages.

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

Creating without `steps` persists a `NEEDS_PLAN` task. Submit `agent_task_plan` with the same taskId, goal, project, mode and overall timeout, plus non-empty steps. Existing executing/terminal tasks cannot be replanned; a client that generates a repair must inspect failures and deliberately create a new task ID. There is no autonomous error-to-source-code patch generator in the agent.

For the cookie HTTP APIs, include `deviceId` in create/plan bodies and in query/cancel URLs. HTTP create/plan return the snapshot directly; MCP returns a JSON-encoded `RemoteTaskReply` in text content. The server logs operation/outcome/correlation IDs, not goal, command text or artifact contents.

## Runtime, recovery and local controls

`NEEDS_PLAN -> QUEUED -> RUNNING -> COMPLETED|FAILED|CANCELLED|INTERRUPTED`. Cancellation may first be `CANCELLING`. Task budget expiry is FAILED with a deadline error; transport loss is INTERRUPTED. Timeout/cancel preserves already-read partial output. Local Pause, Disconnect and standing-permission revocation cancel active task work and stop owned process jobs. Queries and cancellation remain available when control is paused; new work does not.

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
