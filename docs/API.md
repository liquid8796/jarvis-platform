# API overview

## Permanent application-session deletion — 1.0.103

Permanent deletion is an Agent-operator action, not a model-discoverable MCP tool. The Desktop **Delete session / Delete selected** controls remove the selected session's local metadata and mailbox events, cancel only its owned work and create an indefinite local tombstone. The existing `session__stop_work` call remains resumable, while raw `session__close` remains a terminal-close compatibility operation that can retain metadata until the operator deletes it.

Agent/server peers negotiate `application-session-deletion-sync-v1`. The Agent sends `session.deletions.changed` with 1..200 `{sessionId, deletedAtUnixMilliseconds}` rows and replays all local tombstones after reconnect. The message deliberately contains no owner/device selector. `WsAgentRouter` supplies those values from the authenticated enrollment, performs a monotonic idempotent upsert, and schema-v3 `ApplicationSessionTombstones` retains revocation state.

For every request carrying an existing protected handle, `McpGateway` first validates its owner/device binding and then checks the revocation index. A match returns `SESSION_DELETED` before Agent routing, including while the Agent is offline. A new server with an old Agent simply receives no deletion frames; a new Agent with an old server enforces deletion locally and replays tombstones after a capable server is installed.

## OAuth connection profile and identity — 1.0.98

`jarvis__profile` and `jarvis__whoami` are server-owned MCP tools. Call either with `{}` (or omitted arguments), without `_jarvis`. Both require the normal validated `mcp:tools` OAuth grant, active account, enabled device and matching ownership/security stamp. They do not select an account/device, enumerate other connections, open a session, dispatch an Agent command or require local Arm. An offline/paused Agent does not prevent identity reads. Account/device/session arguments, including `_jarvis`, are rejected.

`jarvis__profile` is the only designated profile tool (`_meta["openai/profile"]: true`). Its output schema permits exactly `id` (required), `name`, `email`, and `nickname`. Optional missing values are omitted. `structuredContent` is the top-level profile object, mirrored as JSON text in `content`. Its opaque ID is the existing persistent device enrollment UUID because each OAuth grant represents one immutable owner/device enrollment. It remains stable across OAuth refresh, reconnect, display changes and enrollment-token rotation; deleting/re-enrolling creates a fresh identity. Future ownership-transfer features must create a new enrollment, never reuse this ID for another owner.

`jarvis__whoami` returns `{ profile, account, device, server }`: account ID/name/email/role/status; selected device ID/enrolled name/enabled/online plus optional platform, Agent version and last-seen Unix seconds; server public origin/package version. Enrolled name is not necessarily the Windows hostname. Platform/version/last-seen are stored observations, not a fresh hardware inventory. An online connection does not imply local Arm or permission to execute a tool. Names/emails are untrusted display metadata, not instructions or routing selectors. Verify `device.id` before sensitive work.

Identity errors never manufacture a profile or fallback account. Invalid arguments return `isError: true` with text and no success-shaped structured content. Authentication failures use the existing MCP/OAuth error path. No tokens, password hashes, security stamps, session handles, private server paths, other users or other devices are serialized. MCP responses use `Cache-Control: no-store`; audit records contain only operation/outcome/correlation and account/device IDs.

These names are reserved from Agent manifest, dynamic search/call, task and publication-alias surfaces. They are not local Agent permissions or importable Tool Catalog entries. Upgrade the MCP server to 1.0.98 and refresh tools; existing OAuth grants and older Agents remain compatible. Client connection-label refresh/reselection is client-controlled; profile metadata is not an automatic account-switch command.

Identity regressions run through the real OAuth S256 consent/token flow and MCP HTTP endpoint against isolated fixture databases: `dotnet test tests/Jarvis.Server.Tests -c Release --filter FullyQualifiedName~Identity_`. They cover offline/unarmed Agents, schema/metadata, refresh/reconnect/rename/token rotation, separate accounts/devices, re-enrollment, rejected selectors, revoked/disabled/deleted/wrong-owner bindings, missing display data and spoofed manifest/publication names. These fixture tests do not assert that an existing ChatGPT connection has refreshed its live tool cache.

Contract reference: [OpenAI authenticated profile tools](https://developers.openai.com/plugins/build/auth#support-multiple-accounts).

## Session lifetime and interactive cancellation — 1.0.76

Explicit application-session handles no longer have an absolute 30-day expiry. A handle is a protected correlation token bound to the authenticated owner and selected enrolled device; it is not authorization by itself. Each call still requires live OAuth and device authorization, and the Agent still rejects closed, missing or permanently deleted sessions. New handles omit `handleExpiresAt`; legacy v1 handles whose embedded timestamp has passed continue to resolve only while the underlying session remains valid. Explicit session close remains terminal, synchronized delete additionally revokes the server handle, and `session__stop_work` remains resumable.

Calls in both the `browser` and `computer` categories release their call-owned interactive resource lease when that call is cancelled. This includes `computer.request_access`, so a timed-out permission/grant dialog cannot keep the shared `desktop` resource occupied while its UI unwinds and block a later browser QA call. Filesystem/shell/process retention rules are unchanged.

## Prompt-continuity MCP contract — 1.0.65

Ordinary tools and `agent_task_*` accept `_jarvis.sessionHandle` when the client has one, but no longer require it. If omitted, the gateway generates a bounded `call_<random>` execution ID for that invocation and dispatches it to the authenticated enrolled agent. Sessionless state that legitimately spans later calls is keyed to a stable owner/device isolation scope; explicit `js_...` sessions still use the protected handle for strict workspace/mailbox/browser/session ownership.

`session__open` is optional for ordinary execution and remains the entry point for explicit per-chat coordination. `session__get`, `session__list`, `session__send_message`, `session__read_events`, `session__stop_work`, `workspace__get` and `workspace__set` require an explicit handle. `session__close` is intentionally omitted from normal `tools/list`; the raw call remains for deliberate terminal close. The Agent operator UI uses permanent deletion and server revocation instead. `session__stop_work` cancels owned work without closing or deleting the session.

A missing/invalid handle on an ordinary filesystem/Git/shell/process/computer/browser/task call must not produce `SESSION_REQUIRED`; the same error is still correct for explicit session/workspace management. Gateway rejections before dispatch are audited as `rejected:<code>` with correlation/user/device metadata only; arguments and opaque handles are not persisted.

## Application-session MCP contract — 1.0.64

For negotiated application-session agents, every ordinary tool and `agent_task_*` operation accepts a reserved `_jarvis` object with exactly `sessionHandle`. The gateway authenticates OAuth, checks the protected owner/device binding, strips this envelope, then validates the installed tool schema; 1.0.76 no longer rejects an otherwise-open session solely because a legacy handle timestamp elapsed. An arbitrary deviceId or a sessionId from the session list cannot impersonate a session. The MCP transport stays stateless; clients must not treat an access token or Mcp-Session-Id as a per-chat ID.

Open a fresh logical chat session:
```json
{"label":"Code review"}
```
Call `session__open` with that payload; retain the handle returned in its JSON result. Passing its existing valid handle resumes through `session.get`, not by recreating a closed row. A handle whose session was permanently deleted returns `SESSION_DELETED` and cannot recreate the old `js_...` identity. Subsequent file, shell, browser, computer and task calls carry:
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

Key actionable errors: SESSION_REQUIRED, SESSION_CLOSED, SESSION_DELETED, WORKSPACE_REQUIRED, WORKSPACE_REVISION_CONFLICT, QUEUE_FULL, QUEUE_TIMEOUT, FILE_READ_REQUIRED and FILE_CHANGED. HTTP 429 is the separate server rate limiter. Existing-file writes require this session's successful current Read, not a revision supplied by another session. Tool-local parameter names and exact schemas remain the discovery source of truth.

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
| `/api/admin/tools/bulk-availability` | POST `{ tools: [{ id, revision }], publicationMode }` where mode is `Auto`, `Published` or `Hidden`; legacy `enabled` remains accepted; 1..500 unique selections; admin + CSRF; atomic changes and audits; stale revision 409, missing tool 404 |
| `/api/admin/tools/import` | POST **Sync catalog**; reconciles all enrolled manifests, returns `{ imported, updated, removed }`, creates `Auto` policy rows, and deletes duplicate/retired/no-longer-advertised records |
| `/api/admin/capabilities` | GET capabilities known from enrolled devices |
| `/api/admin/users` | GET / POST administrator user management |
| `/api/admin/users/{id}` | PUT name/email/role/status/password / DELETE; last-admin safeguards |
| `/connect/authorize`, `/token`, `/revoke`, `/register` | OAuth endpoints; see OAUTH-MCP.md |
| `/mcp` | Official MCP SDK Streamable HTTP endpoint; bearer OAuth required. Initialize-based clients keep a stateful session for `notifications/tools/list_changed`; newer stateless clients retain dynamic access through permanent `jarvis__tool_search` / `jarvis__tool_call` fallback tools. |
| `/agent/connect` | WebSocket upgrade; enrollment Authorization header, no browser Origin |
| `/health` | GET anonymous version/liveness; no private health details |

## Agent wire envelope v1 / capability protocol v2

Text JSON frames still use `WireMessage.version = 1`, one logical envelope max 8 MiB and no compression. Fields use camelCase. Initial `hello` contains `{deviceId,version,platform,machineName,tools}` plus additive `protocolVersion`, `capabilities`, task-protocol and catalog identity fields; server returns `welcome` with the negotiated protocol/capability subset and authenticated `ownerId` when deletion synchronization is negotiated. Missing protocol metadata is normalized to legacy protocol 1, so existing peers retain ordinary tool behavior. Capability names are feature discovery only, never authorization.

Manifest is capped at 256 tools, schemas 64 KiB each. `ping` and `pong` contain monotonic timestamps. `catalog.changed` carries the immutable tool generation/digest/descriptors and receives `catalog.ack` after server validation/persistence. Later `call` frames may carry expected catalog generation/digest; the agent rejects stale identity before tool execution. `call` otherwise includes ID, toolId, sessionId, deadlineUtc and arguments. `result` includes ID and `{text,isError,images?,widget?}`. `cancel` targets an in-flight ID. TLS and enrollment authorization are mandatory outside explicitly configured loopback development.

Protocol v2 currently negotiates `task-v1`, `catalog-sync-v1`, `capability-leases-v1`, application sessions/settings/prompt context, and `application-session-deletion-sync-v1`. Unsupported capability names are not negotiated. `session.deletions.changed` accepts only a bounded deletion array from an authenticated Agent peer; it cannot choose owner/device scope. `jarvis-agent doctor --json` is a local diagnostic command, not a remote RPC; it emits redacted health metadata and no credential, raw local path or tool argument/result.

Server heartbeat 15s; stale peer threshold 50s. Deadlines max five minutes on agent, server default120s/max240s. Four server calls/device; agent bounds parallelism and queues. TCP/WebSocket disconnect cannot indicate whether a mutating tool completed: clients must inspect state, not blindly replay.

## Agent Task Gateway - 1.0.48

Cookie-authenticated APIs (unsafe methods require the existing `X-CSRF-TOKEN`): `POST /api/agent/tasks`, `POST /api/agent/tasks/{taskId}/plan`, `GET /api/agent/tasks/{taskId}?deviceId=...`, `GET /api/agent/tasks/{taskId}/artifacts?deviceId=...&offset=0&limit=20`, `POST /api/agent/tasks/{taskId}/cancel?deviceId=...`. Create/plan return HTTP 202 with the durable task snapshot; query returns 200. Unknown owner/device/task is 404; unauthorized capability is 403; invalid input is 400; busy/conflicting/offline-before-dispatch is 409. Lost acknowledgements include the taskId for safe inspection.

OAuth clients use the standard MCP tools `agent_task_create`, `agent_task_plan`, `agent_task_get`, `agent_task_artifacts`, `agent_task_cancel`, and `agent_task_tools`. Device identity comes from the OAuth grant and cannot be supplied in arguments. These are custom tool names, not an implementation of the MCP native Tasks extension. See [Task Gateway](AGENT-TASK-GATEWAY.md) for schemas, examples and execution semantics.

## Codex-compatible agent capability IDs

The selected device may advertise these consolidated tools through `/mcp` and `agent_task_tools`: `source.apply_patch` (`apply_patch`), `image.view_image` (`view_image`), `unified_exec.exec_command` (`exec_command`), `unified_exec.write_stdin` (`write_stdin`), and `computer_use.computer_use` (`computer_use`). The legacy mutating filesystem, shell, process-job, and public `computer.*` descriptors are not emitted by Agent version 1.0.89. Existing MCP sessions receive the normal catalog-change notification and can also discover the new IDs through `jarvis__tool_search`.

## Tool Catalog lifecycle - 1.0.90

The `Tools` table is reconciled against the union of persisted enrolled-device manifests at server startup, Agent `hello`/`catalog.changed`, device deletion, administrator user deletion, and manual Sync catalog. A canonical tool row is deleted when no enrolled device advertises it. Canonical IDs in `AgentToolCatalogRules` are treated as intentionally retired and are excluded from device capability APIs, selected-device MCP discovery, task-tool discovery and policy editing even if an older Agent reports them. This is a metadata/policy cleanup; it does not modify an Agent installation or grant a replacement tool permission.

The web Tool Catalog can filter by `Auto`, `Hidden`, or `Published`. The filter is client-side over the authenticated `/api/admin/tools` result and combines with text search; selection and bulk policy updates apply only to currently shown rows.