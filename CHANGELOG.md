## 1.0.88 - 2026-09-25

- Make the OAuth-bound selected device's live Agent capability manifest the MCP runtime source of truth. Missing catalog-policy rows now mean Auto visibility; Published retains explicit public metadata and Hidden is the explicit deny state.
- Add permanent `jarvis__tool_search` and `jarvis__tool_call` fallback tools so a ChatGPT/MCP session can discover and invoke capabilities added after its original direct tool list was built. Generic invocation still passes current schema, Agent catalog generation/digest, Arm/Pause, local permission/approval, session ownership and execution-resource checks.
- Advertise MCP `tools.listChanged` for initialize-based stateful sessions and emit `notifications/tools/list_changed` after Agent `hello`/`catalog.changed` or administrator publication-policy changes. Newer stateless protocol paths remain usable through the permanent fallback pair.
- Replace the old import-disabled catalog behavior with Auto/Published/Hidden policy, keep `Import installed` as an optional metadata mirror, update task-plan visibility to the same selected-device rules, and add an additive database schema-v2 migration that retains the legacy `Enabled` column as a compatibility mirror.
- Add OAuth/notification/stale-session/Hidden/removal regression coverage and update operator/UI documentation. Bump package to **1.0.88**, assembly/file to **1.0.88.0**.

## 1.0.87 - 2026-09-24

- Add four explicit-session ImageGen tools backed exclusively by ChatGPT Web in the user's selected Chrome/Edge extension instance. No standalone browser, cookie import, OpenAI API key or paid API fallback.
- Add local Desktop/extension selection, exact-instance routing, account/conversation checks, strict reference uploads, durable idempotent jobs, cancellation and reconciliation without resubmitting prompts.
- Save browser-attributed original downloads as immutable session artifacts with bounded transparent PNG previews and edit lineage. Refuse ambiguous results, missing inputs, unsafe paths and cross-session references.
- Add ImageGen settings and extension popup; extension version 1.4.0 adds Downloads permission. Reload the extension and explicitly select the signed-in browser; existing permissions are not automatically enabled.
- Bump package to 1.0.87 and assembly/file to 1.0.87.0. Verification and deployment evidence are recorded in docs/BUILD-STATUS.md; do not infer live ChatGPT end-to-end acceptance from unit tests.

## 1.0.86 - 2026-09-24

- Add the Blender MCP Python/stdio integration observed in the local authoring pipeline: six sensitive `blender.*` tools with real downstream discovery, calls, resources, prompts, images and structured results.
- Share validated MCP config/transport lifecycle with Unity while preserving its IDs and behavior. Blender enforces loopback, Safe Mode and telemetry opt-out, serializes calls across this Agent's sessions, and never falls back to raw addon code execution.
- Add explicit configuration/startup helpers, regression/smoke coverage and operator documentation. Preserve other MCP entries, fail on malformed JSON, and retain existing consent, enrollment and Arm/Pause controls. Cancellation never claims to undo or forcibly interrupt Blender Python.
- Prefix Blender public catalog names (`blender_list_tools`, `blender_call_tool`, etc.) to avoid silently colliding with Unity under the existing gateway's global name uniqueness rule.
- Fix MCP stdio deadlocks by continuously draining diagnostic stderr with a fixed-size buffer, including output without newlines. Keep diagnostics out of tool replies; verify the failure/fix with a native process regression and live Blender calls.
- Bump package to **1.0.86**, assembly/file to **1.0.86.0**. No new gateway wire messages or permissions are introduced. Release verification passed **506 .NET + 8 Python tests** and real Blender smoke; the versioned server release was deployed and independently verified on OCI. See `docs/BUILD-STATUS.md` for evidence and the separate running-Agent upgrade requirement.

## 1.0.85 - 2026-09-24

- Integrate the MCP for Unity Jarvis Agent client through the operator-owned `JarvisAgent/mcp.json` Unity entry. Add six sensitive `unity.*` tools for discovery, calls, resources, and prompts with stdio/Streamable HTTP support.
- Keep connections lazy and isolated by Agent session. Configuration changes reconnect; missing/disabled/malformed configuration revokes that session's bridge. Pause/stop disposes owned connections; failed modifying requests are never automatically replayed. Existing approvals, Arm/Pause, enrollment, and tool permissions are unchanged.
- Launch native Windows `.exe`/`.com` MCP servers directly rather than through `cmd.exe`, preserving `>=` version requirements and other literal shell metacharacters. Retain the existing batch/extensionless launcher fallback.
- Add parser, lifecycle, schema, forwarding, and real Windows stdio process regressions. Document Unity Editor setup and catalog import. Bump package to **1.0.85**, assembly/file to **1.0.85.0**.

## 1.0.84 - 2026-09-24

- Keep newly opened chat sessions on the Agent's configured default workspace unless the current user request explicitly asks for a workspace override. Workspace tool descriptions and schema guidance now reject inferred switches sourced from prior chats, remembered project paths, session labels/list results or unrelated project context.
- Add regression coverage for the session/workspace tool contract and bump package/assembly/file versions to **1.0.84 / 1.0.84.0**.

## 1.0.82 - 2026-09-22

- Fix WPF mouse wheel routing when the pointer is over nested input containers by forwarding wheel events to visible scroll viewers.

## 1.0.83 - 2026-09-22

- Fix WPF mouse wheel routing when the pointer is over nested input containers by handling already-routed wheel events at the window level and resolving the actual scroll viewer under the cursor.

# Changelog

## 1.0.82 - 2026-09-22

- Fix WPF mouse wheel routing when the pointer is over nested container controls. The main window now forwards wheel input to the nearest scrollable parent instead of allowing child controls to trap scrolling.

## 1.0.81 - 2026-09-20

- Complete plain-text prompt delivery: append saved enabled bodies in order with blank-line separators, without a source banner, nested JSON, title prefix, synthetic fences or zero-width rewriting. Preserve prompt body whitespace/Unicode through preview, save, reload and delivery.
- Enforce the 16,000-character rendered limit including separators, retain existing input/storage bounds, and report null snippets as validation errors. Keep malformed optional metadata from replacing a completed tool result.
- Extend regression coverage for exact text, boundary sizes, wire roundtrips, UI persistence and normal/session/task MCP replies. Preserve first output blocks, images, structured schemas, local approvals, Arm/Pause and enrolled-agent isolation; do not claim text formatting grants system priority or proves model obedience.
- Bump package to **1.0.81**, assembly/file to **1.0.81.0**. Internal wire/storage JSON, capability `user-prompt-context-v1`, default preset contents and browser extension 1.3.0 remain unchanged.

## 1.0.80 - 2026-09-20

- Add the Agent **Prompt injection** tab with editable titles/text, add/delete, individual and global enable switches, draft preview, explicit Save/Reload and confirmed restore-to-defaults. Seed seven disabled process/memory/overlay workflow examples; prompts do not implement these capabilities or promise anti-cheat bypass/account safety.
- Persist presets separately from credentials and permissions in `prompt-injection.json`, with atomic replacement, revision-conflict detection, bounded input and preservation of corrupt files. Deleting all presets does not silently restore defaults.
- Negotiate `user-prompt-context-v1`. Attach saved, enabled context to subsequent successful Agent tool/task replies; the MCP server adds a separately labelled text block while preserving the original tool text, images and structured-output schemas. Legacy peers and error/denial replies receive no prompt context. Tool permissions, Arm/Pause and operating-system controls are unchanged.
- Add persistence, view-model, protocol, approval-isolation, device-isolation and WPF binding/render regression coverage. Bump package/assembly/file versions to **1.0.80 / 1.0.80.0**, based directly on 1.0.77 without restoring the discarded 1.0.78/1.0.79 changes. Browser extension remains 1.3.0.

## 1.0.77 - 2026-09-19

- Add checkbox-based multi-selection to the Agent Sessions list, including Select all/Clear controls for the currently visible open sessions.
- Add bulk Stop selected and Close selected actions. Bulk close uses one confirmation dialog for the checked sessions, preserves the existing terminal-handle semantics, and never pauses unselected sessions.
- Add regression coverage for bulk targeting, closed-session exclusion and filter-driven selection cleanup. Bump package/assembly/file versions to 1.0.77 / 1.0.77.0; browser extension protocol/version remains 1.3.0.

## 1.0.76 - 2026-09-19

- Extend cancellation-bound execution-resource cleanup to `computer` calls as well as browser calls. A cancelled `computer.request_access` or other desktop-scoped computer operation now releases its call-owned `desktop` lease immediately while local permission/grant UI or driver teardown finishes unwinding, preventing later browser QA from hanging behind stale desktop ownership.
- Remove the absolute 30-day expiry from newly issued application-session handles. Session handles remain protected owner/device-bound correlation tokens, every call still requires live OAuth/device/local authorization, and explicit Agent session close remains terminal. Legacy v1 handles remain accepted after their historical expiry timestamp when the underlying Agent session is still open.
- Add focused regressions for the cancelled computer-permission desktop lease and legacy expired-handle compatibility. Bump package/assembly/file versions to 1.0.76 / 1.0.76.0; browser extension protocol/version remains 1.3.0.

## 1.0.75 - 2026-09-19

- Make normal `dotnet build` output for both Desktop and CLI include the dedicated browser runtime: the entry points build BrowserService and BrowserHost for ordering, copy `jarvis-browser-service.*` into the Agent root, and copy `jarvis-browser-host.*` under `browser/`.
- Remove transitive root-level native-host copies from Agent build/publish output and make `Verify-AgentOutput.ps1` reject that misplaced layout while continuing to require the browser service and `browser/jarvis-browser-host.exe`.
- Refresh the packaging regression fixture and add build-closure checks for both Agent entry points. Bump package/assembly/file versions to 1.0.75 / 1.0.75.0; browser extension protocol/version remains 1.3.0.

## 1.0.74 - 2026-09-19

- Bind browser execution-resource leases to the lifetime of the call that acquired them. When an old session/call is stopped or cancelled, its browser/desktop lease is released promptly even if the underlying browser task is still unwinding, so another session is not stuck behind stale Chrome-channel ownership.
- Scope the fail-safe to browser calls only; filesystem, shell and process leases keep their existing completion/retention semantics. Cancellation-triggered browser release is deferred out of the cancellation callback so a bulk stop/revocation can cancel sibling calls before resource waiters are pumped.
- Add a regression that holds an old session's `browser|...` plus `desktop` claim without disposing it, cancels that call, and requires a new session to acquire the desktop/browser path immediately. Bump package/assembly/file versions to 1.0.74 / 1.0.74.0; browser extension protocol/version remains 1.3.0.

## 1.0.73 - 2026-09-18

- Fix the dedicated Chrome/Edge native-messaging host lifecycle so a browser-service pipe disconnect wins over an idle blocking Chrome stdin read; the host now exits promptly and lets the extension reconnect to the replacement browser service.
- Add a regression that holds Chrome input idle, drops the browser-service pipe and requires the relay to terminate without waiting for another browser frame.
- Bump package/assembly/file versions to 1.0.73 / 1.0.73.0. Browser extension protocol/version remains 1.3.0.

## 1.0.72 - 2026-09-18

- Show the raw `js_...` session ID directly under each session name in the Agent Sessions list so operators do not need to expand details to identify a chat.
- Render session names and IDs as read-only selectable text with an I-beam cursor, mouse text selection and normal copy behavior while keeping them out of the keyboard tab order.
- Add static XAML and rendered WPF smoke coverage for visible/selectable session identity text; update operator documentation and bump package/assembly/file versions to 1.0.72 / 1.0.72.0.

## 1.0.71 - 2026-09-18

- Move browser execution out of the Agent process: keep the existing flat `browser.*` MCP surface as a proxy, add dedicated `jarvis-browser-service.exe` runtime ownership and a minimal `jarvis-browser-host.exe` Chrome/Edge native-messaging relay, and package/verify both companions for Desktop and CLI.
- Add browser-family routing (`auto`, `dev`, `chrome`, `edge`, `extension`). Loopback targets use an isolated Jarvis dev-browser profile; external calls select a ready non-dev browser so frontend verification cannot leak into the user's normal browser selection.
- Add versioned browser capability negotiation: extension 1.3.0 advertises browser/native-host protocol versions, family, capabilities and persistent instance identity; incompatible extension connections remain non-ready. The Agentâ†”browser-service channel has a separate protocol/capability handshake and retains existing browser tool IDs for client compatibility.
- Enforce rendered frontend verification in production even when no `IRemoteTaskAgenticCoordinator` is configured. Autonomous frontend tasks deterministically collect target, DOM, overlay, console, screenshot and post-interaction evidence; visual tasks also collect desktop/mobile/overflow and structural fidelity evidence. Missing target/browser/evidence fails closed, while explicit Figma/pixel-perfect/reference-image work still requires semantic comparison evidence.
- Add routing/session/frontend regressions, require browser companion executables in publish validation, and bump package/assembly/file versions to 1.0.71 / 1.0.71.0.

## 1.0.70 - 2026-09-17

- Add a bounded immutable `VisualFidelityLedger` covering layout, typography, color, iconography, overflow and interaction-state mismatches; visual/layout frontend completion now requires `VisualFidelity` evidence and unresolved blocking mismatches fail the normal verification gate.
- Make visual fidelity a first-class agentic goal-verifier output so the host converts returned ledgers into typed frontend evidence and feeds unresolved fidelity debt through the existing repair loop.
- Add six stable Codex-parity coding scenarios plus `EngineeringEvidence`, `CodingHarnessEvaluator` and additive `HarnessEvaluator.EvaluateEngineering(...)` metrics for build/test/browser/console/interaction/visual pass rates, corrective loops and evidence counts while preserving historical throughput metrics.
- Add Core regressions for ledger resolution/bounds, visual completion blocking and coding benchmark aggregation; bump package/assembly/file versions to 1.0.70 / 1.0.70.0.

## 1.0.69 - 2026-09-17

- Add `PluginSkillLoader` progressive disclosure for catalog-validated `SKILL.md` roots: bounded metadata discovery, reparse/out-of-root protection, size/UTF-8 validation and per-skill diagnostics; full instructions are decoded only after explicit planner/verifier selection.
- Project compact skill metadata into agentic planning and goal-verification prompts, expose explicit `LoadSelectedSkills(...)` on both contexts, and wire discovery/loading through `PluginRuntimeBootstrap`, `AgentConnection` and Windows startup without executing plugin entry code.
- Add per-session `BrowserObservationTracker` freshness semantics: refs from the latest `browser.read_page/find` generation are invalidated after material browser mutations and stale `ref`/`ref_id` usage is rejected before raw Chrome execution.
- Add Core/Windows regressions for lazy skill loading, outside-root/oversize rejection, skill-context projection and browser ref invalidation; bump package/assembly/file versions to 1.0.69 / 1.0.69.0.

## 1.0.68 - 2026-09-17

- Add deterministic frontend/visual change classification and a fail-closed `FrontendVerificationGate` for autonomous coding tasks. Base rendered proof now requires target identity, rendered DOM/accessibility state, framework-overlay health, console health, screenshot evidence and an interaction/post-state proof; visual/layout work additionally requires desktop, mobile and overflow evidence.
- Feed missing/failing rendered evidence back into the agentic coding prompt as verification debt across bounded goal-repair rounds, and combine that gate with the configured goal verifier before allowing `COMPLETED`.
- Add typed `RemoteTaskSnapshot.verification` protocol summaries and matching strict MCP output schemas for retained evidence, missing kinds and failed kinds while keeping the field optional for legacy/deterministic task JSON.
- Add Core and Server regressions covering classification, latest-evidence semantics, fail-closed frontend completion, full visual completion and schema compatibility; bump package/assembly/file versions to 1.0.68 / 1.0.68.0.

## 1.0.67 - 2026-09-17

- Add vendor-neutral `IRemoteTaskAgenticCoordinator` support for goal-only `AUTONOMOUS` tasks: generated plans are persisted and revalidated, normal step execution/repair remains permission-gated, and final completion now requires an independent goal-verification pass when the agentic coordinator is active.
- Allow failed goal verification to request at most two bounded repair rounds; append and validate those repair steps as part of the durable plan, execute them through the existing runner, and retain bounded goal-verifier artifacts.
- Add `CodingPromptAssembler` with deterministic bounded layers for coding policy, rendered frontend/browser QA policy, resolved workspace, sorted tool capabilities, selected skills, outcomes and verification debt; inject these layers into agentic planning/goal contexts without choosing a model provider or granting permissions.
- Add focused agentic/prompt regressions plus the Codex-parity design/implementation plan documents; bump package/assembly/file versions to 1.0.67 / 1.0.67.0.

## 1.0.66 - 2026-09-17

- Publish human-readable MCP `title` plus matching `annotations.title` for every dynamic Jarvis action and all six `agent_task_*` tools, following the metadata pattern used by mature MCP servers such as Desktop Commander; dynamic titles split namespace/underscore and Pascal/camel-case identifier boundaries.
- Preserve existing read-only/destructive/open-world hints, schemas, permissions, routing and execution behavior; ChatGPT remains responsible for the final compact/collapse presentation.
- Add JSON-RPC `tools/list` regression coverage for task, session and dynamic process actions.
- Bump package/assembly/file versions to 1.0.66 / 1.0.66.0. Production deployment and ChatGPT tool-definition refresh remain separate steps.

## 1.0.65 - 2026-09-17

- Make `_jarvis.sessionHandle` optional for ordinary MCP tools and `agent_task_*`: missing handles now dispatch through a per-call ephemeral execution ID instead of failing `SESSION_REQUIRED` before the local agent. Explicit session/workspace tools still require a validated owner/device-bound handle.
- Keep stateful sessionless resources usable across later prompts with a stable authenticated owner/device isolation scope for process jobs, filesystem read-before-write observations, computer state, per-session adapters, browser suites, scheduler fairness and resource claims; explicit `js_...` sessions retain strict per-session ownership.
- Add resumable `session__stop_work`; remove destructive `session__close` from normal tool discovery while retaining raw/operator/UI close behavior. A stopped session remains readable/resumable; only explicit close is terminal.
- Add pre-dispatch gateway rejection audit records such as `rejected:SESSION_REQUIRED` without logging tool arguments or session handles, plus reason-coded agent reachability states for connect/reconnect/heartbeat/transport diagnostics.
- Add regressions for prompt-to-prompt ordinary calls without handles, sessionless task/process/file/computer/browser continuity, explicit-session isolation, resumable stop-work and rejection auditing.
- Bump package/assembly/file versions to 1.0.65 / 1.0.65.0. Browser extension remains 1.2.0; production deployment remains a separate step.

## 1.0.64 - 2026-09-17

- Add protected application-session handles bound to authenticated owner and agent, schema envelope validation/stripping, persistent metadata/workspace revisions and bounded coordination mailboxes. Missing/foreign/closed sessions fail explicitly; handles are not permissions and metadata never exposes another session's handle.
- Allow an empty agent default workspace. Add session/workspace tools and preserve the accepted request's workspace snapshot when a chat changes its folder. Default edits affect new sessions only.
- Add live revisioned execution settings with default 5 calls/process jobs/durable tasks, fair per-session queues, queue expiry, independent cancellation/status capacity, and server acknowledgement. Reducing limits drains existing work without canceling it; composites do not deadlock at limit 1.
- Enforce owned process/task operations and scoped stop/close, resource coordination for files/repositories/browser/desktop, per-session browser selection/tab groups and per-session desktop service state. Unknown shell effects retain conservative exclusion. Existing-file writes require this session's successful read and a matching content fingerprint; stale writes fail before mutation.
- Add WPF Execution limits and Sessions pages using UI UX Pro Max WPF guidance: inline validation, persisted drafts, acknowledgement visibility, metadata-only session rows, filter/empty/error states, scoped stop/confirmed close, keyboard actions, dynamic high-contrast tokens, and scroll-safe narrow layouts.
- Pin official Microsoft.Windows.Console.ConPTY 1.24.260710001 and ship its native host alongside the matching assets. Fix redirected-parent stdio inheritance, answer the one-time startup handshake, drain final output and retain resources through process cleanup.
- Validate architecture-specific native hosts in Windows publish outputs before packaging; expose an isolated `--pty-only` smoke that loads the actual published ConPTY library and checks final output/exit status without connecting to the live agent.
- Bump package/assembly/file versions to 1.0.64 / 1.0.64.0; vendored bridge assemblies to 1.0.25 / 1.0.25.0; browser extension to 1.2.0. Live deployment is a separate acceptance step.

## 1.0.63 - 2026-09-17

- Add a persistent `Always approve` choice to the local approval dialog only for the constrained `process.start` and `process.spawn` tools; persistence is exact-tool scoped and is written atomically before the active request is approved.
- Keep permanent constrained-process grants separate from ordinary Full permission and scoped capability leases. AgentRuntime/CLI reload the new permission-file v2 format while v1 files remain supported.
- Show active permanent process grants as `Always approved` in Tool permissions with a `Require approval again` revocation action; revocation immediately restores scoped-lease/interactive approval behavior and signals owned activity to stop.
- Clarify Desktop permission copy so Full permission no longer appears to promise bypass of the constrained-process exception; add focused policy, invocation and runtime-reload regressions.
- Bump package/assembly/file versions to 1.0.63 / 1.0.63.0.

## 1.0.62 - 2026-09-16

- Publish object-root `outputSchema` definitions for every dynamic MCP tool and all six `agent_task_*` operations; return matching `structuredContent` on success and tool-level errors.
- Keep ordinary tool text opaque in `{text, isError}`; expose typed task snapshots, artifact pages and errors, and wrap task tool descriptors in `{tools: [...]}` only in structured content. Preserve legacy text and omit unavailable nullable fields.
- Preserve image content without duplicating base64 or local widget HTML in structured results. Correct the existing image adapter to use SDK `ImageContentBlock.FromBytes`, preventing decoded image bytes from being serialized as base64 text.
- Add OAuth/WebSocket contract regressions for discovery, task lifecycle and owner/device isolation, local validation/permission errors, descriptor wrapping, artifact pagination, omitted fields, malformed output types and binary image round-tripping. No permission policy or agent wire-contract changes.
- Bump package/assembly/file versions to 1.0.62 / 1.0.62.0. Live server deployment and ChatGPT tool-definition refresh remain separate from source publication.

## 1.0.61 - 2026-09-16

- Make the Jarvis Agent settings sidebar the single visible navigation surface for `Connection center` and `Tool permissions`, with persistent selected/hover/focus treatment and two-way `SelectedTab` synchronization.
- Keep the existing `TabControl` only as an internal content host: its header strip is removed and the host itself is no longer keyboard-focusable, avoiding a second invisible navigation stop.
- Remove the redundant tab-selection commands, bind the desktop footer version to the running assembly, extend the UI smoke harness with sidebar/content/version regressions, make its layout verification avoid same-thread `ApplicationIdle` deadlocks, and make desktop shutdown tolerate a detached WPF `Application` in the reflection-based smoke host.
- Bump package/assembly/file versions to 1.0.61 / 1.0.61.0.

## 1.0.60 - 2026-09-16

- Add `tool_script.run`, a fresh-engine Jint JavaScript sandbox with statement/memory/time/script/call/argument/output bounds and exactly one host capability: guarded `invokeTool(toolId, argsJson)`; CLR/Node/filesystem/network/process globals are not enabled.
- Fail the complete script when any nested tool call is rejected or fails, even if JavaScript attempts to catch the Promise rejection, and reject recursion into either composite code mode.
- Add bounded adaptive DAG concurrency for independent ready actions only when the live tool registry marks them read-only and non-sensitive; mutating/sensitive actions and repairs remain serialized.
- Centralize Core host-tool descriptors so live runtime, CLI `list-tools` and Desktop permissions expose the same `tool_program`, `tool_script` and developer tools.
- Upgrade Harness V2 with production-bootstrap/process/thread/plugin/protocol/doctor/safe-script/parallel-DAG groups, schema-v2 throughput/p95 scenario metrics and bounded p50/p95/max real tool-latency metrics; bump package/assembly/file versions to 1.0.60 / 1.0.60.0.

## 1.0.59 - 2026-09-16

- Activate a managed plugin runtime that hot-reloads validated local manifests into the live dynamic tool catalog while retaining the last-known-good snapshot when validation fails.
- Bind declared interrupt/stop/subagentStop hooks only to explicit host-supplied implementations and isolate failures per plugin so cleanup fan-out continues.
- Extend manifest validation with optional maximum Agent compatibility, contained skill roots, bounded MCP dependency metadata and provenance while retaining SHA-256 entry pinning and no arbitrary code loading/downloading.
- Add atomic local install/update with rollback plus manifest-only uninstall; entry payload cleanup remains explicit so shared/local artifacts are not deleted implicitly.
- Extend redacted doctor metadata with plugin skill-root/MCP-dependency counts; add runtime/hot-reload/package/hook production-bootstrap regressions and bump package/assembly/file versions to 1.0.59 / 1.0.59.0.

## 1.0.58 - 2026-09-16

- Add a profile-local SQLite thread runtime with append-only project/thread event journals, persisted turns/items/artifact references, goals/sections and parent/fork-event lineage.
- Add durable per-thread queue add/update/delete/reorder/start operations and bounded project search; queue rows are materialized for ordering while every mutation is also journaled.
- Add non-destructive compact/rollback checkpoints: default timeline projection may hide compacted events, while audit reads can still include the complete immutable event history.
- Publish seven production `thread.*` tools through AgentRuntime, CLI discovery and Desktop permission catalog with selected-project scope guards and bounded schemas.
- Add persistence/reopen, compaction, queue/fork/search and production-bootstrap regression coverage; bump package/assembly/file versions to 1.0.58 / 1.0.58.0.

## 1.0.57 - 2026-09-16

- Add `process.spawn` for exact argv execution without an inserted shell, bounded environment overrides and optional Windows ConPTY sessions while retaining `process.start` as the compatibility shell wrapper.
- Add `process.write_stdin` and `process.resize_pty`; `process.read` remains cursor-compatible and now includes bounded structured stdout/stderr/pty/system events alongside combined output.
- Create Windows ConPTY children suspended, attach them to the existing kill-on-close Job Object before resume, and retain Pause/disconnect/permission-revocation cleanup so interactive jobs cannot escape Agent ownership.
- Extend Task Gateway managed-process handling and scoped capability leases to `process.spawn`, including token-wise argv command-prefix matching.
- Add regression coverage for argv/environment execution, stdin, ConPTY resize/cancel and spawn capability leases; bump package/assembly/file versions to 1.0.57 / 1.0.57.0.

## 1.0.56 - 2026-09-16

- Add additive agent capability protocol v2 negotiation in hello/welcome while retaining `WireMessage.version = 1` for legacy frame compatibility; current negotiated features are Task Gateway v1, catalog synchronization and capability leases.
- Add redacted `AgentDoctor` operational diagnostics for package/assembly drift, protocol/capabilities, catalog identity/tool count, plugin metadata health, permission-store/grant/lease counts, bounded task-snapshot health/count and local process/computer/browser readiness.
- Add `jarvis-agent doctor --json`; the command emits no credentials, raw local paths, permission/lease/session identifiers, plugin manifest content or tool arguments/results and returns a non-zero diagnostic status when a checked subsystem is unhealthy.
- Preserve existing legacy peers that omit protocol metadata and cover v2 negotiation plus doctor redaction/invalid-plugin behavior with regression tests.

## 1.0.55 - 2026-09-16

- Wire the existing adaptive Task Gateway extension point and pinned local plugin catalog into the production Windows `AgentRuntime` composition root; the default coordinator may repair only installed read-only/non-sensitive steps and cannot broaden tool arguments or permission.
- Add live catalog synchronization: registry changes send generation/digest/descriptors, the server validates/persists and acknowledges them, later calls are pinned to the acknowledged catalog identity, and stale calls fail locally before tool/schema execution.
- Add expiring session/turn capability leases with workspace and command-prefix constraints. `process.start`/future `process.spawn` require a matching invocation-time lease for Full Permission rather than treating one saved arbitrary-process tool grant as unconstrained authority.
- Reuse the existing permission-revocation cancellation path for lease expiry/revocation, preserving local Arm/Pause and no-replay behavior.
- Add production-composition, catalog-race/stale-state and capability-lease regression coverage; document the runtime-closure architecture and security boundary.

## 1.0.54 - 2026-09-16

- Add `developer.symbol_search`, a bounded workspace-scoped source/text search that skips VCS/build/dependency directories and returns structured file/line matches.
- Add `developer.test`, a sensitive/mutating fixed `dotnet test` wrapper with workspace path validation, cancellation/owned-process cleanup, bounded stdout/stderr and structured passed/failed/skipped/total parsing.
- Add a DAP adapter launcher/session contract that validates adapter/workspace inputs and owns only the process it starts; stop/cancellation kills that owned adapter and no arbitrary attach/injection API is exposed.
- Add `HarnessEvaluator` machine-readable metrics plus `scripts/Run-HarnessEvaluation.ps1`, which runs deterministic real regression groups for dynamic catalogs, policy/pause, adaptive no-replay, tool code mode/plugins, delegation/SQLite memory, developer tools, stale Computer Use state and task transport robustness.
- Publish the two developer tools through the dynamic Agent host so they retain the same local schema, Arm/Pause, exact-permission and approval boundaries as other tools.

## 1.0.53 - 2026-09-16

- Add durable task lineage (`parentTaskId`, `rootTaskId`, `depth`) to task snapshots and persist it across agent restart/reopen while preserving top-level create digest compatibility.
- Add bounded child-task creation: same owner/device/project, child execution mode may only stay equal or narrow, maximum depth 3 and maximum 8 direct children per parent.
- Add `RemoteTaskDelegation.JoinAsync` for bounded fork/join coordination with cancellation and terminal-state polling; expose optional `parentTaskId` through REST and `agent_task_create` MCP input.
- Replace the misleading in-memory `SqliteMemoryStore` with a real SQLite store using WAL, parameterized queries, owner/project/namespace partitions, provenance, upsert, TTL expiry and bounded search.
- Pin SQLite dependencies to `Microsoft.Data.Sqlite.Core` 10.0.11 / `SQLitePCLRaw` 2.1.12 to avoid the 2.1.11 high-severity restore advisory observed during RED/GREEN work, and disable connection pooling so store disposal releases database files deterministically.

## 1.0.52 - 2026-09-16

- Add `tool_program.run`, a bounded JSON interpreter with call/set/if/forEach/assert/return operations and hard instruction/tool-call/time/output budgets; it has no eval, shell escape or direct OS API.
- Route every nested program call back through the existing guarded local invoker so nested tools independently enforce installed schema, Arm/Pause, exact permission and approval policy; recursive program calls are rejected.
- Add composite-tool scheduling so the outer orchestrator releases execution/interactive slots after approval before nested calls, avoiding nested-call semaphore deadlocks without weakening nested policy.
- Add local plugin manifest/catalog contracts with min-agent-version checks, entry-file SHA-256 pins, bounded declarations, supported lifecycle hook names and path-traversal rejection.
- Bind plugin declarations only to locally supplied implementations and hot-project validated plugin tools into `DynamicToolRegistry`; manifests do not download or load arbitrary remote code.

## 1.0.51 - 2026-09-16

- Add validated adaptive DAG contracts plus an async plan/execute/verify/replan loop that never replays already verified actions.
- Bound repairs to the failed logical action and a maximum repair budget; replacement actions keep the same logical ID and cannot introduce unmet dependencies.
- Add optional Task Gateway adaptive coordination for `AUTONOMOUS` tasks only. Normal/read-only task execution remains deterministic, and cancellation/timeout is never automatically repaired.
- Validate gateway repair replacements for logical ID/stage, installed tool/schema and existing local permission/approval boundaries before execution.
- Preserve default behavior when no adaptive coordinator is configured; the gateway does not silently invent an LLM planner.

## 1.0.50 - 2026-09-16

- Add opaque per-session computer observation state IDs and generations; stale, evicted, invalidated or cross-session state fails closed before desktop input reaches the baseline tool.
- Wrap `computer.computer_batch` with a required current `stateId`, strip Jarvis metadata before vendor dispatch, and invalidate the state after dispatch so coordinates cannot be blindly reused.
- Add `computer.get_state` with foreground-window identity, focused UI Automation metadata and a bounded accessibility-tree snapshot; UIA access failure returns partial state without escalating privileges.
- Append fresh state metadata to successful standalone screenshots and invalidate all computer state on local Pause/owned-activity stop.
- Preserve existing app grants, denied-app handling, Arm/Pause, local approval and Full Permission boundaries.

## 1.0.49 - 2026-09-16

- Add a runtime `DynamicToolRegistry` with immutable snapshots, catalog generations, canonical SHA-256 descriptor digests and precompiled local schemas.
- Make ordinary remote calls and Task Gateway validation resolve the current installed-tool snapshot at execution time while preserving Arm/Pause, schema, approval and exact-permission gates.
- Add optional thread/turn correlation to the wire execution context and a local lifecycle hub for interrupt/stop/subagent-stop cleanup notifications.
- Add release-version verification so `VERSION`, root package/assembly/file versions, README heading and the newest CHANGELOG release cannot silently drift.
- Preserve no-replay semantics and keep dynamic discovery non-authoritative for permission: replacing a catalog never grants tool consent.

## 1.0.48 - 2026-09-16

- Add an authenticated Task Gateway over the existing outbound agent WebSocket, with additive task-v1 capability negotiation; older agents retain ordinary tool calls.
- Expose create, submit-plan, status, paged artifacts and cancellation HTTP APIs plus six OAuth-device-bound MCP task tools and canonical installed-tool schema discovery.
- Run client-supplied engineering plans through the same installed-tool schema, local Arm, standing-permission and explicit-approval gates as ordinary tool calls. Task modes do not grant permissions.
- Persist bounded task snapshots and text artifacts atomically on the agent; repeated identical IDs do not replay actions, changed payloads conflict, and interrupted work never automatically resumes.
- Wire real owned process start/read/cancel to step execution and wait for actual exit codes. Preserve partial output on timeout/cancel, stop later stages after failure, and permit bounded retries only for non-sensitive read-only tools.
- Distinguish FAILED task deadlines from INTERRUPTED transport loss. Retain original README/changelog history, use assembly-derived health/CLI versions, and align package/assembly/file versions at 1.0.48 / 1.0.48.0.
- Add offline cache-only packaging and a full-solution build before release tests. Keep NoRestore semantics consistent for agent and server publishing.
- Scope: goal-only requests remain NEEDS_PLAN until a client supplies steps. This release is not a configured LLM planner or SQLite memory implementation, does not deploy/restart live services, and makes no Codex-parity claim. See docs/AGENT-TASK-GATEWAY.md and docs/BUILD-STATUS.md for executed evidence.


## 1.0.23 - 2026-09-15

- Recognize the seven required System.Private.* framework DLL filenames during packaging; continue rejecting private configuration, credentials and font binaries.
- Detect Windows through the runtime platform rather than the optional OS environment variable when packaging the Agent.
- Add multi-folder selection, remove/additional-folder management and primary-folder selection to the Agent GUI.
- Persist additionalDirectories with backward compatibility for the existing single-workspace profile; pass them through connection and native tool contexts. CLI configure supports the same folders.
- Add an optional workingDirectory to managed jobs; shell/process commands are not confined by a project-directory sandbox.
- Integrate the supplied Jarvis icon in the executable, main window, sidebar, tray and local dialogs.
- Add multi-directory/path/profile regression tests and bump package/assembly versions to 1.0.23 / 1.0.23.0.
- Scope limitation: workspace-guard removal and the full-permission tab were blocked by the editing platform and are NOT part of this release. Existing approvals and file guards remain; unfinished permission integration was removed.

## 1.0.22 - 2026-09-15

- Add tool-card checkboxes, select all filtered tools, clear selection, and confirmed bulk publish/disable actions.
- Add an admin/CSRF protected bulk API; validate revisions and installed capabilities before atomically updating availability and audit records. No automatic publishing.
- Replace the 30-minute local-control lease with an explicit process-local arm state that lasts until Pause, manual Disconnect or Exit. Restart always starts paused.
- Temporary transport reconnects keep the local arm choice, cancel interrupted jobs and never replay them. Per-tool consent, call deadlines and authentication stay unchanged.
- Remove the countdown/lease timer from Desktop and align CLI prompts. Assembly/FileVersion 1.0.22.0.
- Execution evidence for build, test, package and deployment will be recorded in docs/BUILD-STATUS.md.


## 1.0.21 - 2026-09-15

- Fix Chromium OAuth consent navigation: retain same-origin form submission and permit the exact validated client callback origin only on the consent document.
- Keep default CSP, PKCE, exact redirect registration, device ownership and CSRF checks enabled; no wildcard or global security-policy relaxation.
- Add consent CSP regression coverage and native Chromium navigation verification; do not confuse an HTTP 302 test with browser navigation success.
- Bump package/assembly/file versions to 1.0.21 / 1.0.21.0. Final executed evidence is recorded in docs/BUILD-STATUS.md.

## 1.0.20 - 2026-09-15

- Publish the existing /connect/register DCR endpoint in both OpenIddict discovery documents with the supported public-client token authentication method none.
- Use the OpenIddict 7.7 HandleConfigurationRequestContext extension point; construct metadata URLs from the configured issuer, not Host headers.
- Stop advertising openid because the consent flow issues only MCP access/refresh grants. Preserve PKCE S256, exact callback allowlists, CSRF, resource/device binding and token validation.
- Add discovery-driven OAuth/MCP integration regression tests and a non-secret verification script; document ChatGPT Dynamic Client Registration and user-defined client setup.
- Bump package/assembly/file versions to 1.0.20 / 1.0.20.0. No production credential rotation or wildcard callback policy is part of this patch.


## 1.0.19 - 2026-09-15

- Include all four reused vendor projects in the root and Agent solutions so Visual Studio owns their restore/build graph; preserve their project references and all tool implementations.
- Reproduce the missing Agent Windows reference assembly; the initial vendor restore assets contain NU1012 with an unversioned Windows platform, preventing dependent build outputs from completing.
- Remove the redundant ProtectedData PackageReference only from Agent Windows. Windows desktop framework supplies it; vendor Host retains its required package.
- Retain the existing standalone JarvisCode.App host exclusion; do not delete tool DLLs or disable reference assembly generation.
- Add Verify-AgentReferences.ps1 to verify complete solution membership, canonical Windows restore targets, restore errors and versioned reference assemblies; retain Verify-AgentOutput.ps1 for standalone-host exclusion checks.
- Bump package/assembly/file versions to 1.0.19 / 1.0.19.0. Verification results are recorded in docs/BUILD-STATUS.md after execution.


## Unreleased - Agent build output fix (2026-09-15)

- Exclude the standalone JarvisCode.App executable/deps/runtimeconfig from agent Debug and Release build copy lists as well as publish output.
- Clean only the three exact obsolete filenames from existing agent output folders; preserve required tool DLLs, PDB symbols, assets and the standalone vendor project.
- Add scripts/Verify-AgentOutput.ps1 to check for unwanted host files and missing runtime dependencies.
- Scope the build policy to Jarvis.Agent.Desktop, Jarvis.Agent.Cli and Jarvis.Agent.Windows; skip filesystem cleanup during design-time builds.

## 1.0.18 - 2026-09-14

- Preserve OAuth authorization parameters in the consent POST body, including PKCE, state and resource. Keep CSRF and local device checks enabled.
- Register MVC view/antiforgery services required by the consent validation filter; do not bypass CSRF.
- Add browser-form OAuth regression coverage for state escaping, CSRF, deny, PKCE/resource failures, replay and refresh.
- Handle concurrent WebSocket disposal during disconnect/cancellation without an unhandled exception.
- Exclude only the unused standalone JarvisCode.App executable manifests from Agent publish; retain its tool assembly and duplicate-file validation.
- Apply the Window theme explicitly to the derived WPF main window after a real Windows render revealed the missing background.
- Make package/export names follow VERSION and add an existing-service upgrade script with health-gated command rollback; private server configuration and data remain in place.
- Synchronize assembly/file/package versions at 1.0.18 / 1.0.18.0. Build, regression and runtime evidence will be recorded in docs/BUILD-STATUS.md after execution.

## 1.0.17

### Added
- Production deployment baseline for Jarvis MCP Server.
- linux-arm64 deployment notes.
- MCP Streamable HTTP deployment documentation.
- Agent local build baseline.

### Security
- Private certificates and OAuth secrets must remain external runtime configuration.

### Commit
feat(jarvis): prepare v1.0.17 agent build baseline


## 1.0.47

- Add autonomous execution loop tests.
- Validate retry policy behavior.
- Update agent harness documentation.
- Add runtime identifiers and assembly version alignment for agent packaging.
- Fix agent publish target resolution for win-x64 packaging.
- Allow publish restore to resolve runtime assets when package generation is executed.
- Align package metadata with VERSION 1.0.47.


