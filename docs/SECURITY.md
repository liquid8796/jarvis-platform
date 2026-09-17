# Security boundaries and threat model

## 1.0.64 multi-session boundaries

Application handles are protected with the server's existing persisted Data Protection key ring, bound to authenticated owner and enrolled execution device, expire after 30 days, and are never returned by list/metadata operations. They are not a replacement for OAuth, per-tool approval, persistent constrained-process grants, or Arm/Pause. Retain and protect the server data directory during upgrades. Never log or copy handles into inter-session messages.

The local session store validates owner/device/session and closed state again at dispatch. Queued calls keep immutable workspace snapshots. Per-session process/task ownership prevents a sibling from reading stdin/output or cancelling another chat's job by guessing an ID. The native bridge stamps session identity outside browser arguments; extension ownership checks include read/origin/close paths. Separate per-session browser origin gates and computer-service state do not weaken denied-app or OS/UIPI boundaries.

Global desktop exclusion and shared observation invalidation prevent stale-coordinate actions after another session changes the desktop. File and repository claims use canonical paths; protected agent directories stay excluded even through filesystem links. Existing-file writes require a current session-local SHA-256 observation. These are coordination safeguards for managed calls, not an OS sandbox, a browser-profile boundary or an atomic transaction against unrelated external file writers.

Registry/mailbox responses contain only explicitly stored metadata and coordination events. Messages from another agent are untrusted data, not user consent or higher-priority instructions. No chat transcript harvesting, automatic transcript sharing, cross-account routing or idle-chat wakeup is implemented. Explicitly invoked durable project journals retain their existing project-scoped sharing semantics.

Queue, mailbox, session and browser-history capacities are bounded. Legacy clients talking to a modern agent must open a session; old agents are supported only through explicitly negotiated compatibility. Update the browser extension rather than bypassing capability checks. Emergency Pause remains global; Stop/Close on the Sessions page is scoped. All runtime/permission UI validation in this release uses isolated test fixtures, not live protected Agent controls.

## Protected access

An active user, an enabled owned device, a valid device-bound OAuth grant, a published matching capability, and a locally armed agent are all required. Opaque enrollment tokens are SHA-256 hashed at the server, shown once, expire after 30 days, and stored with Windows DPAPI CurrentUser locally. OAuth uses OpenIddict, code+S256, exact allowed callbacks, short-lived reference access tokens and refresh grants. Revoked token entries are checked; device/account validity is checked per call and periodically on connections. Local arming cannot be set via a remote envelope.

Web uses Identity password hashing/lockout, HttpOnly cookies, no persistent localStorage token, anti-forgery validation for unsafe cookie APIs, explicit consent, CSP, encoded untrusted text and rate limits. Production signing/encryption PFX keys persist outside releases; Data Protection keys persist in private application data. Rate limits and local caps are not a substitute for provider-level DDoS protection. User creation is pending by default; there is no fake email-verification or automatic password recovery service.

## Important non-guarantees

**A project path guard is not a process sandbox.** Shell, browser JavaScript and desktop control have the permissions of the Windows account. Local approval of an arbitrary command is approval of that authority. Use a dedicated non-admin Windows account or VM containing no unrelated secrets. Do not put production credentials in that session. Filesystem adapters canonicalize and reject traversal, links/junctions/ADS, and the agent private profile. Concurrent filesystem replacement can still produce TOCTOU races; no kernel-enforced file sandbox is claimed.

Computer-use cannot operate an elevated/UAC secure desktop without rights that this program does not request. The GUI must stay in a signed-in, interactive Windows session. Native computer/browser grants from the baseline stay in force in addition to remote-host policy. Sensitive results such as screenshots are sent through the server to the authorized MCP client; metadata-only audit does **not** mean the server cannot observe tool content in transit. Client model providers receive results needed to answer the user's request.

HTML widgets run in a sandboxed frame without same-origin; WebView2 host objects, web messages, downloads, permissions and external network requests are disabled. This intentionally prevents CDN libraries, arbitrary links and baseline `sendPrompt` behavior. Visual HTML is still untrusted content; WebView2 and Chromium must be kept patched by the operator. Files saved by CLI are local artifacts, not proof of successful visual display.

## Defaults and operator actions

No unattended auto-arm, no startup persistence, no stealth mode, no global machine service for the agent, no remote arbitrary plugin upload. Explicitly enable only necessary tools; inspect package dependencies and baseline code before trusting it. Do not copy keys into source. Restrict Linux config/data to mode 0700 directories and 0600 secrets, back them up encrypted, and remove initial Bootstrap credentials after creation. Default TLS proxy trust is loopback only; do not clear all known-proxy restrictions.

Use the local Pause button, tray menu, hotkey, disconnect, or process exit to stop remote control. Pause cancels pending tool calls and owned managed jobs; cancelling a process cannot undo previously written files or network requests. Disabling a device/rotating enrollment closes its server connection. `Revoke my grants` rotates account security stamp, closes agents and signs out; a new app process starts paused. An automatic transport reconnect in the same process retains the prior explicit arm choice but still needs valid credentials.

## Production readiness gate

This source has not undergone a production penetration test, package vulnerability scan, Windows runtime smoke test, real ChatGPT OAuth integration or VM deployment test. Treat it as an implementation to build/review/test before handling sensitive projects. Do not use this release as a remote administrator on a machine containing banking/production secrets without an isolated acceptance environment.

## Manual arm and bulk publishing - 1.0.22

There is deliberately no maximum duration for an explicitly armed running Agent. Pause, manual Disconnect, Ctrl+C in CLI, or Exit clears the process-local grant. It is not saved in the profile and cannot be activated remotely. Transient transport loss cancels calls/jobs; reconnect preserves the choice but never replays an action. Per-action approval and command timeouts remain. Bulk publication is admin-only, CSRF-protected, revision-checked, and commits audit records in the same database transaction; importing or selecting tools alone never publishes them.

## Multi-directory update - 1.0.23

The file boundary now checks the explicitly selected primary and additional directories, rather than only one root. Paths under unselected roots and links/junctions remain rejected. Directory selection is not an OS sandbox for arbitrary commands, browser operations or desktop input. Managed jobs can specify an explicit starting directory; they retain approval, ownership, timeout and cancellation controls.

No new silent-action mode or full-permission settings are shipped in this version. The requested guard removal/native-consent changes were blocked by the editing platform. No live enrollment profile, local permission grants or remote service was changed during implementation/testing.

## Stale desktop-state protection - 1.0.50

Computer input is now bound to a fresh opaque observation state. A `stateId` is valid only for the session that observed it and only while it remains that session's current state. New observations, local Pause and any dispatched input batch invalidate older state. Stale/cross-session IDs fail before the baseline input tool runs. The token is synchronization metadata, not an authorization credential; Arm/Pause, tool permission, local approval, frontmost-app and denied-app checks still apply independently.

The new accessibility snapshot is deliberately bounded and best-effort. It reads only information Windows UI Automation exposes to the current non-elevated user session, clips returned strings/nodes, and treats access exceptions as partial results. It does not request elevation or interact with UAC secure desktop.

## Tool-program, Safe Script and plugin safety - 1.0.52 / 1.0.60

The deterministic `tool_program.run` remains a small JSON instruction language with no dynamic evaluation, arbitrary process creation or direct file/network primitive. `tool_script.run` adds JavaScript only inside a fresh Jint interpreter with explicit script/statement/memory/wall-time/call/argument/output budgets. Jarvis does not enable CLR interop and injects no Node `process`/`require`, filesystem, socket, fetch or browser host objects. Its only host capability is `invokeTool(toolId,argsJson)`. Both code modes reject composite recursion, and every nested call goes through the same local installed-schema, Arm/Pause, exact-ID/capability permission and approval checks as a top-level call. A nested failure or denial makes the entire Safe Script fail even when JavaScript attempts to catch the Promise rejection, so script control flow cannot hide a rejected authority check.

Plugin discovery accepts only top-level local manifests, rejects path traversal and unsupported hooks, verifies a 64-hex SHA-256 pin for the referenced local entry file, and binds declarations only to tool/hook implementations already present in the host process. 1.0.59 additionally bounds Agent compatibility, skill roots, MCP dependency/provenance metadata and hot-reload behavior. The watcher validates a complete next snapshot before publication and keeps last-known-good state on malformed/tampered metadata; lifecycle-hook exceptions are isolated. Local install/update is atomic with rollback and uninstall removes only the manifest. Remote arbitrary plugin upload/loading remains unsupported.

## Delegation and memory isolation - 1.0.53

A `parentTaskId` is lineage metadata, not authority. The Agent loads the parent from the current owner's local store, requires identical resolved project/device scope, prevents execution-mode escalation, and enforces local child/depth bounds before persisting the child. The server cannot forge a different owner in the task request.

Durable memory uses parameterized SQL and bounded keys/values/provenance/search limits. Partitions include owner, project and namespace in every lookup/upsert primary key; expired records are not returned. SQLite dependency versions are pinned above the vulnerable 2.1.11 native bundle observed by NuGet audit during implementation.

## Developer-tool boundary - 1.0.54

`developer.symbol_search` rejects paths outside selected workspaces and skips dependency/VCS/build directories. `developer.test` does not accept an arbitrary command: it always starts `dotnet test` with argument-list encoding, shell execution disabled, bounded timeout/output and owned-process cancellation; it remains sensitive/mutating for local approval purposes.

DAP support exposes no generic attach, injection or process-control tool. A launcher may start one explicit existing adapter executable and the resulting session can stop only that owned process. The offline evaluation script executes repository tests locally and contains no credential/model dependency.

## Scoped process authority and catalog freshness - 1.0.55

Saved exact-ID grants for `process.start` or `process.spawn` are not sufficient to authorize arbitrary commands without prompting. Full-permission execution for process surfaces requires an active capability lease whose session/turn, expiry, allowed workspace roots and command prefix match the actual invocation; shell commands use a boundary-aware text prefix and argv spawns compare prefix tokens directly. Lease expiry or revocation emits the same cancellation signal used for standing-permission revocation. These checks do not turn workspace roots into a kernel sandbox and do not undo effects already performed before cancellation.

Dynamic catalog discovery remains non-authoritative. The agent sends immutable generation/digest-tagged snapshots, the server validates and persists them before acknowledging, and later calls are bound to the acknowledged catalog identity. A stale generation/digest is rejected by the agent before tool lookup/schema execution. Catalog ACK protects synchronization/TOCTOU; it is not a permission grant and cannot Arm the Agent.

## Diagnostic disclosure boundary - 1.0.56

Protocol capability names describe supported features only and are not trusted authorization claims. The server intersects advertised protocol-v2 names with its own supported set; a legacy/absent protocol version remains compatible with the stable wire-v1 envelope.

`jarvis-agent doctor --json` is intentionally local and redacted. It emits versions, generic status/error-type labels, booleans, counts and catalog generation/digest only. It does not emit enrollment/OAuth credentials, raw workspace/plugin/task paths, saved permission tool IDs, capability lease IDs/session IDs, plugin manifest bodies, commands, tool arguments/results or screenshot/browser content. Doctor performs bounded read-only metadata checks and cannot Arm the agent or grant permission.

## Durable thread data boundary - 1.0.58

Thread history is stored locally under the Agent profile in SQLite and is not itself a permission grant. `thread.create/search` resolve projects through the current selected workspace set; every operation on an existing thread re-checks its persisted project before reading or mutating state. Tool schemas and runtime validation bound title/goal/item/artifact/queue/search sizes.

Compaction and rollback never erase journal rows. This avoids presenting a compacted or rolled-back projection as proof that earlier side effects vanished. Artifact entries are references/metadata, not automatic file reads or uploads; callers still need the separate file/tool permissions to access referenced content.

## Owned interactive process boundary - 1.0.57

`process.spawn` never inserts a shell: executable and arguments are passed separately. Environment overrides are bounded and cannot contain invalid environment names. `process.write_stdin`, `process.resize_pty`, `process.read` and `process.cancel` resolve only opaque IDs in the Agent-owned registry; there is no PID attach surface.

Windows ConPTY children are created suspended, attached to a kill-on-close Job Object, then resumed. This closes the pre-ownership spawn window for PTY descendants. ConPTY is still code execution under the logged-in user's OS authority, not a sandbox. Local Arm, schema validation, scoped capability leases, Pause/disconnect cleanup and owned-process cancellation remain the security boundary.
