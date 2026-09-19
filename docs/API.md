# API overview

## Session lifetime and interactive cancellation — 1.0.76

Explicit application-session handles no longer have an absolute 30-day expiry. A handle is a protected correlation token bound to the authenticated owner and selected enrolled device; it is not authorization by itself. Each call still requires live OAuth and device authorization, and the Agent still rejects closed/missing sessions. New handles omit `handleExpiresAt`; legacy v1 handles whose embedded timestamp has passed continue to resolve so an otherwise-open session can resume. Explicit session close remains terminal, while `session__stop_work` remains resumable.

Calls in both the `browser` and `computer` categories release their call-owned interactive resource lease when that call is cancelled. This includes `computer.request_access`, so a timed-out permission/grant dialog cannot keep the shared `desktop` resource occupied while its UI unwinds and block a later browser QA call. Filesystem/shell/process retention rules are unchanged.

## Prompt-continuity MCP contract — 1.0.65

Ordinary tools and `agent_task_*` accept `_jarvis.sessionHandle` when the client has one, but no longer require it. If omitted, the gateway generates a bounded `call_<random>` execution ID for that invocation and dispatches it to the authenticated enrolled agent. Sessionless state that legitimately spans later calls is keyed to a stable owner/device isolation scope; explicit `js_...` sessions still use the protected handle for strict workspace/mailbox/browser/session ownership.

`session__open` is optional for ordinary execution and remains the entry point for explicit per-chat coordination. `session__get`, `session__list`, `session__send_message`, `session__read_events`, `session__stop_work`, `workspace__get` and `workspace__set` require an explicit handle. `session__close` is intentionally omitted from normal `tools/list`; the raw/operator/UI close path remains for deliberate terminal cleanup. `session__stop_work` cancels owned work without closing the session.

A missing/invalid handle on an ordinary filesystem/Git/shell/process/computer/browser/task call must not produce `SESSION_REQUIRED`; the same error is still correct for explicit session/workspace management. Gateway rejections before dispatch are audited as `rejected:<code>` with correlation/user/device metadata only; arguments and opaque handles are not persisted.

## Application-session MCP contract — 1.0.64

For negotiated application-session agents, every ordinary tool and `agent_task_*` operation accepts a reserved `_jarvis` object with exactly `sessionHandle`. The gateway authenticates OAuth, checks the protected owner/device binding, strips this envelope, then validates the installed tool schema; 1.0.76 no longer rejects an otherwise-open session solely because a legacy handle timestamp elapsed. An arbitrary deviceId or a sessionId from the session list cannot impersonate a session. The MCP transport stays stateless; clients must not treat an access token or Mcp-Session-Id as a per-chat ID.

Open a fresh logical chat session:
```json
{"label":"Code review"}
```
Call `session__open` with that payload; retain the handle returned in its JSON result. Passing its existing valid handle resumes through `session.get`, not by recreating a closed row. Subsequent file, shell, browser, computer and task calls carry:
```json
{"_jarvis":{"sessionHandle":"<this chat's opaque handle>"},"file_path":"D:\\Work\\App\\README.md"}
```
Choose or clear this session's workspace through `workspace__set`:
```json
{"_jarvis":{"sessionHandle":"<handle>"},"path":"D:\\Work\\App","additionalDirectories":[],"expectedRevision":0}
```
Use the revision actually returned by `workspace__get`; `path: null` clears the folder. This does not alter the agent default or another session. Accepted requests retain their prior workspace even while queued.

Published tools: `session__open`, `session__get`, `session__list`, `session__send_message`, `session__read_events`, `session__close`, `workspace__get`, `workspace__set`. Session metadata includes workspace revision, lifecycle, bounded activity/resource counts and unread events, not handles or automatically captured transcript content. Lists/messages are limited to the authenticated owner and selected execution agent. Messages carry an explicit agent-coordination-data source and cannot grant permissions. Event reads have a cursor, bounded page size, truncation and hasMore indicators.

Process read/stdin/resize/cancel and durable task/artifact operations check creator session ownership as well as account/device. Session close is idempotent, cancels owned work and does not pause siblings. Status/cancel/close use independent bounded capacity; mutating session operations still obey the existing Arm and approval gates. Queue expiry fails before tool execution; disconnected or timed-out mutations with unknown completion must not be replayed automatically.

Key actionable errors: SESSION_REQUIRED, SESSION_CLOSED, WORKSPACE_REQUIRED, WORKSPACE_REVISION_CONFLICT, QUEUE_FULL, QUEUE_TIMEOUT, FILE_READ_REQUIRED and FILE_CHANGED. HTTP 429 is the separate server rate limiter. Existing-file writes require this session's successful current Read, not a revision supplied by another session. Tool-local parameter names and exact schemas remain the discovery source of truth.

Execution settings travel over authenticated agent WSS, not an MCP permission-changing tool: AgentHello advertises capability/settings, execution.settings.changed carries a revision and execution.settings.ack confirms it. Settings control execution capacity, never authorization.

Read the actual controller contracts for exact JSON fields. `/api` uses cookie authentication plus `X-CSRF-TOKEN` for every unsafe operation, including login/register. Fetch `/api/auth/csrf`, retain cookies and refresh CSRF after login/logout. Record DTO validation rejects malformed input. Secret-bearing responses use Cache-Control no-store. Errors are real HTTP errors, not always-200 envelopes.

| Path | Methods / purpose |
|---|---|
| `/api/auth/csrf`, `/session` | GET anti-forgery token/current user |
| `/api/auth/register`, `/login`, `/logout`, `/revoke` | POST account lifecycle / security-stamp revoke |
| `/api/devices` | GET own devices / POST enroll and return one-time token |
| `/api/devices/{id}` | PUT revision-aware edit/enable / DELETE own device |
| `/api/devices/{id}/rotate` | POST rotate own enrollment credential, disconnect existing transport |
| `/api/devices/{id}/tools` | GET own installed capability descriptors |
| `/api/overview`, `/api/activity` | GET metrics / latest 100 own metadata events |
| `/api/admin/tools` | GET list / POST alias to an installed capability |
| `/api/admin/tools/{id}` | GET detail+schema / PUT revision-aware metadata / DELETE |
| `/api/admin/tools/bulk-availability` | POST `{ tools: [{ id, revision }], enabled }`; 1..500 unique selections; admin + CSRF; atomic changes and audits; stale revision 409, missing tool 404 |
| `/api/admin/tools/import` | POST import discovered capabilities, disabled by default |
| `/api/admin/capabilities` | GET capabilities known from enrolled devices |
| `/api/admin/users` | GET / POST administrator user management |
| `/api/admin/users/{id}` | PUT name/email/role/status/password / DELETE; last-admin safeguards |
| `/connect/authorize`, `/token`, `/revoke`, `/register` | OAuth endpoints; see OAUTH-MCP.md |
| `/mcp` | Official MCP SDK Streamable HTTP endpoint; bearer OAuth required |
| `/agent/connect` | WebSocket upgrade; enrollment Authorization header, no browser Origin |
| `/health` | GET anonymous version/liveness; no private health details |

## Agent wire envelope v1 / capability protocol v2

Text JSON frames still use `WireMessage.version = 1`, one logical envelope max 8 MiB and no compression. Fields use camelCase. Initial `hello` contains `{deviceId,version,platform,machineName,tools}` plus additive `protocolVersion`, `capabilities`, task-protocol and catalog identity fields; server returns `welcome` with the negotiated protocol/capability subset. Missing protocol metadata is normalized to legacy protocol 1, so existing peers retain ordinary tool behavior. Capability names are feature discovery only, never authorization.

Manifest is capped at 256 tools, schemas 64 KiB each. `ping` and `pong` contain monotonic timestamps. `catalog.changed` carries the immutable tool generation/digest/descriptors and receives `catalog.ack` after server validation/persistence. Later `call` frames may carry expected catalog generation/digest; the agent rejects stale identity before tool execution. `call` otherwise includes ID, toolId, sessionId, deadlineUtc and arguments. `result` includes ID and `{text,isError,images?,widget?}`. `cancel` targets an in-flight ID. TLS and enrollment authorization are mandatory outside explicitly configured loopback development.

Protocol v2 currently negotiates `task-v1`, `catalog-sync-v1` and `capability-leases-v1`. Unsupported capability names are not negotiated. `jarvis-agent doctor --json` is a local diagnostic command, not a remote RPC; it emits redacted health metadata and no credential, raw local path or tool argument/result.

Server heartbeat 15s; stale peer threshold 50s. Deadlines max five minutes on agent, server default120s/max240s. Four server calls/device; agent bounds parallelism and queues. TCP/WebSocket disconnect cannot indicate whether a mutating tool completed: clients must inspect state, not blindly replay.

## Agent Task Gateway - 1.0.48

Cookie-authenticated APIs (unsafe methods require the existing `X-CSRF-TOKEN`): `POST /api/agent/tasks`, `POST /api/agent/tasks/{taskId}/plan`, `GET /api/agent/tasks/{taskId}?deviceId=...`, `GET /api/agent/tasks/{taskId}/artifacts?deviceId=...&offset=0&limit=20`, `POST /api/agent/tasks/{taskId}/cancel?deviceId=...`. Create/plan return HTTP 202 with the durable task snapshot; query returns 200. Unknown owner/device/task is 404; unauthorized capability is 403; invalid input is 400; busy/conflicting/offline-before-dispatch is 409. Lost acknowledgements include the taskId for safe inspection.

OAuth clients use the standard MCP tools `agent_task_create`, `agent_task_plan`, `agent_task_get`, `agent_task_artifacts`, `agent_task_cancel`, and `agent_task_tools`. Device identity comes from the OAuth grant and cannot be supplied in arguments. These are custom tool names, not an implementation of the MCP native Tasks extension. See [Task Gateway](AGENT-TASK-GATEWAY.md) for schemas, examples and execution semantics.
