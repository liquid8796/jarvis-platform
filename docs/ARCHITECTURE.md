# Architecture decisions · 1.0.60

## 1.0.64 application-session architecture (current)

These rules supersede older shared OAuth-session, fixed 4-call and mandatory-workspace descriptions below. MCP remains stateless. `McpSessionContext` protects a random application-session ID with ASP.NET Data Protection, the authenticated account and the OAuth-bound execution device. A transport connection, access token, browser or chat client device is not used as a chat ID. Each chat must retain its own handle; deliberately reusing a handle means sharing that logical session.

The local SQLite session registry keys records by owner/device/session, snapshots the workspace at admission, keeps optimistic revisions, persists bounded cursor mailboxes, and retains closed-session tombstones longer than the 30-day protected handle lifetime. Agent context validates ownership again before approval and execution. Existing server data-protection keys must remain in the persistent data directory across upgrades. Account/device mismatches, closed sessions and missing handles fail closed.

`AgentExecutionSettings` is local authority: default 5 calls, 5 process jobs, 5 durable tasks, queue 100 and 60-second queue timeout. The settings store uses revision-checked atomic replacement. AgentHello negotiates settings; live changed/ack messages expose actual acknowledged revision. A reduction stops new starts until active usage drains. Each scheduling key is the full identity. Fair execution and hierarchical path/repository resource coordination are independent of the permission policy. A bounded control lane remains usable while ordinary slots are full. Composite orchestrators release their outer execution slot before guarded children. Process jobs retain resource leases through real exit/output cleanup.

The native browser bridge uses an AsyncLocal application scope and per-session selected browser. The extension receives session identity in a trusted envelope, stores separate group/tab ownership, rejects foreign tabs even during origin preflight, and persists ownership through service-worker restarts. Up to 512 active sessions and bounded closed tombstones prevent unbounded growth; closing does not consume active capacity. Browser groups are not cookie/profile isolation. Computer service instances and observation IDs are per-session, with shared invalidation after desktop-affecting operations.

Metadata projection and mailbox coordination are not transcript access. Existing explicitly invoked project journals remain project-scoped shared artifacts, not automatic chat history; selecting the same project grants no extra tool permissions. No push-to-idle-chat capability or unattended model service is added. Different agent machines remain separate OAuth bindings and filesystem namespaces.

The filesystem adapter records bounded SHA-256 observations after stable reads. Existing-file Write/Edit/NotebookEdit require that session's current observation; changes fail with FILE_CHANGED. This protects cooperating Jarvis calls under resource locks, not arbitrary external applications atomically modifying a file between validation and write. Canonical paths also preserve the private-agent-directory exclusion through directory links.

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

### Runtime closure and catalog synchronization - 1.0.55

The Windows composition root now owns one production registry and injects both the conservative adaptive coordinator and the validated local plugin bootstrap into `AgentConnection`; these are no longer test-only seams. A registry replacement sends the complete immutable catalog snapshot as `catalog.changed`. The server validates schemas/descriptors, persists the bound device manifest, updates peer generation/digest and replies `catalog.ack`; subsequent calls carry that acknowledged identity and stale calls fail locally before tool lookup/schema execution.

Standing consent remains separate from discovery. `ToolPermissionPolicy` adds expiring capability leases scoped to a session or turn with optional workspace roots, command prefixes and network allowance. Legacy exact-ID Full Permission remains for ordinary tools, but `process.start`/`process.spawn` require a matching invocation-time lease rather than treating an arbitrary shell/process surface as one unconstrained saved capability. Lease expiry/revocation reuses the existing in-flight cancellation path.

### Capability protocol and diagnostics - 1.0.56

`WireMessage.Version` remains 1; protocol v2 is additive metadata in hello/welcome, so older peers continue deserializing the same envelope. The agent advertises a bounded capability-name set, the server intersects it with supported names, and a missing/zero protocol version is normalized to legacy v1. Feature handlers still validate their own fields and never treat capability advertisement as authorization.

`AgentDoctor` is a read-only Core diagnostic projection, and the CLI exposes it as `doctor --json`. The snapshot deliberately contains only counts, booleans, generic status/type names, versions and catalog identity. Plugin/task checks are bounded and no credential, raw local path, permission tool ID, lease/session ID, command argument/result or manifest body is serialized. Doctor health is operational evidence, not an Arm/permission grant or a substitute for tests.

## Managed plugin lifecycle - 1.0.59

`PluginCatalog` validates declarative manifests only. A pinned entry file proves package identity/provenance but is never dynamically loaded; tool and lifecycle-hook implementations must already be supplied by the local host. Compatibility can be bounded by minimum/maximum Agent versions; skill roots must resolve inside the plugin directory and MCP dependencies/provenance remain bounded metadata.

`PluginRuntimeBootstrap` owns one last-known-good snapshot, a debounced `FileSystemWatcher`, explicit host hook bindings and local atomic package management. A valid reload publishes through `Changed` into `AgentConnection.ApplyPluginCatalog`; a failed validation preserves the previous snapshot and records redacted error type. Lifecycle dispatch maps interrupt/stop/subagentStop to declared host bindings and catches failures per plugin so one extension cannot block later cleanup subscribers.

## Durable thread journal - 1.0.58

The Agent profile owns a separate SQLite `thread-runtime.db` used by `ThreadRuntimeToolSet`. The normalized thread row carries current project/title/goal/section, fork lineage and checkpoint watermarks; `thread_events` is the append-only audit journal for thread creation, turns/items/artifact references, queue changes, metadata, forks and checkpoints. Queue rows are materialized to support deterministic reorder/start without replaying journal history.

Compaction and rollback are projections, not destructive rewrites. A compact checkpoint stores a summary and `compact_through_event_id`; ordinary timeline reads start after that watermark while audit reads may include all prior events. Rollback records `rollback_target_event_id` and a journal event but does not delete later facts. All thread tools verify that the persisted project stays inside the current selected workspace set.

## Long jobs

`process__start` remains the compatibility shell wrapper; `process__spawn` launches an exact bounded argv with optional environment overrides and Windows ConPTY, and both return opaque owned job IDs. `process__write_stdin` sends bounded text only to an owned running job; `process__resize_pty` resizes only an owned ConPTY. `process__read` keeps combined output compatibility and adds structured stdout/stderr/pty/system events plus incremental cursor, truncation, completion and exit code. `process__cancel` targets only an owned job. Maximum four active jobs, 30-minute process timeout, 128 KiB retained characters/job, 32k read chunks, at most 50 retained completed jobs. Local arming has no timed expiry. Manual Pause/Disconnect/Exit and connection interruption can still cancel an owned job.

Windows Job Objects are used to group the launched shell and children with kill-on-close. Piped jobs are assigned immediately after `Process.Start`; ConPTY jobs use `CREATE_SUSPENDED`, attach to the Job Object, then resume the main thread so PTY descendants cannot race ownership. This is still not a hardened adversarial process sandbox. Arbitrary commands can affect files/network outside the workspace. The old short shell tool stays available under explicit consent; its detached background mode is rejected.

## Storage and scale

SQLite WAL and EF Identity/OpenIddict tables, initial schema version 1. Existing DB is never dropped/recreated. `EnsureCreated` is intentionally initial-schema-only; later schema changes require reviewed migrations and backups. Optimistic revisions protect device/catalog edits. One process keeps live sockets in memory: do not run multiple replicas behind round-robin. Horizontal scale would require an external routing directory/message bus and a shared production database, neither provided in this version.

Archive is a source release, not evidence that these workflows have all passed runtime tests. See BUILD-STATUS and ACCEPTANCE.

## Adaptive orchestration boundary - 1.0.51

Adaptive orchestration is an Agent Core policy layer, not a new authority. Plans are validated DAGs and repairs can replace only the currently failed logical action; completed actions are not replayed. The Task Gateway coordinator hook is optional and receives no direct tool execution capability: returned replacement steps go back through local schema validation and `AgentConnection` guarded invocation.

Default composition supplies no model planner. It does supply the conservative production repair coordinator introduced in 1.0.55, which may retry only the same installed read-only/non-sensitive logical step without changing arguments or authority. The generic adaptive DAG loop can run independent ready actions concurrently only when a live-registry policy marks them read-only/non-sensitive; concurrency is bounded to 1..8, while mutating/sensitive actions and repairs remain serialized.

## Composite tool and plugin boundary - 1.0.52 / 1.0.60

Composite tools are orchestration surfaces, not alternate execution authorities. AgentConnection still owns the guarded nested invoker. The composite marker changes semaphore lifetime only: approval occurs before slots are released, and nested calls independently reacquire policy/limits. `AgentCoreHostTools` is the descriptor/implementation factory for host-owned composites and developer tools, so live runtime registration, CLI discovery and Desktop permission UI cannot silently diverge.

`tool_script.run` adds a fresh Jint engine per call with explicit script/statement/memory/time/tool-call/argument/output budgets. Jint CLR interop is never enabled and no Node/process/filesystem/network host objects are injected. The only host delegate is async `invokeTool(toolId,argsJson)`, and any failure/denial recorded by that delegate fails the complete script even if JavaScript catches the Promise rejection. Nested invocation of either code-mode composite is rejected.

Plugin manifests are discovery/integrity metadata. Hash pins protect the referenced local entry file, but a manifest does not cause Jarvis to load that file. A host must provide matching `IAgentTool` instances explicitly; only those instances can be projected into the dynamic tool registry.

## Delegation and memory boundary - 1.0.53

Delegation does not introduce a second execution authority. Forked work is materialized as ordinary persisted RemoteTask records with explicit lineage and therefore traverses the same tool registry, local policy gates and no-replay logic as parent work. Scope inheritance is enforced locally, not trusted from the server or caller.

Durable autonomous memory is SQLite-backed and explicitly partitioned; it is not an implicit global model memory. Owner/project/namespace are part of the primary key, TTL is enforced during reads, and provenance remains attached to each current value.

## Developer and evaluation boundary - 1.0.54

Developer convenience is layered on top of existing governance rather than bypassing it. Published developer tools remain normal `IAgentTool` instances in `DynamicToolRegistry`; test execution is not treated as read-only. DAP support is deliberately a local owned-process contract without remote attach/injection semantics.

Evaluation is credential-free and deterministic. Harness V2 binds Core contract tests to shipping Windows/server composition tests so production bootstrap, Process V2, durable threads, plugin lifecycle, protocol/doctor, stale desktop state, adaptive no-replay/read-only parallelism, both code modes, delegation/memory and task transport are all named scenario groups. The PowerShell report records targeted-test throughput and p95 scenario duration; `HarnessEvaluator` can additionally consume bounded tool-latency samples and emit p50/p95/max metrics.
