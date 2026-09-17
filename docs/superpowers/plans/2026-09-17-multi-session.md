# Multi-session execution implementation plan

> For agentic workers: execute with superpowers:executing-plans, test-driven-development and verification-before-completion. The user approved the complete design in the conversation on 2026-09-17 and requested UI UX Pro Max for the interface. Work is authorized on master; preserve unrelated work and never force-push.

**Goal:** Let independent chats on the same account, from any client device, share one OAuth-bound Jarvis Agent without sharing execution context, workspace, owned processes or browser tabs.

**Architecture:** Keep stateless MCP and the existing authenticated WSS connection. A protected application-session handle binds account, agent and a random session ID. Store session metadata, workspace revisions and bounded mailboxes in a separate local store. A fair, dynamically configurable scheduler owns execution slots, with resource-specific exclusion and independent cancellation/status capacity.

**Tech stack:** .NET 10, C# 14, ASP.NET Core/MCP SDK, WPF MVVM, existing SQLite and browser extension JavaScript. No new permission authority, background model service or transcript sharing.

**Spec:** Approved architecture in this file (sections below) and the conversation dated 2026-09-17.

## Global constraints

- Default concurrent tool calls, process jobs and durable tasks: 5. Positive integer settings; bounded queue and queue timeout are distinct settings.
- Never change local Arm/Pause, exact tool approval, constrained-process approval or protected-agent-directory boundaries. A session handle is not OAuth or a permission grant.
- Agent workspace may be empty. Relative paths require an explicit call directory or session workspace; absolute paths keep existing filesystem permission checks.
- A session's initial workspace is copied from the optional agent default once. Later changes use an optimistic revision and affect future accepted calls only.
- Distinguish the client device from the enrolled execution device. Handles cannot route across OAuth-bound agents or accounts.
- Session metadata and mailbox messages are scoped to owner/agent; messages are untrusted coordination data, never user consent. Do not expose other sessions' handles or transcripts.
- Stop/close one session cancels its calls/tasks/owned process jobs and releases its resources, not the whole agent. Reconnect does not replay uncertain mutations.
- Browser tab groups and selected browsers are session-specific. Shared cookies are not advertised as profile isolation. Desktop input remains exclusive and requires fresh state after another session changes the desktop.
- UI uses the existing WPF theme, system fonts, semantic resources, visible labels/focus, inline validation, empty/error states and keyboard-accessible commands. No control of the live Agent permission UI is needed for validation; use synthetic smoke fixtures.
- Complete the release with existing Markdown documentation, package/assembly/file version bump, a conventional commit and normal push to origin/master.

## Approved application-session contract

Public tools: session__open/get/list/send_message/read_events/close and workspace__get/set. Other tools, including agent_task_*, carry a reserved `_jarvis.sessionHandle` envelope removed before installed-schema validation. session__open does not require a handle; passing an existing valid handle resumes that session. New sessions receive unique random IDs and protected, account/device-bound handles. Missing/invalid handles for a multi-session agent fail explicitly rather than falling back to shared OAuth session state. The agent validates local session ownership and closed status independently.

Session metadata contains identity, label, creation/last-active timestamps, workspace revision, running/queued counts and mailbox cursor information; it contains no handles. Optional parent identity is validated for the same owner/device. Mailbox reads are bounded, cursor-based and durable. Session closure is idempotent and closed sessions cannot be reopened accidentally.

Settings are locally saved and revisioned, advertised in AgentHello and synchronized using execution.settings.changed/ack. Reducing concurrency drains existing work without cancelling it or starting new work above the limit. Composite outer calls release execution slots before nested guarded calls. Status/cancel/session-close use a bounded control lane. Scheduler uses per-session round-robin queues, cancellation, queue capacity and queue deadlines.

## Implementation and verification tasks

### 1. Contract regression tests and baseline

Files: tests/Jarvis.Core.Tests/MultiSessionContractTests.cs, tests/Jarvis.Server.Tests/McpSessionTests.cs, tests/Jarvis.Agent.Windows.Tests/SessionIsolationTests.cs.

- [x] Record existing version, clean-tree status and existing test conventions.
- [x] Add and verify regression tests against existing entry points for optional workspace and published session/workspace tools. The resumed audit did not independently recreate the original feature RED run; fresh complete-suite GREEN evidence is recorded below.
- [x] Add ownership, handle tampering/account/device binding, workspace revision/snapshot, mailbox and session-close cases as the APIs become available.

Verification: focused dotnet test filters, then all three maintained test projects. A blocked baseline command is not test evidence; do not evade a tool denial.

### 2. Session identity and workspace state

Files: shared/Jarvis.Protocol/{Messages,AgentProtocolVersion,WorkspaceDirectories}.cs; new session contracts; jarvis-agent/src/Jarvis.Agent.Core/Sessions/*; AgentContracts.cs; AgentConnection.cs; AgentCoreHostTools.cs; jarvis-mcp-server/src/Jarvis.McpServer/Transport/McpGateway.cs; new Transport/McpSessionContext.cs; Program.cs.

- [x] Add explicit nullable/empty workspace semantics with actionable WORKSPACE_REQUIRED errors.
- [x] Add persistent session store and session/workspace tools, including revision-checked workspace changes and bounded mailbox events.
- [x] Add protected session handles and schema augmentation/stripping, route authenticated owner identity in the wire envelope.
- [x] Snapshot session context at call acceptance; validate local ownership and closed state before dispatch.
- [x] Include session descriptors in shipping discovery, not only a test registry.

### 3. Scheduler, resource ownership and task integration

Files: new Core/Execution/* and shared execution settings contracts; AgentConnection.cs; Core/ProcessTools.cs; Core/RemoteTasks/*; server Infrastructure/WsAgentRouter*.cs; Application/AgentTaskService.cs; Transport/AgentTaskMcpTools.cs; Windows/ToolInventory.cs, LegacyToolAdapter.cs, ComputerStateTracker.cs, StatefulComputerToolAdapter.cs; browser bridge and extension.

- [x] Implement dynamic fair concurrency, independent bounded control lane and atomic admission/queue accounting.
- [x] Advertise settings and acknowledge live changes without reconnect; remove fixed legacy 4/16 limits for negotiated multi-session execution.
- [x] Bind process/task ownership and cancellation to the creating session, preserving workspace snapshots and parent-task scope.
- [x] Use path/repository resource exclusion for known filesystem/Git mutations; conservatively serialize unknown shell effects.
- [x] Scope selected browser and tab groups to session; reject foreign tab IDs and cleanup only owned tabs.
- [x] Add desktop shared-generation invalidation in addition to per-session state.

Required regressions: 6 independent calls at limit 5; live decrease/increase; round-robin fairness; queue cancellation/full/timeout; composite at limit 1; no cross-session job/task/tab access; close A leaves B active; no replay after disconnect.

### 4. WPF Settings and Sessions

Files: Windows/AgentProfile.cs, AgentRuntime.cs; Desktop/ViewModels/MainViewModel.cs and new session/settings view-models as appropriate; Desktop/MainWindow.xaml and view-only code-behind; CLI/Program.cs; tests/Jarvis.Agent.UiSmoke/*.

- [x] Read original UI UX Pro Max skill, generate a developer-operations design system and query WPF guidance.
- [x] Add execution settings editor with labels, inline errors, save/apply feedback, actual acknowledged revision and advanced queue/job/task limits.
- [x] Support an empty default workspace and explicit clear action.
- [x] Add Sessions navigation with workspace, active/queued work, last activity, metadata-only detail and per-session stop/close.
- [x] Preserve theme/system fonts and accessibility; validate synthetic empty, populated, invalid-setting and narrow-window states without connecting or arming a runtime.

### 5. Release and evidence

Files: VERSION, Directory.Build.props, README.md, CHANGELOG.md, docs/{ARCHITECTURE,AGENT,API,SECURITY,ACCEPTANCE,BUILD-STATUS}.md, COMMIT_MESSAGE.txt and versioned build scripts/metadata identified by scripts/Verify-CurrentVersion.py.

- [x] Run focused tests, full maintained suites, build/publish validation and synthetic UI smoke; record exact successes/failures, not estimates.
- [x] Review code paths for ownership, cancellation races, settings revision drift and sensitive-data exposure.
- [x] Bump package/assembly/file versions consistently after the completed patch and update existing docs with migration/client-refresh instructions and limitations.
- [x] Verify the release diff and prepare `feat(session): add isolated multi-chat execution and session controls`. Publication is the final handoff: normal push to master, followed by a remote-ref comparison; it is not inferred from this document or a successful build.

## Initial evidence

- Repository: D:\Project\tools\Jarvis\jarvis-platform. Initial branch master tracks origin/master with a clean working tree.
- Initial source version: 1.0.63; assembly/file version 1.0.63.0.
- Live Agent-window control was denied by its own-app boundary; do not request it again or operate that UI through another path.
- The initial combined source-map/baseline-test shell request was blocked by the OpenAI execution layer; no baseline test pass is claimed. Subsequent independent source reads remain available.

## Earlier resumed implementation checkpoint (historical)

- Existing unfinished changes retained on master; no destructive reset or deployment.
- Baseline server regression: 99 passed. Core baseline: 188 passed, one fast-exit ConPTY output failure under investigation.
- Optional workspace adapter and cross-session desktop invalidation: 5 regression tests passed after fixes.
- Added and executed seven red/green extension isolation tests, including foreign-tab rejection, persistence, concurrent group creation and scoped close.
- Native bridge application-session envelopes/selection, per-session desktop services and runtime Settings load are being integrated. UI, final regressions, docs/version and push remain pending.

## Final release checkpoint - 1.0.64

- Completed session/workspace, dynamic scheduling, job/task/resource ownership, browser/desktop state and WPF Settings/Sessions implementation. All implementation checklist entries above now refer to this completed source patch, not a live deployment.
- Full fresh release verification exited 0 through `scripts/Verify-MultiSessionRelease.ps1`: build 0 errors / 39 vendored warnings; Core 203 + Windows 57 + Server 99 tests passed; browser 8, UI asset 4 and native-publish 5 tests passed.
- The interruption was at an incorrect root-level OpenConsole.exe packaging check. Architecture-specific host validation and published-library PTY smoke now pass for Desktop and CLI. The first new packaging test run was RED (5 failures), followed by GREEN (5 passes).
- Isolated WPF smoke reports 75 installed tools, zero binding errors, optional workspace, saved limits and metadata/filter/minimum-size states. It did not save a live profile, connect or arm control. UI UX Pro Max guidance and WPF-specific searches were read from the local reference checkout; those downloads are excluded from Git/packages.
- Product package/assembly/file versions are 1.0.64 / 1.0.64.0; the modified vendored bridge is 1.0.25 / 1.0.25.0 and the browser extension is 1.2.0. Publish archives, CRC checks, hashes and limitations are recorded in BUILD-STATUS.md.
- Source finalization changes after the full release run are documentation only. Live server/Agent restart, extension reload, real chat catalog refresh and physical UI/two-client-device acceptance remain separate. The conventional commit and its normal master push are verified from Git at handoff, not asserted in advance here.
