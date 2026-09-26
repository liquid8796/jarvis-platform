# Jarvis Control - 1.0.96

## Live Tool permissions catalog (1.0.96)

The Jarvis Agent Desktop **Tool permissions** tab now uses the same complete descriptor inventory as the runtime manifest. The offline bootstrap includes Artifact Runtime plus thread/automation/asynchronous-input capabilities, so the local permission count no longer stops at the older 83-tool snapshot while the MCP server shows 102 tools. Descriptor-only projections avoid starting automation pumps or opening a second mutable runtime merely to render the settings UI.

After connect, Tool permissions subscribes to the Agent's live `DynamicToolRegistry`. Plugin installs/removals and any future catalog replacement update both the overview count and permission rows on the WPF dispatcher. Unsaved choices are preserved for retained canonical IDs, newly appearing tools start from active local policy, and removed capabilities are immediately removed from active standing consent. Package **1.0.96**, assembly/file **1.0.96.0**.

## Thread, automation and asynchronous input (1.0.95)

Jarvis Agent now extends the durable thread runtime with `thread__list` and cursor-based `thread__wait`, plus session-owned automation and asynchronous-input tools. Automations are deterministic local schedules: when due, they append one bounded item to the existing thread queue and journal an `automation.fired` event. They do **not** invoke a model, execute an arbitrary tool, or create an invisible cloud job. One-time schedules complete after firing; interval schedules coalesce missed occurrences and advance the next due time. A local Agent pump catches up while the Agent is running, and `automation_run_due` provides an explicit bounded catch-up path.

`async_input_request` creates a durable structured question attached to a thread. A later caller can list, wait for, answer, cancel, or expire it; answers are validated locally against the supplied JSON Schema and guarded by optimistic revision numbers. Both wait tools are composite/read-only waits so they do not occupy a normal execution slot while polling. Automation and input ownership is bound to authenticated owner, enrolled device, and explicit Jarvis session; thread access remains constrained to selected workspace projects. Session close cancels active schedules and pending inputs owned by that session. See [Thread, automation and asynchronous input](docs/THREAD-AUTOMATION-ASYNC-INPUT.md).

## Artifact Runtime (1.0.94)

Jarvis Agent now publishes durable `artifact_create`, `artifact_update`, `artifact_get`, `artifact_list`, `artifact_show`, and `artifact_delete` tools for explicit Jarvis sessions. HTML, Markdown, SVG, JSON, and plain-text documents are stored in a local SQLite/WAL runtime keyed by authenticated owner, enrolled device, and session. Each artifact has an opaque ID, revision, SHA-256 digest, event history, and a stable `jarvis-artifact://...` URI. Updates and deletes require the caller's expected revision, so concurrent edits fail with a conflict instead of silently overwriting newer content.

Rendering remains local. HTML is normalized with a network-denying content-security policy, `<base>` and refresh redirects are removed, Markdown/text are escaped, SVG is wrapped as a data image, and JSON is validated and pretty-printed. `artifact_show` can display the current revision through the existing local artifact host and also returns a widget to the caller. Content, metadata, artifact count, and list size are bounded; soft deletion removes stored content from normal reads. See [Artifact Runtime](docs/ARTIFACT-RUNTIME.md).

## Collaboration Workers (1.0.93)

Jarvis Agent now publishes five explicit-session collaboration tools: `worker_spawn`, `worker_send`, `worker_wait`, `worker_list`, and `worker_stop`. A worker is a bounded concurrent JavaScript actor—not an undisclosed model call—that can receive mailbox messages, emit incremental text/tables, and invoke current non-composite Agent tools through the same guarded dispatcher. Multiple workers can run concurrently for independent inspection or verification work, while each worker remains isolated to the authenticated owner, enrolled device, and Jarvis session that created it.

Workers receive no Node, CLR, direct filesystem, process, environment, or network APIs. Nested calls retain schema validation, Arm/Pause, local permission and approval, execution-resource scheduling, workspace/application boundaries, cancellation, and audit behavior. Mailboxes, output, images, execution time, memory, statements, worker count, and nested calls are bounded; Pause, permission revocation, session stop/close, explicit stop, or Agent shutdown cancels owned workers. See [Collaboration Workers](docs/COLLABORATION-WORKERS.md).

## Computer Use Observation V2 (1.0.92)

`computer_use` now binds every desktop input to one exact `get_window_state` observation. A state response carries an opaque `observation_id`, generation, observed window bounds and, when requested, a `screenshot_id`. Element actions must present the current observation; coordinate, scroll and drag actions must also present the matching screenshot. A new observation, changed window bounds, expiration or the first action attempt invalidates the old IDs, so stale pixels and accessibility indexes cannot be replayed.

`get_window_state` can independently request screenshot and accessibility text, returns focused/selected/document context, and uses an occlusion-capable `PrintWindow` capture before falling back to desktop copy. Input actions support `return_state: accessibility | screenshot | full` to return a fresh post-action observation in the same call. See [Computer Use Observation V2](docs/COMPUTER-USE-OBSERVATION-V2.md).

## Session Tool REPL (1.0.91)

Jarvis Agent now publishes four session-bound orchestration tools: `exec` (`tool_repl.exec`), `wait` (`tool_repl.wait`), `sleep` (`tool_repl.sleep`) and `repl_reset` (`tool_repl.reset`). `exec` runs bounded JavaScript in a persistent Jint runtime scoped to one authenticated owner/device/Jarvis session. Successful cells retain `globalThis` state, rebuild a dynamic `tools` object from the current Agent catalog, support `Promise.all` parallel requests, and can emit text, JSON tables, images and widgets. Long-running cells return a `cell_id`; `wait` streams unread output and can terminate the cell.

The REPL has no direct Node, CLR, filesystem, process or network globals. Every `tools.<publicName>(args)` call re-enters the ordinary Agent dispatcher and keeps schema validation, Arm/Pause, local permissions, approval, resource scheduling, workspace and application restrictions, owner/device/session isolation, cancellation and audit behavior. Composite/session-control recursion is rejected, failures cannot be swallowed to report success, state is discarded after a failed/cancelled cell, and Pause, permission revocation, session stop or `repl_reset` cancels owned work. See [Session Tool REPL](docs/SESSION-TOOL-REPL.md).

## Automatic Tool Catalog cleanup and policy filters (1.0.90)

The MCP server now reconciles its persisted Tool Catalog with all enrolled Agent manifests whenever the server starts, an Agent connects or changes its catalog, a device/user is deleted, or an administrator selects **Sync catalog**. A policy row is retained only while at least one enrolled device still advertises that canonical capability. Removed capabilities, duplicate policy rows, and explicitly retired legacy IDs are deleted from the database rather than lingering as stale cards.

The Tool Catalog toolbar includes an **Availability** filter with **All**, **Auto**, **Hidden**, and **Published** modes. Text search, shown counts, select-all, and bulk policy actions share the same filtered scope. The server also centrally retires the old mutation/shell/process/public-computer IDs replaced by `apply_patch`, `exec_command`, `write_stdin`, and `computer_use`, so an older connected Agent cannot reintroduce those records.
## Codex-compatible agent tool surface (1.0.89)

Jarvis Agent now advertises the same compact public primitives used by the installed Codex Desktop instead of the former category-specific mutation fleet:

| Capability | New public tool | Replaces the former public tools |
|---|---|---|
| Source changes | `apply_patch` (`source.apply_patch`) | `filesystem.Write`, `filesystem.Edit`, `filesystem.NotebookEdit` |
| Local image inspection | `view_image` (`image.view_image`) | fragmented local screenshot/image-path workflows |
| Command execution | `exec_command` (`unified_exec.exec_command`) | `shell.PowerShell`, `shell.Bash`, `process.start`, `process.spawn` |
| Interactive process I/O | `write_stdin` (`unified_exec.write_stdin`) | `process.read`, `process.write_stdin`, `process.resize_pty`, `process.cancel` |
| Desktop control | `computer_use` (`computer_use.computer_use`) | the former public `computer.*` tool fleet |

`computer_use` supports `list_windows`, `get_window`, `list_apps`, `launch_app`, `get_window_state`, `click`, `press_key`, `type_text`, `scroll`, `set_value`, `drag`, `perform_secondary_action`, and `activate_window`.

The replacement is surface-level, not a security bypass. Workspace containment, optimistic file checks, owner/session process isolation, Arm/Pause, application grants, Full permission, approvals, resource locks, and task deadlines remain enforced locally. Empty `write_stdin` input only polls an owned session; text input and Ctrl+C still require mutation authority. Legacy saved permission IDs are migrated to the corresponding new capability IDs when loaded.

## Live MCP tool availability (1.0.88)

The selected Agent device's current capability manifest is now the runtime source of truth for MCP discovery. New capabilities default to **Auto** and become usable without an admin import/enable step; **Published** keeps an explicit public name/description, while **Hidden** is the explicit deny state. Stateful initialize-based MCP clients advertise `tools.listChanged=true` and receive `notifications/tools/list_changed` after Agent catalog or admin-policy changes.

Every MCP session also receives permanent `jarvis__tool_search` and `jarvis__tool_call` tools. They resolve the selected device's live canonical tool IDs and schemas at call time, so a chat whose direct tool list is stale can still discover and invoke a tool added after the chat started. This fallback does not grant authority: Agent Arm/Pause, exact schema validation, local permission/approval, resource scheduling, session ownership and catalog-generation checks remain unchanged. Database schema v2 is an additive migration that retains the legacy `Enabled` column as a compatibility mirror. Package **1.0.88**, assembly/file **1.0.88.0**.

## ChatGPT Web ImageGen (1.0.87)

Four session-owned `image_gen.*` tools create/edit images through the user's explicitly selected **existing Chrome/Edge profile** and Jarvis Agent Browser **1.4.0**. No embedded browser, cookie import, API key, Codex runtime or paid API fallback is used. Durable jobs preserve uncertainty instead of replaying prompts; original images and separate bounded PNG previews are stored locally. Choose the exact extension instance in **ImageGen** settings or the extension popup. Normal Arm/Pause and tool approvals remain unchanged. See [ImageGen setup and recovery](docs/IMAGEGEN.md) and BUILD-STATUS for executed verification and live-browser limitations. Package **1.0.87**, assembly/file **1.0.87.0**.

## Blender MCP integration (1.0.86)

The Agent adds six `blender.*` bridge tools using the same real Python/stdio MCP pipeline as the local Blender authoring workflow. Configure the existing Python environment with `scripts/Configure-BlenderMcp.ps1`; keep the addon listening on IPv4 loopback. Safe Mode remains enabled, telemetry disabled, and normal tool consent/Arm/Pause checks unchanged. Unity and Blender share tested transport plumbing but keep separate namespaces and connections. See [Blender setup](jarvis-agent/README.md#blender-mcp-bridge-1086). Package **1.0.86**, assembly/file **1.0.86.0**.

## Unity MCP integration (1.0.85)

Jarvis Agent now consumes the **Jarvis Agent** client configuration written by MCP for Unity to `%LOCALAPPDATA%\JarvisAgent\mcp.json`. Supports stdio and Streamable HTTP with six `unity.*` bridge tools, lazy per-session connections, bounded discovery/results, and cancellation on Pause/session stop. See [the Agent guide](jarvis-agent/README.md#unity-mcp-bridge-1085) for setup and limitations. Assembly/file version: **1.0.85.0**.

**Version 1.0.84 hardens per-chat default-workspace routing.** Newly opened explicit sessions keep the Agent's configured default workspace unless the current user request explicitly asks to select, change or clear it. Workspace tool guidance no longer treats prior chats, remembered project paths, session metadata or inferred project identity as authority for an override. See the [operator guide](docs/AGENT.md) for the session/workspace contract and [BUILD-STATUS.md](docs/BUILD-STATUS.md) for executed checks. Build/push does not itself update a running Agent or production MCP service.

A chat may call `session__open` when it needs explicit per-chat workspace/mailbox isolation, but ordinary filesystem/Git/shell/process/computer/browser/task calls no longer require `_jarvis.sessionHandle`. When no handle is present, the gateway creates an ephemeral call ID and the agent uses a stable owner/device isolation scope for stateful local resources. `session__stop_work` is resumable; destructive `session__close` is hidden from normal model discovery and retained for explicit operator/UI cleanup. Upgrade server and agent together and refresh cached MCP schemas; browser extension **1.3.0** adds protocol/capability negotiation required by the dedicated browser service.

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

Build thành công sẽ tạo `artifacts/agent/1.0.81/desktop/Jarvis.Agent.Desktop.exe`, `artifacts/agent/1.0.81/cli/jarvis-agent.exe`, ZIP agent và tar.gz self-contained server. Mỗi output Agent còn có `jarvis-browser-service.exe` ở root để dùng chung runtime/dependencies với Agent, còn native-messaging host nằm tại `browser/jarvis-browser-host.exe`; giữ cả thư mục output, không copy riêng executable. Từ 1.0.75, `dotnet build` trực tiếp `Jarvis.Agent.Desktop` hoặc `Jarvis.Agent.Cli` cũng tạo đúng layout browser companion này, không chỉ `scripts/Build.ps1`. WebView2 Runtime vẫn là điều kiện riêng cho visualize WPF.

## Chạy local

```powershell
.\scripts\Run-Server.Dev.ps1 -AdminEmail 'admin@example.test'
```

Script hỏi mật khẩu an toàn, không có admin mặc định. Mở `http://localhost:18765`. Development dùng ephemeral OAuth keys: restart sẽ làm mất hiệu lực các grant cũ. Không expose chế độ này ra Internet.

Đăng nhập admin → user/device/tools được quản lý trên web. User mới đăng ký ở trạng thái **pending**; admin phải approve trước khi đăng nhập. Trong **Devices**, enroll thiết bị và lưu token một lần. Mở Jarvis Agent, nhập URL/device ID/token, chọn workspace, bật tùy chọn HTTP loopback **chỉ khi test local**, rồi Connect. Kết nối thành công chưa cho phép điều khiển: cần **Arm control** (until Pause, Disconnect or Exit) tại máy. Pause qua GUI/tray hoặc `Ctrl+Alt+Pause`.

Trong **Tool permissions**, Full permission vẫn áp dụng cho các tool thông thường. Riêng `process.start` và `process.spawn` là constrained exceptions: hộp thoại local có `Deny`, `Approve once` và `Always approve`. `Always approve` được lưu vĩnh viễn theo exact tool ID trên máy đó và có thể thu hồi bằng **Require approval again**; Arm/Pause và các boundary Windows hiện có vẫn áp dụng.

Sau khi Agent gửi manifest, capability mới của **selected device** dùng chính manifest live và mặc định ở policy **Auto**: không cần Import/Enable để ChatGPT dùng. **Tool catalog → Import installed** chỉ còn là thao tác mirror metadata tùy chọn cho UI/admin. Admin có thể chuyển từng tool sang **Published** để giữ alias/description rõ ràng hoặc **Hidden** để chặn cả direct tool lẫn gateway generic. Catalog CRUD không upload script tùy ý để chạy trên máy người dùng.

CLI:

```powershell
.\artifacts\agent\1.0.81\cli\jarvis-agent.exe configure
.\artifacts\agent\1.0.81\cli\jarvis-agent.exe list-tools
.\artifacts\agent\1.0.81\cli\jarvis-agent.exe doctor --json
.\artifacts\agent\1.0.81\cli\jarvis-agent.exe connect
.\artifacts\agent\1.0.81\cli\jarvis-agent.exe browser-install
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
| `jarvis-agent/src/Jarvis.Agent.Windows` | Adapter tới tool baseline, browser proxy/service protocol, profile DPAPI |
| `jarvis-agent/src/Jarvis.Agent.BrowserService` | Dedicated browser runtime, family routing, session/tab ownership và isolated dev browser |
| `jarvis-agent/src/Jarvis.Agent.BrowserHost` | Minimal Chrome/Edge native-messaging relay process |
| `jarvis-agent/src/Jarvis.Agent.Desktop` | WPF MVVM, tray, local prompts, WebView2 artifacts |
| `jarvis-agent/src/Jarvis.Agent.Cli` | Interactive terminal host và browser-install command |
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

Current package **1.0.95**, assembly/file **1.0.95.0**. Historical release notes and commit messages remain in `CHANGELOG.md`; `scripts/Verify-CurrentVersion.py` checks current-version metadata for drift.

## Agent Harness (1.0.47)

Architecture:

Goal -> Planner -> Executor -> Tool Router -> MCP Tools -> Artifact -> Verification -> Recovery -> Memory

Core remains framework independent and integrations are provided by adapters.

Packaging supports win-x64 runtime assets for Agent delivery.

## Task Gateway (1.0.48)

The MCP server routes durable tasks to the local agent over the existing authenticated WebSocket. HTTP lifecycle APIs and six device-bound MCP task tools support goal creation, explicit plan submission, status, paged output artifacts, cancellation and installed-tool schema discovery. Goal-only NORMAL/READ_ONLY input still returns `NEEDS_PLAN`; goal-only `AUTONOMOUS` input can be planned locally only when the host explicitly injects an `IRemoteTaskAgenticCoordinator`, so no model provider or extra permission is silently assumed.

Agent execution reuses local permissions, waits for process exit codes, retains bounded output and stores snapshots outside the repository. Reconnect/restart does not replay uncertain actions. Both the server and agent need this release for task-v1; older agents still support ordinary tools. Detailed usage and boundaries: [Task Gateway](docs/AGENT-TASK-GATEWAY.md). Build with `.\scripts\Build.ps1 -Component All -ServerRuntime linux-arm64 -Offline` when dependencies and runtime packs are cached. Executed release evidence is recorded in [Build status](docs/BUILD-STATUS.md).

## Dynamic Tool Host (1.0.49)

The Agent now owns a `DynamicToolRegistry` instead of freezing the installed-tool dictionary inside `AgentConnection`. Each snapshot has a generation and canonical SHA-256 descriptor digest, with local JSON schemas compiled as part of the same immutable snapshot. Ordinary calls and task-plan validation both resolve that current snapshot; discovery still cannot grant permission or bypass local approval.

Wire calls may carry optional `threadId` and `turnId` correlation, and local runtime components can subscribe to interrupt/stop/subagent-stop lifecycle notifications for deterministic cleanup. Existing clients remain compatible because the new correlation fields are optional.

## Stateful Computer Use (1.0.50)

`computer.screenshot` and the new `computer.get_state` mint an opaque state ID scoped to the current remote session. `computer.computer_batch` requires that current ID, validates it before dispatch, removes Jarvis state metadata before the reused baseline tool sees the request, and invalidates the token once dispatch starts. Take a new screenshot/state observation before the next input batch.

`computer.get_state` reports bounded foreground-window/process metadata, the focused UI Automation element and up to 200 accessibility nodes. UI Automation failures yield a partial snapshot rather than an elevation attempt. Pause/disconnect-owned cleanup invalidates outstanding computer state; existing computer app grants and denied-app rules remain in force.

## Adaptive Harness (1.0.51)

Agent Core now includes `AdaptiveAgentExecutionLoop` with validated dependency DAGs, async execution/verification and bounded replacement of only the failed logical action. Verified predecessors are not replayed by the adaptive loop. `AgentConnection` can optionally inject an `IRemoteTaskAdaptiveCoordinator`; Task Gateway uses it only for `AUTONOMOUS` tasks and never after cancellation/timeout.

The default hosts still do not silently configure a paid/model planner; NORMAL/READ_ONLY plans retain the 1.0.48 deterministic semantics. Every adaptive or agentic replacement is revalidated against the installed local tool schema and then traverses the same Arm/Pause, exact-permission and approval path.

## Goal-Owned Coding Harness (1.0.67)

`IRemoteTaskAgenticCoordinator` extends the adaptive coordinator with initial planning and final goal verification. A goal-only `AUTONOMOUS` task is queued only when this coordinator is explicitly present; its generated plan is persisted before execution, must retain goal/mode/timeout/project, and is validated against normal stage/schema/tool limits. Successful tool exit codes no longer imply autonomous completion: the coordinator receives bounded execution evidence and must pass goal verification. It may request up to two bounded repair rounds, which are appended to the persisted plan and executed through the same guarded path.

`CodingPromptAssembler` provides deterministic prompt layers for injected coordinators: coding policy, rendered frontend/browser policy, resolved workspace, sorted tool capability metadata, optional skill instructions, execution outcomes and verification debt. Prompt material is bounded, vendor-neutral and never acts as a permission grant or a model-provider configuration.

## Rendered Frontend Verification (1.0.68)

Autonomous frontend completion now fails closed through `FrontendVerificationGate`. `FrontendChangeClassifier` recognizes common rendered source extensions (`.tsx`, `.jsx`, `.vue`, `.svelte`, `.css`, `.scss`, `.sass`, `.less`, `.html`) plus frontend/visual intent in the goal. Base rendered proof requires target identity, rendered DOM/accessibility state, no framework overlay, console health, a screenshot and a target interaction with post-state evidence. Visual/layout work additionally requires desktop and mobile viewport checks plus overflow/clipping evidence.

Evidence is typed, the latest evidence for a kind wins, and missing/failing kinds are projected back into the agentic prompt as `verification-debt` before another goal-repair round. `RemoteTaskSnapshot.verification` exposes the requirement type, pass state, retained evidence, missing kinds and failed kinds; the MCP output schema advertises this field as optional so deterministic/legacy task JSON remains compatible.

## Progressive Coding Skills and Browser State (1.0.69)

`PluginSkillLoader` turns validated plugin skill roots into bounded coding context without executing plugin entry files. Discovery reads only bounded front matter metadata, rejects reparse-point traversal and paths outside declared roots, isolates oversized/invalid skills as diagnostics, and exposes compact `plugin/name: description` metadata to agentic planning/verification. Full UTF-8 Markdown instructions are loaded only through explicit `LoadSelectedSkills(...)` selection and are revalidated against the current enabled plugin catalog at load time.

Each per-session browser suite now owns a `BrowserObservationTracker`. Element refs produced by `browser.read_page` or `browser.find` belong to the current observation generation. Material navigation, form input, clicks/typing/scrolling, JavaScript, uploads, tab/browser switches and viewport resize invalidate that generation. A later ref-bearing call must use a ref from a fresh observation; stale refs fail locally before the raw Chrome tool runs. Failed mutations do not invalidate the prior generation.

## Interactive Cancellation and Durable Session Handles (1.0.76)

Computer-use calls now use the same cancellation-bound call lease as browser calls. In particular, if `computer.request_access` or another desktop-scoped computer operation is cancelled while its local permission/grant UI is still unwinding, the cancelled call releases the shared `desktop` claim immediately instead of leaving later browser QA queued behind stale ownership. Deferred cancellation release still avoids re-entrant waiter pumping, and filesystem/shell/process retention semantics are unchanged.

Explicit `js_...` application-session handles are now durable correlation tokens for the lifetime of the underlying open Agent session rather than 30-day bearer-like leases. Every use still requires live OAuth, the same enrolled device, the same owner binding, and an open Agent session; explicit close remains terminal. Legacy v1 handles are accepted even after their historical expiry timestamp so an otherwise-open long-running session can resume instead of failing with `SESSION_EXPIRED`.

## Browser Companion Build Closure (1.0.75)

A normal `dotnet build` of `Jarvis.Agent.Desktop` or `Jarvis.Agent.Cli` now builds both dedicated browser projects as non-assembly project references, then copies the browser-service app files into the Agent output root and the native-messaging host app files under `browser/`. The build removes any transitive root-level `jarvis-browser-host.*` copies so the runtime layout matches browser registration expectations. The publish verifier rejects a misplaced root native host while still requiring `jarvis-browser-service.exe` and `browser/jarvis-browser-host.exe`.

The official `scripts/Build.ps1` flow still produces the self-contained win-x64 package and independently publishes the native host under `browser/`; this change specifically closes the ordinary project-build path that developers commonly copy/run directly.

## Cancelled Browser Resource Reclamation (1.0.74)

Browser resource leases now follow the cancellation lifetime of the call that acquired them. When a prior chat/session is stopped or its browser call is cancelled, the call-owned browser/desktop lease is released promptly even if the browser task has not finished unwinding. Because Chrome mutations claim both a session-scoped `browser|...` resource and shared `desktop`, this prevents an old Chrome call from blocking the first browser action in a newer session. The fail-safe is browser-only; filesystem, shell and process resource lifetimes are unchanged.

## Browser Native Host Reconnect Hardening (1.0.73)

The dedicated native-messaging relay now treats either side closing as terminal. In particular, if `jarvis-browser-service.exe` restarts while Chrome is otherwise idle, `jarvis-browser-host.exe` no longer remains blocked forever waiting for the next Chrome stdin frame. The host exits promptly, Chrome observes the native-port disconnect, and extension 1.3.0 follows its existing reconnect backoff to attach to the replacement service. This prevents a stale host from leaving `browser.list_connected_browsers` empty after an Agent/browser-service restart.

## Browser Runtime Isolation and Deterministic FE Verification (1.0.71)

Browser automation is now a process boundary rather than an in-process `BrowserBridge`. Existing `browser.*` MCP IDs remain stable in `ToolInventory`, but their Windows adapters proxy versioned requests to `jarvis-browser-service.exe`; browser-native stdio is handled only by the small `jarvis-browser-host.exe`. The service owns browser connections, application-session/tab state, observation state and per-family execution. Published Desktop and CLI packages keep the service in the publish root so it shares the validated Agent runtime/dependencies, while the independently self-contained native host lives under `browser/`. Desktop Browser integration now registers that packaged host automatically; dev outputs without it prompt specifically for `jarvis-browser-host.exe`.

`browserFamily` accepts `auto`, `dev`, `chrome`, `edge` or `extension`. `auto` routes loopback URLs to `dev`, which starts Chrome/Edge with a Jarvis-owned isolated profile and the packaged extension, while non-loopback calls select a ready external browser. The extension 1.3.0 handshake advertises browser/native-host protocol versions, capabilities and a persistent extension instance ID; incompatible extensions stay non-ready instead of receiving calls.

For `AUTONOMOUS` frontend work, completion no longer depends on the model remembering to test. Core locates the rendered loopback target and gathers target identity, rendered DOM, framework-overlay health, console health, screenshot and post-interaction state; visual work also checks 1440×900, 390×844, horizontal overflow and structural visual fidelity. The normal `FrontendVerificationGate` runs even when no `IRemoteTaskAgenticCoordinator` is configured. Missing browser/target/evidence fails closed. Explicit Figma/pixel-perfect/reference-image goals still require semantic comparison evidence rather than being auto-passed by the structural baseline.

## Visual Fidelity and Coding Quality Benchmarks (1.0.70)

Visual/layout tasks now require `FrontendEvidenceKind.VisualFidelity` in addition to desktop/mobile/overflow proof. `VisualFidelityLedger` is immutable and bounded to 128 entries across layout, typography, color, iconography, overflow and interaction-state categories. Blocking mismatches remain visible until explicitly resolved; the goal verifier can return the ledger directly and the host converts it into typed evidence so unresolved mismatches enter normal verification debt and block `COMPLETED`.

`CodingHarnessScenarios.All` defines six stable engineering fixtures: modal repair, responsive clipping, API error state, stale loading state, visual regression and backend regression. Each run records `EngineeringEvidence` for build/tests/browser/console/interaction/visual proof, corrective loops and evidence count. `HarnessEvaluator.EvaluateEngineering(...)` reports success and per-evidence pass rates plus average repair/evidence cost while the original throughput/authorization metrics remain unchanged.

## Restricted Tool Code Mode and Plugins (1.0.52)

`tool_program.run` executes only a small JSON instruction language (`call`, `set`, `if`, bounded `forEach`, `assert`, `return`). It has no `eval`, reflection, direct filesystem/network API or shell primitive. Each nested `call` is sent back through `AgentConnection`'s guarded invoker and is independently checked for local installation/schema/Arm/Pause/permission/approval. The outer composite tool is still mutating/sensitive for task-policy purposes and recursive invocation is rejected.

The plugin SDK is metadata-first: local `*.plugin.json` manifests declare an ID/version/minimum agent version, a relative entry file plus SHA-256 pin, tool IDs, permissions/skills and supported lifecycle hook names. Jarvis validates these fields and binds declared tools only to implementations already supplied by the local host; it does not load or download code from a manifest. `ApplyPluginCatalog` hot-projects that validated set into the dynamic registry without replacing non-plugin tools.

## Delegation and Durable Memory (1.0.53)

`agent_task_create` accepts optional `parentTaskId`. The local Agent verifies that the parent exists for the same owner/device, forces the child onto the same resolved project, prevents execution-mode broadening, caps lineage depth at 3 and direct children at 8, then persists `parentTaskId`, `rootTaskId` and `depth` in the normal durable task snapshot. Top-level create digests are unchanged, so pre-1.0.53 idempotent retries remain compatible.

`SqliteMemoryStore` is now genuinely durable. It stores memories in SQLite partitions keyed by owner/project/namespace, retains provenance and timestamps, supports TTL expiration and bounded text search, and keeps the legacy `IAgentMemory.Save/Get(string)` facade mapped to a default partition. SQLite connections are short-lived/non-pooled and use WAL/busy-timeout semantics for concurrent writers.

## Safe Script Code Mode and Harness V2 (1.0.60)

`tool_script.run` is a fresh-engine Jint JavaScript sandbox for control-flow-heavy tool composition. It exposes standard ECMAScript plus one host capability, `invokeTool(toolId, argsJson)`. CLR interop is not enabled and Node/process/filesystem/network globals are not injected. Script size, statements, memory, wall time, nested tool calls, argument JSON and output are bounded; a failed or denied nested tool call fails the whole script even if script code tries to catch it. Every nested effect re-enters the same installed-schema, Arm/Pause, permission and local-approval path as `tool_program.run`.

`AdaptiveAgentExecutionLoop` can now run independent ready actions concurrently only when the live registry marks their tool read-only and non-sensitive, with a configurable cap of 1..8. Mutating/sensitive actions and repair attempts remain serialized. `AgentCoreHostTools` is the single descriptor source used by the live connection, CLI `list-tools` and Desktop permissions surface, preventing host-tool catalog drift.

Harness V2 expands the deterministic offline evaluation to production runtime bootstrap, Process V2, durable threads, plugin lifecycle, protocol/doctor, Safe Script, read-only DAG parallelism and existing no-replay/state/permission scenarios. Its machine-readable schema includes targeted-test throughput and p95 scenario duration; `HarnessEvaluator` also accepts bounded real tool-latency samples and reports p50/p95/max without granting authority.

## Managed Plugin + Hook Runtime (1.0.59)

Plugin manifests remain metadata-only: Jarvis never loads or downloads the pinned entry file as arbitrary executable code. Tools and lifecycle hooks must be implementations already supplied by the local host. Manifests now support minimum/maximum Agent compatibility, validated relative skill roots, bounded MCP dependency declarations and provenance metadata in addition to the existing SHA-256 entry pin.

`PluginRuntimeBootstrap` watches the local plugin directory with debounced reload, validates a complete next catalog before publishing it and retains the previous snapshot when validation fails. Live changes flow through the dynamic catalog protocol. `interrupt`, `stop` and `subagentStop` hooks bind to explicit host implementations and failures are isolated per plugin. Local package install/update writes entry+manifest atomically with rollback on validation failure; uninstall removes only the manifest and leaves payload cleanup explicit. Doctor reports only manifest/tool/skill/MCP counts and status, not local skill paths or provenance contents.

## Durable Thread Runtime (1.0.58)

The production Agent now persists a profile-local `thread-runtime.db` and publishes seven `thread.*` tools for project-scoped orchestration. A thread records append-only events for turns, typed items, artifact references, metadata, queue mutations and fork lineage; queue rows are materialized only for efficient reorder/start operations.

`thread.checkpoint` compact/rollback operations are deliberately non-destructive. Compaction stores a summary plus an event watermark used by the default timeline projection, and rollback records a target event without deleting later audit history. `thread.get` can request compacted history explicitly; `thread.search` is bounded and restricted to selected project roots. The CLI/Desktop tool catalog and production runtime use the same store/tool descriptors.

## Process Runtime V2 (1.0.57)

`process.spawn` starts an owned executable from an argv array without inserting a shell. It supports a bounded working directory, up to 128 environment overrides, timeout, optional Windows ConPTY dimensions and returns the same opaque owned `jobId` used by `process.read/cancel`. `process.write_stdin` streams bounded UTF-8 input and `process.resize_pty` resizes only a running owned ConPTY session.

`process.read` remains cursor-based and backward compatible through its combined `output` field, and now also returns bounded structured `events` tagged `stdout`, `stderr`, `pty` or `system`. Windows PTY creation starts the child suspended, attaches it to the Agent's kill-on-close Job Object, then resumes it; Pause, permission revocation, disconnect and Agent disposal stop owned activity rather than attaching to unrelated machine processes. Task Gateway treats both `process.start` and `process.spawn` as long-running owned jobs and waits for a final exit code.

## Capability Protocol V2 and Doctor (1.0.56)

The JSON wire envelope remains version 1 for backward compatibility, while `AgentHello` and `welcome` now negotiate **protocol v2** capabilities. Current capability names cover Task Gateway v1, live catalog synchronization and scoped capability leases. A legacy peer that omits protocol metadata is normalized to protocol 1 and continues to use ordinary tool calls unchanged.

`jarvis-agent doctor --json` creates a read-only, redacted operational snapshot: package/assembly drift, negotiated protocol/capabilities, catalog generation/digest/tool count, plugin metadata health, permission-store health plus grant/lease counts, bounded task-snapshot integrity/count, owned-process count, and computer/browser readiness. It never serializes enrollment tokens, raw workspace/plugin/task paths, permission tool IDs, lease IDs/session IDs, tool arguments/results or plugin manifest content.

## Runtime Closure and Scoped Permissions (1.0.55)

Production `AgentRuntime` now constructs a single `DynamicToolRegistry`, injects a conservative default `IRemoteTaskAdaptiveCoordinator`, loads SHA-256-pinned local plugin manifests through `PluginRuntimeBootstrap`, and projects their locally supplied tools before connecting. This closes the 1.0.51/1.0.52 gap where adaptive/plugin engines existed in Core/tests but the shipping Windows composition root did not activate them.

Registry replacements now emit `catalog.changed`; the server validates and persists the new descriptor set, acknowledges it with `catalog.ack`, and pins later calls to the acknowledged catalog generation/digest. The Agent rejects calls carrying stale catalog identity before schema/tool execution. Discovery still does not grant permission.

`ToolPermissionPolicy` now supports bounded session/turn capability leases. `process.start` and structured `process.spawn` cannot use a legacy unconstrained saved Full Permission by themselves: invocation-time authority must match the session/turn, expiry, allowed workspace root and command prefix. Spawn prefixes are matched token-by-token against argv rather than reconstructed shell text. Lease expiry/revocation raises the existing cancellation signal so in-flight and queued guarded work is stopped without disarming the user's process-local Arm choice.

## Developer Tools and Evaluation (1.0.54)

Agent Core now publishes `developer.symbol_search` and `developer.test` alongside `tool_program.run`. Symbol search is read-only, workspace-scoped and bounded; test execution is intentionally marked mutating+sensitive because `dotnet test` may build/write project artifacts, so it still requires the ordinary local policy path. The DAP launcher is a Core contract rather than a remotely exposed attach/injection tool: it starts one validated adapter executable, owns that process and can stop only that owned process.

Run `powershell -ExecutionPolicy Bypass -File scripts/Run-HarnessEvaluation.ps1` to execute Harness V2. It runs named regression groups against the real Core/Windows/Server test projects, including shipping runtime bootstrap rather than Core seams alone, and writes schema-v2 JSON with targeted-test totals, throughput and p95 scenario duration to `artifacts/evaluation/harness-eval.json`. This benchmark is deterministic and requires no model/API credentials.
