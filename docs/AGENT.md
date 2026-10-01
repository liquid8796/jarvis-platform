## 1.0.103 permanent session deletion and server revocation

The **Sessions** page now separates resumable cancellation from permanent deletion. **Stop work / Stop selected** affects only open sessions and leaves their protected handles usable. **Delete session / Delete selected** requires confirmation, cancels only the selected sessions' owned work, removes their local SQLite metadata and mailbox events, and records an indefinite tombstone. Retained closed sessions can be selected for deletion; unrelated sessions and global Arm/Pause state are unchanged.

Matching Agent/server builds negotiate `application-session-deletion-sync-v1`. The Agent sends at most 200 deletion rows per frame and replays all tombstones after every reconnect. A failed send does not restore local data or lose the revocation. The server derives owner/device identity from the authenticated enrolled WebSocket, persists a monotonic tombstone, and rejects the former handle as `SESSION_DELETED` before dispatch even when the Agent is offline. Session IDs in a payload cannot select another owner/device.

The local tombstone is intentionally not pruned while protected handles have no fixed expiry. Deletion is idempotent and a deleted `js_...` identity cannot be reopened. Upgrade both components to **1.0.103** for end-to-end synchronization; staged upgrades remain compatible. Package **1.0.103**, assembly/file **1.0.103.0**.

## 1.0.102 browser extension reconnect recovery

Jarvis Agent Browser **1.4.1** persists native-host recovery through Manifest V3 worker suspension. Fast JavaScript-timer retries remain, but a chrome.alarms wake-up retries every 30 seconds until the local Agent browser service accepts the extension. Browser startup and extension install/update events also reconnect, and disconnect callbacks are tied to the exact port that raised them so an old port cannot clear a replacement connection.

If the Agent is run as Administrator, Chrome's native-messaging host still starts at medium integrity. Version 1.0.102 creates only the extension-facing named pipe with a medium mandatory-integrity label while retaining a protected DACL for SYSTEM plus the current Windows user and rejecting remote clients. This removes the Windows MIC block that previously made an enabled extension appear disconnected under an elevated Agent; it does not grant another account access, lower the Agent process, or bypass browser session ownership.

After installing Agent **1.0.102**, use Connection center's browser setup once and reload the unpacked extension. This refreshes the assets and manifest without changing its stable extension ID or importing cookies. Package **1.0.102**, assembly/file **1.0.102.0**.

## 1.0.101 process-stop truthfulness and audit redaction

Durable process status is observation-based. For non-elevated launches, Jarvis checks the Windows Job Object termination result and continues monitoring descendants until the tree is gone. Elevated launches cannot be assigned to the medium-integrity Agent's Job Object; when Windows rejects Kill, process_stop returns state with termination_unconfirmed as true and the launch remains running until a later process_get observes its actual exit. Do not interpret a stop request as proof that an elevated application closed. If the Agent exits while Windows is rejecting termination, that elevated child can remain alive and must be verified or closed by the operator.

The immediate tool response still contains the actionable validation or access error. Persistent Agent audit files instead record a bounded reason code and generic failure description, preventing command text, paths, arguments and exception payloads from being copied to disk. Package **1.0.101**, assembly/file **1.0.101.0**.

## 1.0.100 durable launches, UAC consent, diagnostics and audit

`process_launch` starts a directly owned executable instead of launching it through a short-lived shell. It requires an explicit Jarvis application session and returns a `launch_id`; use `process_get` and `process_stop` with the same session. On Windows, a non-elevated process tree stays in an Agent-owned job object. The initial executable may exit after starting a child, but the launch remains running while descendants remain in that job. A transient transport reconnect and `session__stop_work` do not reap the durable tree. Natural tree exit, `process_stop`, application-session close, explicit local Pause, permission revocation, optional timeout, or Agent exit ends it.

Tool permissions has an independent **Allow Full Permission tools to request Windows UAC elevation (Administrator)** setting. A caller must also hold Full Permission for `process.launch`, request `run_as_administrator: true`, and complete the normal Windows UAC prompt. The option is not persisted in the ordinary tool-ID set and can be revoked independently. It does not grant SYSTEM, duplicate privileged tokens, cross integrity boundaries without UAC, access protected processes, bypass anti-cheat, or circumvent browser/enterprise/OS protections.

`binary_inspect` reads bounded file size/hash/version/Authenticode-certificate/PE-header/section data and printable string samples without executing the file. `process_list` and `process_inspect` use query-only process APIs for basic identity, architecture, elevation/integrity and an optional bounded module list. These tools do not implement debugger attach, process-memory reads/writes, injection, thread manipulation, token duplication or protection bypass.

Agent audit records now persist reason codes such as pause, permission revocation, transport interruption, queue rejection, process start/stop/timeout and tool completion/failure. Files rotate at 5 MiB and records older than 14 days are removed. Session identifiers are stored as short SHA-256 correlation values; arguments, prompts, file contents, command text and browser payloads are not recorded. `audit_query` defaults to the current explicit session; `all_sessions: true` requires Full Permission.

Browser Full Permission now supplies standing origin consent to Jarvis' browser-origin gate for the current invocation. It does not add the domain to Allowed Sites and does not override website authentication, browser extension selection, Chrome/Edge policy, denied applications or Windows permissions. ImageGen deep-clones browser state before composing responses so a transport-owned `JsonNode` cannot fail with `The node already has a parent`.

The active local Blender configuration points to the managed `mcp-for-blender 2.1.1` Python runtime and its bundled protocol-11 addon. Live probing on 2026-09-29 reported all 36 tools, addon compatibility with Blender 5.2.1 LTS and telemetry disabled. Poly Haven preview plus all four Tripo lifecycle names are present; no Premium generation or credit-spending operation was used to certify the activation.

## 1.0.82 mouse wheel routing

The desktop shell now captures mouse-wheel input before child controls can consume it. When the cursor is inside nested containers, lists or other components, the nearest scrollable parent receives the wheel movement while preserving normal edge behavior.

# Jarvis Agent operator guide

## 1.0.81 plain-text user-managed prompt context

The fifth Agent navigation tab, **Prompt injection**, manages local prompt presets. Use **Add prompt**, select a row to edit its title/text, toggle individual entries, and turn on **Attach enabled prompts to MCP replies**. All changes, including deletions and switches, are drafts until **Save changes**. **Reload saved** discards a dirty draft only after confirmation; **Restore defaults** explicitly replaces the draft with the seven disabled examples. Deletion also requires confirmation. Preview shows the draft context, while the saved revision/status describes what the running Agent will use. Titles are editor metadata only. Body whitespace, line endings and Unicode are preserved through preview, save/reload and delivery; this release does not reconstruct whitespace already removed by older versions.

The seven examples cover attach/inject/hook planning, live-memory state inference, live entities/pointers, memory snapshots/comparison, changing byte/field investigation, controlled memory writes and signal-driven overlays. They are editable workflow text, **not implementations or capability grants**. They do not install a debugger, read/write process memory, bypass anti-cheat or guarantee that a game account is safe. Existing tool permissions, local approval, Arm/Pause, schema validation and OS protections remain in force.

Settings live in `prompt-injection.json` under the existing per-user Agent settings directory, separate from enrollment credentials and tool permissions. The initial global switch and all examples are disabled. A valid saved empty list stays empty on reload. Saves use a revision check, file lock and atomic replacement; a corrupt existing file is preserved rather than reset. Limits are 64 entries, 120 characters per title, 4,000 per body, 16,000 enabled title/body characters and 64,000 stored title/body characters; serialized settings are limited to 512 KiB. The rendered body text, including the two newline characters inserted between enabled bodies, must also fit 16,000 characters. Oversize drafts are rejected before saving/applying; no silent truncation is performed. Do not put passwords, enrollment tokens or unrelated private data into prompts.

### Delivery and scope

Agent and server negotiate the additive `user-prompt-context-v1` capability. On each subsequent successful top-level tool reply or Agent task lifecycle reply, the Agent takes the latest saved enabled snapshot and includes it as optional transport metadata. The server appends one separate MCP text-content block containing only the enabled preset bodies, in saved order, joined with `\n\n` (one blank line). It adds no source banner, title/ID/revision prefix, nested JSON envelope, synthetic fence or invisible-character substitutions. Each body remains exactly as saved. Literal JSON or fence-looking strings typed by the user remain literal body text. MCP JSON-RPC, Agent/server transport metadata and the local settings file still use JSON; only the extra JSON envelope inside the model-facing text has been removed. JSON serialization is not encryption. The original first text block, images and structured-result schema remain unchanged. The context is explicitly user-editable, not a system/developer message, proof of consent or permission grant. Invalid optional metadata is dropped at the MCP adapter rather than replacing the actual tool result.

Both Agent and MCP server must support this capability. Upgrade both to 1.0.81 for matching plain-text preview, whitespace-preserving saves and server rendering; the existing capability name and wire fields are unchanged. A server that does not advertise this capability receives no prompt context; the tab reports the missing support. A 1.0.80 server that advertises it can still receive the metadata but renders the older labelled/JSON format until upgraded. Saving while connected applies to future replies without restarting. Saving while disconnected applies on the next connection. This patch does not automatically deploy or restart the already-running Agent or OCI service.

Presets are **per local Agent settings profile**, not per chat or workspace: every authorized chat routed to that Agent can receive the enabled text. The text is shared with the connected MCP server/client and may enter the client's conversation history. Disabling/deleting a preset stops attaching it to subsequent replies; it cannot remove copies already sent. The client may ignore, truncate or retain this optional context, so delivery is not proof that a model followed it.

No context is added to failed/denied replies, `tools/list`, the server-only `agent_task_tools` inventory, or reasoning before the first successful Agent reply. Internal task steps/artifacts and the built-in deterministic planner are not rewritten. This implementation is an opt-in Jarvis tool-reply context attachment, **not** registration of native MCP `prompts/list` or `prompts/get` templates. It cannot force higher-priority host instructions to change.

Plain text is a presentation format, not a role or security upgrade. The host controls model message roles; this adapter returns ordinary MCP tool-result content and does not configure a host system/developer message. Removing a provenance banner or JSON envelope cannot establish higher priority, guaranteed model obedience or account safety. Fence strings and zero-width substitutions are not model-level security boundaries either. Approval, Arm/Pause and enrolled-agent isolation stay outside the prompt text. Tests verify exact transport and execution boundaries, not resistance or susceptibility of a particular model to prompt injection. References: [MCP tool-result content](https://modelcontextprotocol.io/specification/2025-11-25/server/tools), [MCP prompt messages](https://modelcontextprotocol.io/specification/2025-11-25/server/prompts), and [MCP hints versus enforcement](https://blog.modelcontextprotocol.io/posts/2026-03-16-tool-annotations/).

## 1.0.77 multi-session actions (updated by 1.0.103)

The **Sessions** list has a checkbox on every visible session so several chats can be selected at once. **Select all** respects the current search and **Show closed sessions** filter; **Clear** resets the bulk selection. Closed sessions cannot be stopped, but since 1.0.103 they can be checked for permanent deletion of retained metadata.

Use **Stop selected** to cancel accepted work/resources only for checked open sessions while leaving their handles resumable. Use **Delete selected** to remove local metadata/mailbox history and synchronize permanent server-handle revocation; one confirmation covers the whole batch. Filtering a checked session out of the visible list clears its bulk selection so a hidden row is never mutated accidentally. The existing single-row **Stop work** / **Delete session** controls remain available, and global **Pause** is still the separate all-session stop.

## 1.0.72 selectable session identity

The **Sessions** list now shows the raw session ID directly beneath every session name. Both values use read-only selectable text: drag across the name or `js_...` ID with the mouse, then use `Ctrl+C` or the standard context-menu copy command. These identity fields intentionally stay out of the keyboard tab sequence so repeated rows do not create noisy navigation; the surrounding session row remains the selection target for Stop work, Close session and expanded details.

## 1.0.70 visual fidelity and coding benchmarks

Visual/layout autonomous tasks now require `VisualFidelity` evidence. The goal verifier can return a bounded `VisualFidelityLedger`; unresolved blocking mismatches in layout, typography, color, iconography, overflow or interaction-state categories are converted into failed evidence and feed normal verification debt. Resolved and non-blocking mismatches remain in the ledger for audit without preventing completion.

The engineering harness now has a stable six-scenario coding suite covering modal repair, responsive clipping, API error states, stale loading states, reference-driven visual regression and backend regression. `EngineeringEvidence` records build, test, browser, console, interaction and visual proof together with corrective-loop/evidence counts. `HarnessEvaluator.EvaluateEngineering` exposes additive quality rates while the historical transport/tool throughput evaluator remains backward-compatible.


## 1.0.69 progressive skill context and browser observations

Agentic planning and goal verification can now discover enabled plugin skills as compact metadata and explicitly load selected `SKILL.md` instructions. Discovery is bounded to catalog-validated `SkillRoots`, does not execute plugin code, rejects reparse traversal/out-of-root paths, limits skill size to 256 KiB and isolates invalid skills as diagnostics. Full Markdown bodies are decoded only after explicit selection; both planner and goal-verifier contexts expose the same selection contract.

Browser automation now carries a per-session observation generation. `browser.read_page` and `browser.find` replace the current ref set. Material mutations invalidate it, including navigation, form input, JavaScript, uploads, tab/browser switching, viewport resize and interactive `browser.computer` actions. Any later `ref`/`ref_id` is checked against the current generation before the underlying browser tool executes. This complements tab ownership and computer `stateId` checks: browser DOM refs and desktop coordinates now both have explicit freshness semantics.


## Prompt continuity in 1.0.65

Ordinary MCP tools no longer depend on the model replaying `_jarvis.sessionHandle` across turns. Without a handle, the gateway creates a per-call ephemeral execution ID and the agent uses a stable authenticated owner/device isolation scope for local state that must survive a later prompt. Explicit `session__open` remains available when a chat needs its own workspace, mailbox, browser application session or coordination metadata; `workspace__*` and `session__*` management calls still require that protected handle.

`session__stop_work` cancels only the explicit session's accepted work/resources and leaves the session resumable. Destructive `session__close` is no longer advertised in the normal MCP tool list and remains a raw terminal-close compatibility path. The Agent Sessions UI uses the stronger 1.0.103 delete operation, which removes retained local metadata and synchronizes server-handle revocation. Sessionless process jobs, read-before-write file observations, computer state and browser/tool adapters are scoped by authenticated owner + enrolled device rather than by a model-carried secret. Explicit sessions retain strict `js_...` ownership boundaries.

Connection status now exposes reason-coded states (`CONNECTING`, `RECONNECTING`, `CONNECTED_HEALTHY`, `HEARTBEAT_STALE`, `TRANSPORT_INTERRUPTED`, `DISCONNECTED`). Gateway rejections before agent dispatch are audited by error code without recording tool arguments or session handles. A missing/invalid handle must not make ordinary tools unavailable; it affects only explicit session/workspace operations.

## Multi-chat operation in 1.0.64

The default workspace is optional. Connection center can clear it; Save defaults updates only newly opened sessions, not active chats or already accepted requests. Relative file paths and shell/git calls require a session workspace or explicit call `workingDirectory`. Absolute file paths work without a default. Project journal tools require a selected session project; a missing scope returns WORKSPACE_REQUIRED. CLI visual output with no workspace goes to the agent's dedicated local visual-artifacts area, never an accidental executable directory.

In each independent chat, call `session__open` and keep its returned `_jarvis.sessionHandle` separate. The same account may use separate chats on different client devices against the same enrolled agent. A new session keeps the Agent's configured default workspace unless the current user request explicitly asks to select, change or clear it; prior chats, remembered project paths, session labels/list results and inferred project identity are not authority for `workspace__set`. Use `workspace__get` / `workspace__set` only for an explicit current-request override. An explicit workingDirectory overrides one call only. Folder selection remains working context, not Full permission or an OS-access bypass.

**Execution limits** edits local budgets, default 5 for calls/processes/tasks. Save commits a revision and applies it without reconnecting; the UI distinguishes saved locally, applied locally and server acknowledgement pending/received. Positive concurrency values are not silently clamped. Queue capacity/timeout are separate bounded advanced fields. High values show a resource warning. Lowering limits does not kill work. The server's HTTP anti-abuse rate limit is separate.

**Sessions** shows metadata, current workspace/revision, active/queued calls, process/task counts, resource ownership and unread events. Filter by label, folder or session ID; Show closed sessions exposes retained history. Stop work cancels the selected open session's accepted work and owned transient resources while leaving it resumable. Delete session is confirmed, removes retained metadata/mailbox rows, records a non-resurrectable tombstone and synchronizes permanent handle revocation to a capable server. Owned tabs are cleaned up where the browser is reachable. A cleanup warning means tabs may remain open, not that other sessions were stopped. Global Pause remains the distinct all-session control.

Read an existing file in the current chat before editing or replacing it. FILE_READ_REQUIRED or FILE_CHANGED requires a fresh Read and reconciliation, not retrying the mutation blindly. Keep separate Git worktrees for independent code changes. Shell commands have unknown effects and remain conservatively serialized against conflicting resource use; increasing the call budget does not remove this coordination or the one-desktop-input rule.

### Upgrade / recovery

Publish the matching server and agent, update/reload the packaged Jarvis Browser extension, then refresh/reconnect the MCP client's tool catalog. A supported application-session agent does not silently fall back to the old account-wide session. Existing old agents retain their negotiated legacy path during a staged server upgrade. Preserve the server data directory, local session database and local settings; do not copy credentials into release packages. Protected handles have no fixed wall-clock expiry; terminal close or synchronized permanent deletion makes a handle unusable. No live restart is inferred from source commit or publish.

Sessions can read their mailbox on the next call; this does not wake an idle ChatGPT/Claude conversation. Shared browser login cookies, external file writers and actual concurrent use of the physical keyboard remain outside chat-state isolation.

Open the full extracted tree in Visual Studio 2026; publish the CLI and desktop together using `scripts/Build.ps1`. The resulting agent is an interactive user process on Windows, not a Windows Service. No elevation is requested. Win10/11 behavior, multi-monitor scaling, UAC limitations, clipboard and hotkeys must be exercised using ACCEPTANCE.md before use.

## GUI

Paste the HTTPS origin (no `/mcp` path), enrolled device UUID and one-time enrollment token. The default workspace may be empty; each chat can choose its own existing folder later with `workspace__set`. The private profile is `%LOCALAPPDATA%\JarvisAgent\agent.local.json`; token is DPAPI protected for the current Windows user. Connect saves the profile but does not arm. Use Arm control to permit requests without an automatic time limit until you press Pause, Disconnect or Exit. Sensitive calls open an approval dialog showing the exact tool and arguments. Computer/browser native permissions may ask again. Deny is the default.

Minimize/close hides to system tray; closing the window is not the same as Exit. The tray menu offers restore, pause, and exit. `Ctrl+Alt+Pause` pauses when registration succeeds; another application may occupy this hotkey, so tray Pause remains available. No auto-start entry is installed. Temporary connection loss cancels actions and owned managed processes but preserves your explicit arm choice for reconnect in the same running process. Calls are never replayed. Manual Disconnect/Exit clears the grant; restarting the application always starts paused.

## CLI

`configure` prompts for server/device/workspace/token; `connect` requires local terminal confirmation and one-time action approval. Redirected stdin is rejected. Ctrl+C pauses and exits. A temporary reconnect retains the explicit arm choice from this CLI session; Ctrl+C clears it, and a new CLI process asks again. `list-tools` outputs the actual manifest, and must not be run alongside another host holding the same native bridge. Tool stdout/stderr never pollutes browser native-host stdout.

## Browser

Publish CLI and select `jarvis-agent.exe` from GUI Browser integration or run `jarvis-agent.exe browser-install`. Then load the copied unpacked extension directory shown by the host in Chrome/Edge/CocCoc extension developer mode. The server does not control this installation. The native manifest permits only the derived extension's fixed ID. Do not modify its public manifest key without updating the native-host allowed-origin ID as well. A Chrome extension public identity key is not an SSH/private signing key.

Agent native host: `com.jarvis.agent.browser`; named pipe: `JarvisAgent-browser`. Original Jarvis Code native integration remains separate. Browser installation requires HKCU write access and a browser permitting unpacked extensions. Enterprise browser policies may forbid it; those policies are not bypassed.

## Durable thread workflow - 1.0.58

Use `thread__create` for project-level work that must survive Agent restart, `thread__append_turn` for bounded turn/items/artifact references, and `thread__queue` for durable ordered follow-ups. `thread__fork` records lineage at a specific parent event, while `thread__checkpoint` compaction/rollback changes the default projection without deleting append-only audit events. Use `thread__search` only inside selected project roots and `thread__get(includeCompacted=true)` when an audit needs pre-compaction history.

## Safe code composition - 1.0.60

Prefer deterministic `tool_program__run` when JSON instructions are sufficient. Use `tool_script__run` only for bounded JavaScript control flow; it has no enabled Node/CLR/filesystem/network/process host API and can cause effects only through `await invokeTool(toolId, JSON.stringify(args))`. Every nested tool independently re-enters the normal local guard path. A denied/failed nested call fails the complete script and cannot be converted into a successful result by catching it in JavaScript. Both code modes are composite sensitive tools and cannot recursively invoke either code mode.

## Typical build/test workflow

First use filesystem tools to inspect `README`, build files and tests. Use `process__spawn` with argv for exact locally approved build/test execution; retain `process__start` only when shell syntax is actually required. Poll `process__read` with its jobId and cursor; do not launch repeated duplicate builds. Interactive jobs may use `process__write_stdin` and Windows ConPTY jobs may use `process__resize_pty`. Stop with `process__cancel`. Non-zero exit codes and truncated logs are explicit. Long-lived jobs are not durable across agent exit/disconnect and do not promise continued work while ChatGPT is idle.

Baseline computer tools support visual inspection/clicks of IDE/debugger if locally granted. That is not the same as a tested programmatic breakpoint API. Shell/Bash requires the corresponding installed interpreter; Windows PowerShell is used by managed jobs. Install project SDKs/dependencies separately, respecting the project's offline/network policies.

## Visual Studio build repair - 1.0.19

Keep all projects in `jarvis-agent/Jarvis Agent.slnx` or `Jarvis.slnx` loaded, including Vendor.
The original missing Agent Windows ref DLL was accompanied by NU1012 vendor restore errors;
fix the full dependency restore/build rather than copying a DLL into obj/ref manually.
Agent Windows uses framework ProtectedData; vendor Host retains its independent package.
Use `scripts/Verify-AgentReferences.ps1 -Configuration Debug` after building to check graph
closure, canonical NuGet frameworks and versioned reference DLLs. `Verify-AgentOutput.ps1`
checks that unused standalone JarvisCode.App host files remain excluded, not tool DLLs.
See BUILD-STATUS.md for test scope and the independent Visual Studio license limitation.

## Multiple project folders and branding - 1.0.23

Use the folder picker or **Add folders** to select several directories with Ctrl/Shift. The first folder is the primary working directory when none is set. Existing primary folders are retained when adding more folders. Select an additional folder and choose **Make primary** to swap it with the primary; **Remove** removes only the selected additional folder. Folder paths are normalized and duplicates are removed before saving.

**Save & connect** stores the primary `workspace` and the `additionalDirectories` array in the existing DPAPI-token profile. Old profiles without the new field still load. Folder changes apply on the next connection; they do not change an already-running tool's context. Missing folders must be removed or corrected before reconnecting. CLI `configure` also prompts for additional folders until an empty input is entered.

File/Git/browser adapters receive all explicitly selected directories. Relative paths resolve against the primary directory, while files in another selected directory should be addressed with an absolute path. The existing file guard still rejects unselected directories and links/junctions. Managed `process__start` accepts an optional `workingDirectory`; PowerShell/Bash/desktop/browser execution is not an operating-system project sandbox.

The new PNG is the supplied 9,942-byte Jarvis MCP icon. A derived multi-size ICO is embedded in the executable and used for the window and tray; the sidebar and local dialogs use the same branding.

### Unfinished requested features

Workspace-guard removal and native/full-permission UI integration were blocked by the editing platform before the submitted changes executed. This release does **not** add a Full permission tab, and sensitive actions still ask for local approval. It must not be represented as completion of all four requested changes. The partially integrated permission implementation was removed so the delivered source remains consistent.
