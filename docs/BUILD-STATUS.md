# Build / test status

## 1.0.58 Durable Thread Runtime - executed verification (2026-09-16)

- Added profile-local SQLite `thread-runtime.db` with append-only thread event journals, persisted turns/items/artifact references, goal/section metadata, fork lineage and materialized durable queue state.
- Added seven production `thread.*` tools for create/get/append-turn/queue/search/fork/checkpoint; compaction and rollback record checkpoint projections without deleting prior audit events, and all existing-thread operations re-check the persisted project against selected workspaces.
- RED/GREEN evidence: thread tests first failed because the Threads namespace/runtime did not exist; the first implementation then exposed a UNIQUE(position) reorder collision, fixed with a transactional temporary-position shift. Production bootstrap then failed until `AgentRuntime` published the same thread toolset/profile DB used by CLI/Desktop discovery.
- Targeted verification: thread runtime **3 passed, 0 failed**; Windows production bootstrap **1 passed, 0 failed**; CLI and Desktop explicit builds both succeeded with **0 warnings / 0 errors**.
- Final `dotnet test Jarvis.slnx --nologo --no-restore`: **243 passed, 0 failed** (Core 135, Windows 29, Server 79); Server integration completed in 1m46s.
- `python scripts/Verify-CurrentVersion.py`: passed for package 1.0.58 and assembly/file 1.0.58.0. No push, deployment or service restart was performed.

## 1.0.57 Process Runtime V2 - executed verification (2026-09-16)

- Added exact argv `process.spawn`, bounded environment overrides, structured stdout/stderr/pty/system cursor events, `process.write_stdin` and Windows `process.resize_pty` while retaining the legacy shell `process.start` contract.
- Windows ConPTY creation uses suspended process startup, kill-on-close Job Object attachment before resume, owned stdin/output handles and normal Pause/disconnect/permission-revocation cleanup; Task Gateway treats `process.spawn` as a managed long-running job and waits for exit status.
- RED/GREEN evidence: Process V2 tests first failed because only start/read/cancel existed; spawn capability-lease coverage then failed because prefix matching only understood shell `command`. Implementation added argv-token matching and the focused process/lease group passed **10/10**.
- Process targeted tests: **6 passed, 0 failed**; scoped capability lease tests: **4 passed, 0 failed**.
- Final `dotnet test Jarvis.slnx --nologo --no-restore`: **240 passed, 0 failed** (Core 132, Windows 29, Server 79). Existing vendor warnings remain; Server integration completed in 1m56s.
- `python scripts/Verify-CurrentVersion.py`: passed for package 1.0.57 and assembly/file 1.0.57.0. No push, deployment or service restart was performed.

## 1.0.56 Capability Protocol V2 / Doctor - executed verification (2026-09-16)

- Added protocol-v2 capability negotiation inside the backward-compatible wire-v1 envelope. New agents advertise Task Gateway/catalog-sync/capability-lease support; missing protocol metadata is normalized to legacy v1.
- Added read-only redacted `AgentDoctor` plus CLI `doctor --json`, covering version drift, protocol/capability state, catalog identity/tool count, plugin/permission/task health, process count and computer/browser readiness without returning credentials or raw local paths/IDs/tool payloads.
- RED/GREEN evidence: protocol integration first failed because `AgentHello`/welcome had no v2 fields/constants; doctor tests first failed because the Diagnostics namespace/contracts did not exist. After implementation, doctor tests verify redaction plus invalid-plugin reporting; the real CLI command was smoke-run against the local profile and emitted only the documented redacted fields.
- Targeted tests: doctor **2 passed, 0 failed**; protocol-v2 negotiation **1 passed, 0 failed**; the existing legacy AgentBridge tests remain part of the full suite.
- Final `dotnet test Jarvis.slnx --nologo`: **235 passed, 0 failed** (Core 127, Windows 29, Server 79).
- `python scripts/Verify-CurrentVersion.py`: passed for package 1.0.56 and assembly/file 1.0.56.0. `jarvis-agent doctor --json` exited 0 with package/assembly 1.0.56/1.0.56.0 and protocol 2. No push/deployment/restart was performed.

## 1.0.55 Runtime Closure / Scoped Permissions - executed verification (2026-09-16)

- Production `AgentRuntime` now boots the validated local plugin catalog and conservative adaptive coordinator; dynamic registry replacement is synchronized to the server with `catalog.changed`/`catalog.ack` and later calls are generation/digest-pinned.
- Added expiring session/turn capability leases with workspace/command-prefix constraints. `process.start` no longer treats a legacy arbitrary-process Full Permission grant as sufficient invocation authority; lease expiry/revocation cancels guarded work through the existing policy signal.
- RED/GREEN evidence: stale-catalog tests first failed because wire identity fields did not exist; capability tests first failed on the absent lease model/policy overloads; Windows production-bootstrap tests first failed because `AgentRuntime` did not accept/load plugins or inject adaptive coordination; server integration first failed because catalog changes were unexpected/unpinned, then exposed a generation-1 race until catalog ACK was introduced. The first full-suite rerun correctly exposed one robustness fixture still using the old unconstrained `process.start` grant; its setup was migrated to a scoped lease and the focused cancellation test passed.
- Targeted verification: Core runtime/catalog/lease/permission group **12 passed, 0 failed**; Windows production-bootstrap **1 passed, 0 failed**; server bridge **2 passed, 0 failed**; scoped-lease task-cancellation regression **1 passed, 0 failed**.
- Final `dotnet test Jarvis.slnx --nologo`: **232 passed, 0 failed** (Core 125, Windows 29, Server 78).
- `python scripts/Verify-CurrentVersion.py`: passed for package 1.0.55 and assembly/file 1.0.55.0. No push, package deployment, service restart or production permission mutation was performed.

## 1.0.54 Developer Tools / Harness Evaluation - executed verification (2026-09-16)

- Added workspace-bounded `developer.symbol_search`, sensitive fixed-shape `developer.test`, an owned DAP adapter session contract, reusable `HarnessEvaluator`, and `scripts/Run-HarnessEvaluation.ps1`. The two published developer tools are injected through the dynamic registry and remain under local schema/Arm/Pause/permission/approval policy.
- RED/GREEN evidence: developer-tool tests first failed because the DeveloperTools namespace/contracts did not exist; after implementation one parser boundary test exposed an unnecessary minimum output size and was fixed at the implementation boundary; publication coverage then failed until AgentConnection host bootstrap registered the developer tools. Evaluator tests verify metrics, duplicate-name rejection, cancellation and JSON output.
- `scripts/Run-HarnessEvaluation.ps1`: **8/8 scenario groups passed, 0 failed**, covering **98 targeted tests** (dynamic catalog 5, permission/pause 30, adaptive no-replay 6, tool code mode/plugins 8, delegation/SQLite memory 7, developer tools/evaluator 8, stale computer state 11, task transport/no-replay 23). JSON written to ignored `artifacts/evaluation/harness-eval.json`.
- Core suite: **120 passed, 0 failed**.
- `dotnet test Jarvis.slnx --nologo`: **225 passed, 0 failed** (Core 120, Windows 28, Server 77). Existing vendor warnings remain; no new NuGet audit warning was emitted.
- `python scripts/Verify-CurrentVersion.py`: passed for package 1.0.54 and assembly/file 1.0.54.0. No package publish, merge, push or production deployment was performed.

## 1.0.53 Delegation / Durable Memory - executed verification (2026-09-16)

- Added persisted task lineage, bounded same-scope child creation, fork/join polling, MCP/REST `parentTaskId`, and replaced the fake dictionary `SqliteMemoryStore` with partitioned durable SQLite memory.
- RED/GREEN evidence: memory tests first failed on the missing database constructor/partition/upsert/search contracts; initial SQLite run then exposed pooled file handles and a NuGet High advisory on `SQLitePCLRaw.lib.e_sqlite3` 2.1.11. The final implementation uses non-pooled connections and pins Core 10.0.11 / SQLitePCLRaw 2.1.12; the advisory no longer appears. Delegation tests first failed at the internal host boundary, then verified persisted lineage, scope narrowing, depth/count caps and join; MCP integration verifies `parentTaskId` end-to-end.
- Durable memory/delegation targeted group: **7 passed, 0 failed**; MCP parent-child integration: **1 passed, 0 failed**; Core suite: **112 passed, 0 failed**.
- `dotnet test Jarvis.slnx --nologo`: **217 passed, 0 failed** (Core 112, Windows 28, Server 77).
- `python scripts/Verify-CurrentVersion.py`: passed for package 1.0.53 and assembly/file 1.0.53.0. Existing vendor warnings remain; no package publish or production deployment was performed.

## 1.0.52 Restricted Tool Code Mode / Plugin SDK - executed verification (2026-09-16)

- Added bounded `tool_program.run`, composite nested-call scheduling, local hash-pinned plugin manifests/catalog validation and hot plugin projection into the dynamic registry.
- RED/GREEN evidence: tool-program tests first failed because the interpreter namespace did not exist; plugin tests first failed because the plugin catalog did not exist; connection projection tests failed until host registration and plugin projection APIs were wired. A compile rerun also identified/fixed one missing protocol import and two unused interpreter-state constructor parameters.
- Tool-program/plugin targeted group: **8 passed, 0 failed**. Core suite: **105 passed, 0 failed**.
- `dotnet test Jarvis.slnx --nologo`: **210 passed, 0 failed** (Core 105, Windows 28, Server 77).
- `python scripts/Verify-CurrentVersion.py`: passed for package 1.0.52 and assembly/file 1.0.52.0. Plugin manifests do not load/download arbitrary code; no production deployment was performed.

## 1.0.51 Adaptive Harness - executed verification (2026-09-16)

- Added validated adaptive dependency DAGs, async per-action execution/verification, bounded replacement of only the failed logical action, and optional `AUTONOMOUS` Task Gateway repair coordination.
- RED/GREEN evidence: adaptive harness tests initially failed on missing plan/executor/verifier/replanner contracts; gateway adaptive-rule tests initially failed until replacement ID/stage and mode/cancellation/budget guards existed.
- `dotnet test tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj --nologo`: **97 passed, 0 failed**.
- `dotnet test Jarvis.slnx --nologo`: **202 passed, 0 failed** (Core 97, Windows 28, Server 77).
- `python scripts/Verify-CurrentVersion.py`: passed for package 1.0.51 and assembly/file 1.0.51.0. Default composition still supplies no model planner/coordinator; no production deployment was performed.

## 1.0.50 Stateful Computer Use - executed verification (2026-09-16)

- Added session-scoped opaque computer state IDs/generations, stale/cross-session rejection, `computer.get_state`, bounded Windows UI Automation observation, and state-bound `computer.computer_batch`.
- RED/GREEN evidence: tracker tests first failed because state contracts did not exist; adapter tests first failed because the stateful wrapper/provider interface did not exist; state-tool tests first failed because `computer.get_state` did not exist; inventory integration then failed until the new tool and state-required batch schema were wired.
- Windows targeted suite: **28 passed, 0 failed** after adding 11 stateful-computer tests to the prior 17.
- `dotnet test Jarvis.slnx --nologo`: **196 passed, 0 failed** (Core 91, Windows 28, Server 77).
- `python scripts/Verify-CurrentVersion.py`: passed for package 1.0.50 and assembly/file 1.0.50.0. Existing vendor warnings remain unchanged; no package publish or production deployment was performed.

## 1.0.49 Dynamic Tool Host - executed verification (2026-09-16)

- Added `DynamicToolRegistry`, lifecycle fan-out and optional thread/turn correlation. Ordinary calls and Task Gateway validation resolve the same current immutable tool snapshot; registry replacement does not grant local permission.
- RED/GREEN evidence: dynamic-registry tests initially failed because `DynamicToolRegistry` did not exist; lifecycle tests failed because lifecycle/context fields did not exist; dynamic-connection tests failed until wire correlation, registry constructor/accessor and live schema/tool lookup were wired.
- `python scripts/Verify-CurrentVersion.py`: passed for package 1.0.49 and assembly/file 1.0.49.0 after detecting the pre-patch README 1.0.23 drift.
- `dotnet test Jarvis.slnx --nologo`: **185 passed, 0 failed** (Core 91, Windows 17, Server 77). Existing vendor nullable/unreachable-code warnings remain; no new test failures.
- This verification did not publish packages or deploy/restart a production server.

## 1.0.48 Task Gateway - executed verification (2026-09-16)

Approach A is implemented as a device-bound server gateway and an agent-owned client-plan executor. This section supersedes only the new task-release status; older release/deployment records below are retained. No production deployment or live Agent restart was performed.

### Commands executed

```powershell
.\scripts\Build.ps1 -Component All -ServerRuntime linux-arm64 -Offline
python .\scripts\Verify-TaskGatewayRelease.py
```

Full solution Release build completed with 0 errors / 0 warnings. The subsequent win-x64 vendor publish emitted 38 warning lines from unchanged `vendor/jarvis-code`; no new task source warning was reported. Cached dependency/runtime resolution used a local empty NuGet source plus the existing package cache, with NuGet audit disabled only for offline resolution. No package source or runtime version was silently downloaded.

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Jarvis.Core.Tests | 83 | 0 | 0 |
| Jarvis.Agent.Windows.Tests | 17 | 0 | 0 |
| Jarvis.Server.Tests | 77 | 0 | 0 |
| **Total** | **177** | **0** | **0** |

### Runtime evidence

- Real `AgentConnection` connected to the authenticated TestServer WebSocket, using `ProcessToolSet`, not a fake planner/executor success stub. A synthetic C# program was patched, restored from an empty feed/cache, built, executed with an assertion, packaged into ZIP, and the ZIP entry verified through five submitted task steps.
- OAuth DCR + PKCE token flow exercised all six custom MCP task operations, including plan execution, text artifact retrieval and cross-device rejection. Cookie APIs tested authentication/CSRF; per-step schema, read-only mode, disabled catalog tools, local pause/deny and standing-permission revocation were checked.
- Exit code failure prevents later stages. Tests covered idempotent create/plan, conflicting payloads, safe-read retry, pagination, retained-output assertions, timeout partial logs, task deadline classification, cancellation of owned jobs, transport reconnect without replay and interrupted snapshot recovery.
- Red/green logs exist for missing HTTP endpoints, missing MCP task discovery, stale health version, deadline/output handling, and a false success caused by matching discarded log output. They are diagnostic failure reproductions, not final-suite failures.
- Published CLI `help` returned `Jarvis Agent 1.0.48`. Published agent/core/protocol/server assembly and file versions were inspected as 1.0.48.0. Desktop UI and native Linux execution were not smoke-tested in this patch; Linux ARM64 ELF layout and package contents were validated.

### Release artifacts

| Artifact | Bytes | Archive file entries | SHA-256 |
|---|---:|---:|---|
| `Jarvis-Agent-1.0.48-win-x64.zip` | 176643683 | 2514 | `01a128d66918ce6de04703f0430bf37ebde9b5e37e980821f94f41db729933c8` |
| `jarvis-mcp-server-1.0.48-linux-arm64.tar.gz` | 51238505 | 407 | `15fce4dcb7af327bd5d55df160e39d4fe72472c5deb6bab702ab5fbff4db76b4` |

ZIP CRC and gzip end-of-stream integrity passed. Every archived file SHA-256 and member set matched its publish directory. Checked private-key/certificate/runtime-config/database/font filenames were absent, standalone vendor app entry points were excluded, and the required desktop/CLI/server entry points were present. This is a defined package policy check, not a claim that arbitrary tool logs can never contain sensitive information.

Full machine-readable evidence: `artifacts/verification/1.0.48/verification.json`; build/test/publish log: `artifacts/verification/1.0.48/build-test-package.log`; release TRX files: `artifacts/test-results/1.0.48/`. Each package has a `.sha256` companion.

### Review and boundaries

Focused inline review checked owner/device isolation, additive protocol compatibility, sharing the original local tool permission path, process completion rather than launch acknowledgement, bounded logs/limits, non-replay behavior and documentation preservation. No independent reviewer/subagent was available or claimed. README/API/Agent-Harness baseline bytes and changelog history were checked programmatically; no vendor source was changed.

Goal-only tasks are `NEEDS_PLAN`; the client supplies steps and any repair decisions. Persistence is atomic local JSON, not SQLite or semantic memory. Existing `Autonomous/` classes remain compatibility scaffolding. Catalog availability is checked at acceptance and local authorization at each step. Existing tasks are stopped explicitly by cancellation/pause/device revocation rather than reinterpreting a previously accepted plan. The gateway does not claim Codex parity, automatic code repair, multi-day autonomous inference, exactly-once side effects or live production activation.

Both server and agent must be upgraded for task-v1. Existing OAuth registration and enrollment secrets remain external and unchanged. The release binaries were produced and verified from this working tree before the final source/documentation commit; verification report hashes identify the exact delivered archives.

---

The updated uploaded source was built in this isolated validation tree. Core: 64 passed; Windows adapters: 17 passed; Server: 54 passed. Desktop/CLI win-x64 self-contained publish and isolated WPF permission-tab smoke passed (52 tools). All ZIP CRC checks and seven executable icon sizes passed. Full release evidence: artifacts/verification/release-1.0.24.json. The original project and live enrollment/permission settings were not modified. The complete source archive returned in the conversation contains the updated operator documentation and vendor modification ledger. Historical entries below describe older releases, not this build.

# Verification status - Jarvis 1.0.21 - 2026-09-15

## OAuth consent navigation repair

The loaded consent document previously inherited `form-action 'self'`. Production logs
showed repeated authorization responses without a following token exchange. A native
Chromium reproduction using rendered/sanitized consent HTML confirmed that the form POST
succeeded but the cross-origin callback redirect was blocked by CSP.

The default CSP remains unchanged. Only the authenticated consent document permits the
selected callback origin after exact configuration and registered-client validation.
CSRF, PKCE, device ownership, resource binding and callback allowlists remain enforced.
Existing browser documents retain their old policy: start a fresh Connect flow in ChatGPT.

## Executed checks

| Check | Actual result |
|---|---|
| Server tests | 39 passed, 0 failed |
| Core tests | 23 passed, 0 failed |
| Full solution Release build | Passed, 0 errors; 39 existing vendor warnings in full build |
| Server self-contained linux-arm64 publish | Passed |
| Chrome and Edge native browser checks | 4 per browser passed: reproduce old block, allow Authorize, allow Cancel, reject unapproved origin |
| Browser fixture scope | Actual sanitized consent HTML/header from ASP.NET tests; synthetic loopback callbacks, not a user ChatGPT session |
| Production HTTPS health | ok, version 1.0.21 |
| Production OAuth discovery | 4 public metadata routes passed |
| Service status after promotion | active, NRestarts=0 at verification |
| Deployed server DLL vs published DLL | SHA256 identical |
| Caddy configuration | SHA256 unchanged |

Release: `/opt/jarvis-mcp-server/releases/1.0.21-20260915051921`.
Existing private configuration, certificate files, database location and callback registrations
were not edited. No running local Agent was stopped or granted remote-control permission.

Evidence: `artifacts/verification/oauth-consent-navigation/` contains TRX files, browser reports,
publication/deployment logs and verification.json. Browser tests do not prove the user's final
ChatGPT account link: the user still needs a fresh Connect/consent after deployment.

Commit: `fix(oauth): allow validated consent callback navigation under CSP`.

---

## Historical verification from 1.0.20


### OAuth discovery fix

The live 1.0.18 service exposed /connect/register but omitted registration_endpoint from both
OAuth discovery documents and omitted none from token_endpoint_auth_methods_supported.
The screenshot separately shows ChatGPT rejecting an empty client ID in user-defined client mode.
Version 1.0.20 advertises the existing DCR/public-PKCE flow and removes the unimplemented openid
scope from discovery. Exact callback allowlists, consent, CSRF and MCP authorization remain intact.

### Executed checks for this patch

| Check | Result |
|---|---|
| Server integration tests | 24 passed, 0 failed; includes discovery-driven DCR/PKCE/token and authenticated MCP initialize/tools/list |
| Core tests | 23 passed, 0 failed |
| Complete root solution Release build | Passed, 0 errors; 39 existing vendor warnings |
| Server self-contained linux-arm64 publish | Passed |
| OCI promotion | Passed; release /opt/jarvis-mcp-server/releases/1.0.20-20260915040918 |
| Public HTTPS GET /health | status ok, version 1.0.20 |
| Both public authorization discovery documents | registration_endpoint present; none and S256 advertised; scopes offline_access and mcp:tools |
| Service after deployment | active; NRestarts=0 at verification |
| Caddy | active; Caddyfile SHA256 unchanged from preflight |

Caddyfile SHA256: 6fd78c789cb8bb042a1f622579236d60d9db7225525538f62b5699f879cb0e40.
The existing private configuration, PFX files, callback allowlist and database model were not edited.
Package/assembly/file version: 1.0.20 / 1.0.20.0. No Agent protocol/tool changes.

### Limits

The attempted full Debug build hit file locks held by the user's running Jarvis Agent (PID 1504).
The app was not terminated. Release compilation passed; no new Debug runtime acceptance is claimed.
A first public rejection probe returned HTTP 400 but the PowerShell helper failed reading its body.
The combined probe rerun was tool-blocked; production authentication probes were not retried.
The final helper is read-only GET discovery. Rejection/PKCE/token/MCP behavior is covered by isolated tests.
No real ChatGPT plugin link or production tool execution is claimed. The user must select DCR in
Advanced OAuth settings, approve the exact displayed Callback URL, and complete Jarvis consent.
No production client was registered and no credential/private config was extracted for this patch.

Evidence: artifacts/verification/oauth-discovery/ (TRX, build/deployment logs, public metadata snapshots).
Source backup: artifacts/backups/oauth-discovery-20260915T035607Z/.
Source and server artifacts are generated in artifacts/ with versioned filenames and SHA256 files.
See docs/OAUTH-MCP.md for the exact setup flow and COMMIT_MESSAGE.txt for the proposed commit.

---

### Historical verification: 1.0.19 (not rerun in full for this server-only fix)

# Verification status - Jarvis 1.0.19 - 2026-09-15

### Scope and diagnosis

Local build/reference-assembly repair in `D:\Project\tools\Jarvis\jarvis-platform`.
The initial full build failed with NU1012 in vendor App and Host restore assets.
`Jarvis.Agent.Windows` had intermediate/refint outputs but no completed ref/ or bin/ assembly;
its dependent CLI/Desktop consequently reported CS0006. The vendor dependencies were absent
from both the root and Agent solution files. No production service/configuration was changed.

### Patch

All four reused vendor projects are now solution members, retaining normal ProjectReferences.
Removed redundant ProtectedData PackageReference only from Agent Windows; vendor Host keeps it.
Package version: 1.0.19. Assembly/FileVersion: 1.0.19.0, including the reused vendor assemblies.
Standalone JarvisCode.App executable/deps/runtimeconfig exclusion is unchanged. Tool DLLs remain.
`Verify-AgentReferences.ps1` checks graph closure, restore errors/canonical TFMs and reference DLL versions.
The verifier supports canonical-key and targetAlias + framework NuGet asset layouts.

### Executed checks

| Check | Result |
|---|---|
| Offline restore from local NuGet cache | Passed; no new packages downloaded; vulnerability audit not performed |
| Agent solution Debug Rebuild, Visual Studio MSBuild | Passed |
| Complete root solution Debug Rebuild, Visual Studio MSBuild | Passed |
| Agent Release build | Passed, 0 errors; 39 existing vendor warnings in that full build |
| Core / Server tests | Passed: 23 / 18; 0 failed |
| Desktop and CLI win-x64 self-contained publish | Passed |
| Debug build after both RID publishes, without restore | Passed, 0 errors |
| Debug/Release graph + reference assembly checks | Passed |
| Standalone-host exclusion checks | Passed in Windows/Desktop/CLI build and Desktop/CLI publish outputs |
| Desktop startup / CLI help and manifest | Passed; desktop title Jarvis Agent, version 1.0.19.0; CLI exposes 49 tools |
| Negative verifier fixtures | Rejected missing solution dependency, NU1012 restore error, and unversioned Windows framework |

### Limits and evidence

`devenv.com /Rebuild` reported that the Visual Studio evaluation/license expired. IDE-driven
validation could not run; successful rebuilds above used the installed Visual Studio MSBuild engine.
An exploratory CLI run forcing BuildingInsideVisualStudio=true produced dependency-scheduling errors;
it is retained in diagnostics and is not counted as successful IDE validation. Standard full-solution
MSBuild rebuild and the subsequent publish-to-Debug cycle passed.
No app controls were armed and no remote computer action or production pairing was attempted.
Logs, binlogs, TRX and JSON evidence: `artifacts/verification/agent-reference-assembly/`.
Pre-patch source backup: `artifacts/backups/agent-reference-assembly-20260915T030923Z/`.
The deployed OCI release was not modified by this patch; previous 1.0.18 deployment evidence is historical.

### Recheck after reopening the solution

```powershell
Set-Location 'D:\Project\tools\Jarvis\jarvis-platform'
dotnet build 'jarvis-agent/Jarvis Agent.slnx' -c Debug --no-restore
.\scripts\Verify-AgentReferences.ps1 -Configuration Debug
```

Open the updated root or Agent solution with all Vendor projects loaded. Do not reference copied
bin/obj DLLs or disable reference-assembly generation to hide missing dependency builds.

## Agent 1.0.23 - executed verification (2026-09-15)

Scope delivered: multi-directory GUI/CLI workspaces and supplied icon integration. Workspace-guard removal and full-permission UI/native integration remain unimplemented because the editing platform blocked those calls. The unfinished permission implementation was removed; current file guards and sensitive-action approvals remain operational.

Executed on Windows using the installed .NET 10 SDK:

- Jarvis.Core.Tests: 36 passed, 0 failed, 0 skipped (10 new multi-directory regression tests).
- Jarvis.Server.Tests: 54 passed, 0 failed, 0 skipped.
- Desktop and CLI: Release, win-x64, self-contained publish succeeded. Both are packaged in artifacts/Jarvis-Agent-1.0.23-win-x64.zip. ZIP CRC and forbidden-file-extension checks passed for 2,554 entries.
- Linux ARM64 server cross-publish succeeded; this update did not deploy or execute the Linux package.
- WPF UI smoke succeeded against the published Desktop assembly (version 1.0.23.0): window rendered, icon loaded, Make primary swapped folders, Remove removed the selected folder. The fixture used synthetic server/device/folder values and never saved a profile, connected or armed control.
- All seven ICO sizes (16/24/32/48/64/128/256) were found embedded in the published EXE.
- Root and vendor package/assembly/file versions are 1.0.23 / 1.0.23.0.

Initial packaging exposed two existing script problems: dependence on an absent OS environment variable and a private-file pattern that misclassified required System.Private.* runtime DLLs. Both were corrected; the private configuration/font checks remain. RID-specific restore assets were regenerated from C:/Users/Liquid/.nuget/packages only; no external package source was used. Test suites were run explicitly before the final publish-only invocation.

Evidence: artifacts/verification/agent-build-1.0.23.log, agent-publish-final-1.0.23.log, ui-smoke-final-1.0.23.log; artifacts/verification/ui-1.0.23/ui-smoke.json; docs/agent-update-1.0.23-verification.json; docs/screenshots/agent-1.0.23.png. Existing vendor compiler warnings were not changed. Native folder-picker interaction and real remote/browser control were not exercised by the UI smoke fixture.

To repeat the smoke test after building tests/Jarvis.Agent.UiSmoke:

```powershell
dotnet tests/Jarvis.Agent.UiSmoke/bin/Release/net10.0-windows/Jarvis.Agent.UiSmoke.dll artifacts/agent/1.0.23/desktop artifacts/verification/ui-1.0.23
```
