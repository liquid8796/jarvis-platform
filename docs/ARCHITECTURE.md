# Architecture decisions · 1.0.51

## Transport choice

ChatGPT-facing transport is **MCP Streamable HTTP over HTTPS**, implemented by `ModelContextProtocol.AspNetCore`, not hand-written JSON-RPC. Stateless MCP requests are appropriate because workflow/job state belongs to the agent and explicit job IDs; protocol lifecycle compatibility is delegated to the official SDK. Request-scoped `McpGateway` reads identity from the authenticated HTTP context. No browser cookie is accepted as an agent credential.

Agent transport is one outbound **WSS** connection per enrolled device. JSON text envelopes avoid a second application runtime and keep debugging readable. HTTPS ingress upgrades `/agent/connect`. No compression of secret-bearing frames. This is a practical interoperability/latency tradeoff, **not a measured claim of optimal or fastest transport**. A binary transport could reduce large-image overhead but is not implemented. No remote protocol enables an always-running ChatGPT reasoning loop.

## Boundaries and patterns

Server: `Transport` contains thin HTTP/MCP adapters; `Application` contains DeviceService and ToolCatalogService; `Domain` contains entities and ports `IAgentRouter`, `IAuditWriter`; `Infrastructure` implements EF storage and WebSocket routing; `Security` handles OAuth and current access. Dependency injection and explicit interfaces are used instead of a universal service locator. The SDK callback resolves one scoped gateway at the transport composition root. Services do not invoke HTTP controllers or another use case.

Agent Core depends only on shared protocol and policy/tool abstractions. The Windows adapter owns baseline tool lifecycles. WPF uses MVVM with view-only PasswordBox/tray/hotkey code behind. CLI is a separate host. `LegacyToolAdapter` is an Adapter pattern; `ToolInventory` is an explicit registry/factory; policy wraps tools instead of rewriting their logic. The typed tool contract is `Descriptor + ExecuteAsync`; runtime implements lifecycle coordination, not a new business domain.

Baseline reuse is intentionally a ProjectReference to `JarvisCode.App` for public Windows services/resources. That assembly is heavier than a extracted library, but avoids silently diverging from the supplied computer/browser implementations. Future extraction into a dedicated baseline-tools library should preserve tests and schemas. No claim that all old baseline code has been refactored.

## Invocation lifecycle

OAuth grants bind user+device. Gateway rechecks active status/security stamp/ownership and enabled catalog alias. Schema is from the **bound device's actual manifest**, not arbitrary admin-edited executable content. Router authorizes enrollment token again before dispatch and enforces four in-flight calls per device. Deadline uses UTC; machines must have synchronized clocks.

Agent validates the envelope against the current immutable `DynamicToolRegistry` snapshot and its precompiled local schema, checks the local arm state, serializes sensitive/mutating operations, prompts the local user, checks arm state/cancellation again, then invokes that same snapshot tool. Read-only file operations still require the local arm state but may not prompt each time. Metadata audit surrounds server dispatch; no tool arguments/results are written into the audit database.

A lost connection faults in-flight requests with completion unknown. Reconnect does not replay them and preserves the user-selected arm state within the same process. An in-memory bounded completion cache reduces duplicate execution for the same call ID within the current agent process; this is not durable idempotency or exactly-once delivery. If sending a successful result fails, the client must inspect state before retrying.


## Dynamic tool catalog and lifecycle

Installed tools are represented by immutable registry snapshots. Each snapshot includes the exact implementation map, schemas, ordered descriptors, generation and digest, so one invocation cannot observe half of a hot catalog replacement. The handshake advertises the current descriptor snapshot; later calls and task-plan validation deliberately resolve the latest local snapshot. Catalog replacement is not an authorization event: `ToolPermissionPolicy` remains exact-ID and local approval remains in the invocation path.

Optional `threadId` and `turnId` fields correlate calls without becoming authorization inputs. Local interrupt/stop/subagent-stop notifications are fan-out cleanup signals only and cannot arm control or grant permissions.

## Long jobs

`process__start` approves one shell command, returns a job ID, and starts an owned process. `process__read` returns output, an incremental character cursor, truncation flag, completion and exit code. `process__cancel` targets only an owned job. Maximum four active jobs, 30-minute process timeout, 128 KiB retained characters/job, 32k read chunks, at most 50 retained completed jobs. Local arming has no timed expiry. Manual Pause/Disconnect/Exit and connection interruption can still cancel an owned job.

Windows Job Objects are used to group the launched shell and children with kill-on-close. Assignment occurs immediately after launch, not via a suspended launcher; it is not a hardened adversarial process sandbox. Arbitrary commands can affect files/network outside the workspace. The old short shell tool stays available under explicit consent; its detached background mode is rejected.

## Storage and scale

SQLite WAL and EF Identity/OpenIddict tables, initial schema version 1. Existing DB is never dropped/recreated. `EnsureCreated` is intentionally initial-schema-only; later schema changes require reviewed migrations and backups. Optimistic revisions protect device/catalog edits. One process keeps live sockets in memory: do not run multiple replicas behind round-robin. Horizontal scale would require an external routing directory/message bus and a shared production database, neither provided in this version.

Archive is a source release, not evidence that these workflows have all passed runtime tests. See BUILD-STATUS and ACCEPTANCE.

## Adaptive orchestration boundary - 1.0.51

Adaptive orchestration is an Agent Core policy layer, not a new authority. Plans are validated DAGs and repairs can replace only the currently failed logical action; completed actions are not replayed. The Task Gateway coordinator hook is optional and receives no direct tool execution capability: returned replacement steps go back through local schema validation and `AgentConnection` guarded invocation.

Default composition supplies no model planner/coordinator. This keeps deterministic task execution and security behavior stable while providing a concrete extension point for a future model/plugin planner.
