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

## 1.0.77 multi-session actions

The **Sessions** list now has a checkbox on every open session so several chats can be selected at once. **Select all** checks every currently visible open session (respecting the search filter); **Clear** resets the bulk selection. Closed sessions remain visible when **Show closed sessions** is enabled but cannot be checked for mutation.

Use **Stop selected** to cancel accepted work/resources only for the checked sessions while leaving their handles resumable. Use **Close selected** for terminal closure of all checked session handles; one confirmation covers the whole batch. Filtering a checked session out of the visible list clears its bulk selection so a hidden row is never mutated accidentally. The existing single-row **Stop work** / **Close session** controls remain available, and global **Pause** is still the separate all-session stop.

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

`session__stop_work` cancels only the explicit session's accepted work/resources and leaves the session resumable. Destructive `session__close` is no longer advertised in the normal MCP tool list; the Agent Sessions UI/operator path may still close a session explicitly. Sessionless process jobs, read-before-write file observations, computer state and browser/tool adapters are scoped by authenticated owner + enrolled device rather than by a model-carried secret. Explicit sessions retain strict `js_...` ownership boundaries.

Connection status now exposes reason-coded states (`CONNECTING`, `RECONNECTING`, `CONNECTED_HEALTHY`, `HEARTBEAT_STALE`, `TRANSPORT_INTERRUPTED`, `DISCONNECTED`). Gateway rejections before agent dispatch are audited by error code without recording tool arguments or session handles. A missing/invalid handle must not make ordinary tools unavailable; it affects only explicit session/workspace operations.

## Multi-chat operation in 1.0.64

The default workspace is optional. Connection center can clear it; Save defaults updates only newly opened sessions, not active chats or already accepted requests. Relative file paths and shell/git calls require a session workspace or explicit call `workingDirectory`. Absolute file paths work without a default. Project journal tools require a selected session project; a missing scope returns WORKSPACE_REQUIRED. CLI visual output with no workspace goes to the agent's dedicated local visual-artifacts area, never an accidental executable directory.

In each independent chat, call `session__open` and keep its returned `_jarvis.sessionHandle` separate. The same account may use separate chats on different client devices against the same enrolled agent. Use `workspace__get` / `workspace__set` to select, change or clear that chat's folder. An explicit workingDirectory overrides one call only. Folder selection remains working context, not Full permission or an OS-access bypass.

**Execution limits** edits local budgets, default 5 for calls/processes/tasks. Save commits a revision and applies it without reconnecting; the UI distinguishes saved locally, applied locally and server acknowledgement pending/received. Positive concurrency values are not silently clamped. Queue capacity/timeout are separate bounded advanced fields. High values show a resource warning. Lowering limits does not kill work. The server's HTTP anti-abuse rate limit is separate.

**Sessions** shows metadata, current workspace/revision, active/queued calls, process/task counts, resource ownership and unread events. Filter by label, folder or session ID; Show closed sessions exposes retained history. Stop work cancels the selected session's accepted work and owned tasks/processes, leaving the session available for future requests. Close session is confirmed and terminal for its handle; owned tabs are cleaned up where the browser is reachable. A cleanup warning means tabs may remain open, not that other sessions were stopped. Global Pause remains the distinct all-session control.

Read an existing file in the current chat before editing or replacing it. FILE_READ_REQUIRED or FILE_CHANGED requires a fresh Read and reconciliation, not retrying the mutation blindly. Keep separate Git worktrees for independent code changes. Shell commands have unknown effects and remain conservatively serialized against conflicting resource use; increasing the call budget does not remove this coordination or the one-desktop-input rule.

### Upgrade / recovery

Publish the matching server and agent, update/reload Jarvis Browser **1.2.0**, then refresh/reconnect the MCP client's tool catalog. The new agent rejects a browser extension lacking application-session support. A supported application-session agent does not silently fall back to the old account-wide session. Existing old agents retain their negotiated legacy path during a staged server upgrade. Preserve the server data directory, local session database and local settings; do not copy credentials into release packages. Handles expire after 30 days; open a new session when required. No live restart is inferred from source commit or publish.

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
