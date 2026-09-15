# Changelog

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

