# API overview

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

## Agent wire protocol v1

Text JSON frames, one logical envelope max 8 MiB, no compression. Fields use camelCase. Initial `hello` contains `{deviceId,version,platform,machineName,tools}`; server returns `welcome`. Manifest is capped at 256 tools, schemas 64 KiB each. `ping` and `pong` contain monotonic timestamps. `call` includes ID, toolId, sessionId, deadlineUtc and arguments. `result` includes ID and `{text,isError,images?,widget?}`. `cancel` targets an in-flight ID. TLS and enrollment authorization are mandatory outside explicitly configured loopback development.

Server heartbeat 15s; stale peer threshold 50s. Deadlines max five minutes on agent, server default120s/max240s. Four server calls/device; agent bounds parallelism and queues. TCP/WebSocket disconnect cannot indicate whether a mutating tool completed: clients must inspect state, not blindly replay.
