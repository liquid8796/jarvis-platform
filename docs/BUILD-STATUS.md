# Build / test status

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
