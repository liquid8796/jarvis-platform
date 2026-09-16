# Jarvis Control - 1.0.55

**Version 1.0.55 closes production harness gaps: Windows runtime now boots the adaptive coordinator and pinned local plugin catalog, live tool-catalog changes are acknowledged and generation/digest-pinned end to end, and dangerous process standing consent is constrained by expiring session/turn capability leases.** See [BUILD-STATUS.md](docs/BUILD-STATUS.md) for executed test evidence. This package contains no production credentials.

Jarvis Control là control plane cho MCP; Jarvis Agent là ứng dụng C# .NET 10 trên Windows 10/11, gồm WPF desktop và CLI. Tên web được chọn vì yêu cầu ban đầu chưa điền tên. Một repository chứa hai project sản phẩm và shared protocol; giữ nguyên các thư mục `shared` và `vendor` khi mở solution con.

```text
ChatGPT / MCP client
  │ HTTPS · MCP Streamable HTTP · OAuth + PKCE S256
  ▼
jarvis-mcp-server  ── Jarvis Control web UI
  │ WSS · outbound connection initiated by the agent
  ▼
Jarvis Agent · Windows user session
  ├─ Local control lease / explicit consent / emergency pause
  ├─ Baseline computer-use, browser, visualize, filesystem, Git
  └─ Owned process jobs for build/test with bounded logs
```

## Mở source

Mở `Jarvis.slnx` bằng Visual Studio 2026 với .NET 10 SDK và workload **.NET desktop development**, **ASP.NET and web development**. Solution con: `jarvis-agent/Jarvis Agent.slnx`, `jarvis-mcp-server/jarvis-mcp-server.slnx`. Windows target là x64, không phải Windows Service; desktop phải có session tương tác của người dùng.

```powershell
# Chạy kiểm thử C# và xuất bản server + agent trên máy Windows có SDK/dependencies.
.\scripts\Build.ps1 -Component All -ServerRuntime linux-arm64

# OCI x86_64 dùng linux-x64; xác định kiến trúc VM trước, không đoán.
.\scripts\Build.ps1 -Component Server -ServerRuntime linux-x64

# Chỉ GUI + CLI.
.\scripts\Build.ps1 -Component Agent
```

Script sẽ restore package theo các version đã pin nếu chưa có cache. `-NoRestore` chỉ dùng sau một lần restore phù hợp cùng RID. Gói không chứa NuGet cache; không có lệnh Maven. `-SkipTests` tồn tại để điều tra lỗi build nhưng **không** dùng làm bằng chứng kiểm thử. Chưa có lockfile transitive được tạo bởi SDK.

Build thành công sẽ tạo `artifacts/agent/1.0.55/desktop/Jarvis.Agent.Desktop.exe`, `artifacts/agent/1.0.55/cli/jarvis-agent.exe`, ZIP agent và tar.gz self-contained server. Giữ cả thư mục publish, không copy riêng executable. WebView2 Runtime là điều kiện riêng cho visualize WPF. Browser integration cần CLI executable đã publish, kể cả khi dùng giao diện desktop.

## Chạy local

```powershell
.\scripts\Run-Server.Dev.ps1 -AdminEmail 'admin@example.test'
```

Script hỏi mật khẩu an toàn, không có admin mặc định. Mở `http://localhost:18765`. Development dùng ephemeral OAuth keys: restart sẽ làm mất hiệu lực các grant cũ. Không expose chế độ này ra Internet.

Đăng nhập admin → user/device/tools được quản lý trên web. User mới đăng ký ở trạng thái **pending**; admin phải approve trước khi đăng nhập. Trong **Devices**, enroll thiết bị và lưu token một lần. Mở Jarvis Agent, nhập URL/device ID/token, chọn workspace, bật tùy chọn HTTP loopback **chỉ khi test local**, rồi Connect. Kết nối thành công chưa cho phép điều khiển: cần **Arm control** (until Pause, Disconnect or Exit) tại máy. Pause qua GUI/tray hoặc `Ctrl+Alt+Pause`.

Sau khi agent gửi manifest, admin vào **Tool catalog → Import installed**, kiểm tra rồi bật tool cần thiết. Tool mới import mặc định tắt. Catalog CRUD quản lý alias/metadata/availability của tool đã cài trên agent; **không upload script tùy ý để chạy trên máy người dùng**.

CLI:

```powershell
.\artifacts\agent\1.0.55\cli\jarvis-agent.exe configure
.\artifacts\agent\1.0.55\cli\jarvis-agent.exe list-tools
.\artifacts\agent\1.0.55\cli\jarvis-agent.exe connect
.\artifacts\agent\1.0.55\cli\jarvis-agent.exe browser-install
```

`configure` hỏi token trên stdin ẩn; không nhận token qua URL/command line. `connect` cần terminal tương tác và xác nhận local. Không tự khởi động cùng Windows, không tự arm sau reconnect, không yêu cầu admin. GUI và CLI dùng một single-instance mutex theo Windows user.

## Kết nối ChatGPT

Production endpoint: `https://<your-domain>/mcp`. Đăng ký kết nối MCP với OAuth trong client được cấp quyền. Cấu hình **đúng callback URL mà client cung cấp** vào `Jarvis:OAuthRedirectUris`; không dùng wildcard. Discovery/DCR/code+PKCE/refresh được triển khai bằng OpenIddict. Consent chọn một device thuộc user hiện tại. Gói này chưa được thử trong một phiên ChatGPT thực.

Hướng dẫn chi tiết: [OAuth & MCP](docs/OAUTH-MCP.md), [agent](docs/AGENT.md), [API](docs/API.md), [OCI deployment](docs/DEPLOYMENT-OCI.md).

## Cấu trúc và tài liệu

| Thư mục | Trách nhiệm |
|---|---|
| `jarvis-mcp-server/src/Jarvis.McpServer` | ASP.NET Core, Identity, OAuth, MCP adapter, routing, CRUD, web assets |
| `jarvis-agent/src/Jarvis.Agent.Core` | Connection lifecycle, policy, process jobs; không phụ thuộc WPF |
| `jarvis-agent/src/Jarvis.Agent.Windows` | Adapter tới tool baseline, browser native bridge, profile DPAPI |
| `jarvis-agent/src/Jarvis.Agent.Desktop` | WPF MVVM, tray, local prompts, WebView2 artifacts |
| `jarvis-agent/src/Jarvis.Agent.Cli` | Interactive terminal host và browser native messaging host |
| `shared/Jarvis.Protocol` | Envelope, bounded transport, schema validation, workspace guard |
| `vendor/jarvis-code` | Source baseline được cung cấp, dùng qua ProjectReference |
| `tests`, `.github/workflows` | C# test source/CI và kiểm thử UI với API giả lập |
| `deploy`, `scripts` | Build, private configuration, read-only preflight, isolated deployment |

Đọc [architecture](docs/ARCHITECTURE.md), [security boundaries](docs/SECURITY.md), [tool parity](docs/TOOL-PARITY.md), [test status](docs/BUILD-STATUS.md), [manual acceptance](docs/ACCEPTANCE.md), [changelog](CHANGELOG.md).

## UI preview

The web screenshot uses real web assets with synthetic API fixtures. The Agent screenshot is a real WPF render of the published 1.0.23 assembly using synthetic UI values; the smoke test did not save a profile, connect or arm remote control.

![Jarvis Control dashboard](docs/screenshots/web-dashboard.png)
![Jarvis Agent 1.0.23](docs/screenshots/agent-1.0.23.png)

## Giới hạn quan trọng

Đây là deployment **một server instance**, broker WebSocket trong RAM + SQLite WAL. Không tuyên bố HA, exactly-once, resumable jobs qua restart, production-ready hay nhanh nhất trong benchmark. Call mất kết nối có thể đã tạo side effect; không tự replay. Một call ngắn tối đa 120 giây mặc định; build/test dài dùng `process__start/read/cancel`. Debug qua command/log/UI đã có đường triển khai; **chưa có DAP/breakpoint API chuyên dụng**.

Source tool computer/browser/visualize được giữ lại; host áp dụng giới hạn mới để dùng từ xa. HTML visualize chạy local không network, không `sendPrompt`, không inline MCP Apps trong ChatGPT. 114 optional third-party font binaries không được phân phối lại; system font dùng cho UI. Tham khảo manifest parity thay vì coi ZIP là bản sao bit-for-bit của toàn bộ archive đầu vào.

## Export và commit

```powershell
python .\scripts\Export-Source.py
```

Current package **1.0.55**, assembly/file **1.0.55.0**. Historical release notes and commit messages remain in `CHANGELOG.md`; `scripts/Verify-CurrentVersion.py` checks current-version metadata for drift.

## Agent Harness (1.0.47)

Architecture:

Goal -> Planner -> Executor -> Tool Router -> MCP Tools -> Artifact -> Verification -> Recovery -> Memory

Core remains framework independent and integrations are provided by adapters.

Packaging supports win-x64 runtime assets for Agent delivery.

## Task Gateway (1.0.48)

The MCP server now routes durable client-planned tasks to the local agent over the existing authenticated WebSocket. HTTP lifecycle APIs and six device-bound MCP task tools support goal creation, explicit plan submission, status, paged output artifacts, cancellation and installed-tool schema discovery. Goal-only input returns `NEEDS_PLAN`: no model planner is silently assumed.

Agent execution reuses local permissions, waits for process exit codes, retains bounded output and stores snapshots outside the repository. Reconnect/restart does not replay uncertain actions. Both the server and agent need this release for task-v1; older agents still support ordinary tools. Detailed usage and boundaries: [Task Gateway](docs/AGENT-TASK-GATEWAY.md). Build with `.\scripts\Build.ps1 -Component All -ServerRuntime linux-arm64 -Offline` when dependencies and runtime packs are cached. Executed release evidence is recorded in [Build status](docs/BUILD-STATUS.md).

## Dynamic Tool Host (1.0.49)

The Agent now owns a `DynamicToolRegistry` instead of freezing the installed-tool dictionary inside `AgentConnection`. Each snapshot has a generation and canonical SHA-256 descriptor digest, with local JSON schemas compiled as part of the same immutable snapshot. Ordinary calls and task-plan validation both resolve that current snapshot; discovery still cannot grant permission or bypass local approval.

Wire calls may carry optional `threadId` and `turnId` correlation, and local runtime components can subscribe to interrupt/stop/subagent-stop lifecycle notifications for deterministic cleanup. Existing clients remain compatible because the new correlation fields are optional.

## Stateful Computer Use (1.0.50)

`computer.screenshot` and the new `computer.get_state` mint an opaque state ID scoped to the current remote session. `computer.computer_batch` requires that current ID, validates it before dispatch, removes Jarvis state metadata before the reused baseline tool sees the request, and invalidates the token once dispatch starts. Take a new screenshot/state observation before the next input batch.

`computer.get_state` reports bounded foreground-window/process metadata, the focused UI Automation element and up to 200 accessibility nodes. UI Automation failures yield a partial snapshot rather than an elevation attempt. Pause/disconnect-owned cleanup invalidates outstanding computer state; existing computer app grants and denied-app rules remain in force.

## Adaptive Harness (1.0.51)

Agent Core now includes `AdaptiveAgentExecutionLoop` with validated dependency DAGs, async execution/verification and bounded replacement of only the failed logical action. Verified predecessors are not replayed by the adaptive loop. `AgentConnection` can optionally inject an `IRemoteTaskAdaptiveCoordinator`; Task Gateway uses it only for `AUTONOMOUS` tasks and never after cancellation/timeout.

The default hosts do not configure a model planner, so goal-only tasks still return `NEEDS_PLAN` and NORMAL/READ_ONLY plans retain the 1.0.48 deterministic semantics. Every adaptive replacement is revalidated against the installed local tool schema and then traverses the same Arm/Pause, exact-permission and approval path.

## Restricted Tool Code Mode and Plugins (1.0.52)

`tool_program.run` executes only a small JSON instruction language (`call`, `set`, `if`, bounded `forEach`, `assert`, `return`). It has no `eval`, reflection, direct filesystem/network API or shell primitive. Each nested `call` is sent back through `AgentConnection`'s guarded invoker and is independently checked for local installation/schema/Arm/Pause/permission/approval. The outer composite tool is still mutating/sensitive for task-policy purposes and recursive invocation is rejected.

The plugin SDK is metadata-first: local `*.plugin.json` manifests declare an ID/version/minimum agent version, a relative entry file plus SHA-256 pin, tool IDs, permissions/skills and supported lifecycle hook names. Jarvis validates these fields and binds declared tools only to implementations already supplied by the local host; it does not load or download code from a manifest. `ApplyPluginCatalog` hot-projects that validated set into the dynamic registry without replacing non-plugin tools.

## Delegation and Durable Memory (1.0.53)

`agent_task_create` accepts optional `parentTaskId`. The local Agent verifies that the parent exists for the same owner/device, forces the child onto the same resolved project, prevents execution-mode broadening, caps lineage depth at 3 and direct children at 8, then persists `parentTaskId`, `rootTaskId` and `depth` in the normal durable task snapshot. Top-level create digests are unchanged, so pre-1.0.53 idempotent retries remain compatible.

`SqliteMemoryStore` is now genuinely durable. It stores memories in SQLite partitions keyed by owner/project/namespace, retains provenance and timestamps, supports TTL expiration and bounded text search, and keeps the legacy `IAgentMemory.Save/Get(string)` facade mapped to a default partition. SQLite connections are short-lived/non-pooled and use WAL/busy-timeout semantics for concurrent writers.

## Runtime Closure and Scoped Permissions (1.0.55)

Production `AgentRuntime` now constructs a single `DynamicToolRegistry`, injects a conservative default `IRemoteTaskAdaptiveCoordinator`, loads SHA-256-pinned local plugin manifests through `PluginRuntimeBootstrap`, and projects their locally supplied tools before connecting. This closes the 1.0.51/1.0.52 gap where adaptive/plugin engines existed in Core/tests but the shipping Windows composition root did not activate them.

Registry replacements now emit `catalog.changed`; the server validates and persists the new descriptor set, acknowledges it with `catalog.ack`, and pins later calls to the acknowledged catalog generation/digest. The Agent rejects calls carrying stale catalog identity before schema/tool execution. Discovery still does not grant permission.

`ToolPermissionPolicy` now supports bounded session/turn capability leases. `process.start` (and structured `process.spawn` in the next release) cannot use a legacy unconstrained saved Full Permission by itself: invocation-time authority must match the session/turn, expiry, allowed workspace root and command prefix. Lease expiry/revocation raises the existing cancellation signal so in-flight and queued guarded work is stopped without disarming the user's process-local Arm choice.

## Developer Tools and Evaluation (1.0.54)

Agent Core now publishes `developer.symbol_search` and `developer.test` alongside `tool_program.run`. Symbol search is read-only, workspace-scoped and bounded; test execution is intentionally marked mutating+sensitive because `dotnet test` may build/write project artifacts, so it still requires the ordinary local policy path. The DAP launcher is a Core contract rather than a remotely exposed attach/injection tool: it starts one validated adapter executable, owns that process and can stop only that owned process.

Run `powershell -ExecutionPolicy Bypass -File scripts/Run-HarnessEvaluation.ps1` to execute the offline harness evaluation. It runs named regression groups against the real Core/Windows/Server test projects and writes machine-readable JSON to `artifacts/evaluation/harness-eval.json`. This benchmark is deterministic and requires no model/API credentials.

