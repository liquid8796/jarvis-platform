# Jarvis Agent - 1.0.93

## Collaboration Workers (1.0.93)

Use `worker_spawn` inside an explicit Jarvis session to start a bounded concurrent JavaScript actor. The worker exposes `tools.<publicName>(args)`, `receive(timeoutMs)`, `text(value)`, `table(value)`, and `sleep(milliseconds)`. Continue with `worker_wait`, send later instructions or data with `worker_send`, inspect all session-owned workers with `worker_list`, and cancel a single worker with `worker_stop`.

Workers are deterministic local actors, not additional language-model sessions. They cannot access Node, CLR, raw filesystem/process/network/environment APIs, cannot recursively start composite/session tools, and cannot swallow a rejected nested tool call to report success. All nested effects re-enter normal Agent security, scheduling, ownership, and approval checks. Details and examples are in [Collaboration Workers](../docs/COLLABORATION-WORKERS.md).

## Computer Use Observation V2 (1.0.92)

Call `computer_use` with `action: "get_window_state"` before each desktop input. Keep the returned `observation_id`; coordinate, scroll and drag calls must also keep the matching `screenshot_id`. An observation is single-use for actions and becomes stale when a newer state is captured, the window moves/resizes, it expires, or an action begins. Use `return_state` on an action to receive the next observation without a separate round trip. Full details and examples are in [Computer Use Observation V2](../docs/COMPUTER-USE-OBSERVATION-V2.md).

## Session Tool REPL (1.0.91)

Use `exec` inside an explicit Jarvis session to compose the Agent's current tools with persistent JavaScript state. The runtime exposes `tools.<publicName>(args)`, `text(value)`, `table(value)`, `image(value)` and `sleep(milliseconds)`. Store cross-cell values on `globalThis`. If a cell remains active, continue with `wait`; use `repl_reset` to cancel cells and discard the session's REPL state. The REPL never receives Node, CLR, filesystem, process or network globals. Nested tool calls still pass all normal local security and ownership gates. See [the complete contract](../docs/SESSION-TOOL-REPL.md).

## ChatGPT Web ImageGen (1.0.87)

Four session-owned `image_gen.*` tools create/edit images through the user's explicitly selected **existing Chrome/Edge profile** and Jarvis Agent Browser **1.4.0**. No embedded browser, cookie import, API key, Codex runtime or paid API fallback is used. Durable jobs preserve uncertainty instead of replaying prompts; original images and separate bounded PNG previews are stored locally. Choose the exact extension instance in **ImageGen** settings or the extension popup. Normal Arm/Pause and tool approvals remain unchanged. See [ImageGen setup and recovery](../docs/IMAGEGEN.md) and BUILD-STATUS for executed verification and live-browser limitations. Package **1.0.87**, assembly/file **1.0.87.0**.

## Blender MCP bridge (1.0.86)

This integration follows the observed `racing-bois-1` workflow: Python `-m blender_mcp.server` speaks real MCP stdio (`initialize`, `tools/list`, `tools/call`) and the upstream server connects to the Blender addon over `127.0.0.1:9876`. It is not UI automation or direct headless export. The observed source was `ahujasid/mcp-for-blender` 2.0.0 at commit `6f992ffbca3cb715d111fc640b737b808632273c`, with the existing disabled-telemetry compatibility patch. No Codex account configuration, conversations, or credentials are imported into Jarvis.

From the repository root, configure a trusted, already-installed Blender MCP Python environment (substitute local paths):

```powershell
.\scripts\Configure-BlenderMcp.ps1 -PythonExe 'D:\Project\Unity\racing-bois\_local\blender-env\Scripts\python.exe'
.\scripts\Start-BlenderMcp.ps1 -BlenderExe 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' -AddonPath 'D:\Project\Unity\racing-bois\_local\blender-mcp\addon.py'
```

Configuration writes only `mcpServers.blender` in `%LOCALAPPDATA%\JarvisAgent\mcp.json`. The setup script validates the Python package, takes an exclusive `mcp.json.lock` sidecar lock for concurrent runs of this setup helper, checks the original bytes before replacement, backs up an existing config, and replaces it atomically; malformed/oversized/ambiguous JSON is preserved, not reset. Other MCP servers and metadata are retained. Do not run Unity's Configure action or edit mcp.json concurrently: those writers do not honor this helper's lock. It does not install packages, change tool permissions, start the Agent, or alter Blender preferences. The separate start script launches a fresh task-owned Blender with factory startup and automatic .blend script execution disabled; an occupied port is rejected rather than reused or killed. A different `-Port` must be supplied consistently to both scripts.

Restart the updated Agent, reconnect and import/enable the `blender` category in Jarvis Control. Agent IDs are `blender.list_tools`, `blender.call_tool`, `blender.list_resources`, `blender.read_resource`, `blender.list_prompts`, and `blender.get_prompt`. Their default public catalog names are `blender_list_tools`, `blender_call_tool`, and so on; the prefix prevents the gateway's globally unique name constraint from silently skipping them when Unity is already imported. Use `blender_list_tools` to discover exact downstream schemas, then `blender_call_tool` with a discovered name and arguments. The authoring tools include `get_addon_status`, `get_scene_info` (requires `user_prompt`), `get_object_info`, `execute_blender_code`, and `get_viewport_screenshot`. Images and structured results are forwarded. Resource/prompt list/read wrappers are also available when supported by the downstream server; reading a prompt does not execute it.

Blender is deliberately limited to the observed local Python/stdio launcher. The parser rejects remote hosts, shell/HTTP launchers and explicit unsafe-mode/telemetry-enabled settings, and writes safe defaults into the child environment even when the Agent inherited different values. Upstream Safe Mode is defense in depth, **not an operating-system sandbox**; Blender Python and asset operations remain sensitive actions. No raw socket execution fallback is provided.

Connections are lazy and per Agent session, and all Blender bridge requests within this Agent are serialized. This **does not isolate Blender scenes**: all sessions using the same addon/port share its scene, and other clients such as Codex are outside this Agent's queue. Inspect before changing, save valuable work first, and use separately configured addon ports/Agent profiles for independent scenes. Pause/stop closes owned MCP clients, not the Blender application; already-running Blender Python may continue and cannot be rolled back by cancellation. Failed/uncertain modifying calls are never automatically retried. Batch long authoring work into smaller requests (stdio tool-call deadline: 120 seconds).

The MCP gateway wire protocol is unchanged; the existing dynamic catalog can import these new descriptors. Building does not replace a running Agent or grant/enable new tools. Deployment and verified test results are recorded in [BUILD-STATUS.md](../docs/BUILD-STATUS.md).


## Unity MCP bridge (1.0.85)

The Agent reads only `%LOCALAPPDATA%\JarvisAgent\mcp.json` (`mcpServers.unityMCP`), not a repository MCP file and not the separate JarvisCode profile. Install MCP for Unity 10.2.1-beta.7+, open **Window > MCP for Unity**, select **Jarvis Agent**, and click **Configure** (or select it in the first-run wizard). Keep the Unity MCP session active; HTTP also requires the shared HTTP server. Remote endpoints require HTTPS. Prefer HTTP for multiple assistants: Unity's legacy stdio/TCP connection is single-agent even though Agent-side sessions are isolated.

Restart the updated Agent and reconnect. Import/enable its `unity` tool category in Jarvis Control if the server has not yet imported these descriptors. `unity.list_tools` returns the actual downstream schemas with paging/filtering. Use `unity.call_tool` with a discovered name and object arguments. `unity.list_resources`, `unity.read_resource`, `unity.list_prompts`, and `unity.get_prompt` expose the remaining surfaces. Reading a prompt does not execute its instructions.

All six wrappers retain normal sensitive-tool approval and Arm/Pause checks, including discovery because opening stdio can launch a process. Configuration does not grant permissions or change enrollment. Connections open only on an approved tool call, are reused per session, and close on Pause/stop. The next call reloads local config; invalid/disabled config closes the corresponding session connection. Transport failures are not retried, so verify Unity state before manually repeating a modifying call. Discovery/results and config sizes are bounded; Agent plugin skills are not installed by Unity's client-skill button.

Native Windows executable launch preserves literal `>=` package requirements rather than passing them through shell redirection. The platform package is **1.0.85** and assembly/file version **1.0.85.0**. The release history below describes earlier changes.


### Verification for 1.0.85

Release solution compilation succeeds. The Core suite passes 257/257 tests and the Windows Agent suite passes 107/107, including a real native-process regression for literal version requirements and shell metacharacters. The Unity configurator passes 17/17 EditMode tests; the bridge passes stdio/HTTP fixtures and a live read-only Editor query. Desktop and CLI self-contained win-x64 packages both pass `Verify-AgentOutput.ps1`, and `Verify-CurrentVersion.py` confirms 1.0.85 / 1.0.85.0. The full Server test run was interrupted while still running and is not claimed as passing. Release packaging was completed separately with `Build.ps1 -Component Agent -SkipTests` after the verified Agent/Core runs.

The running Agent is not hot-patched by Unity configuration. Start the newly built 1.0.85 Agent to load the bridge; existing Arm/Pause, enrollment, and catalog/approval controls remain unchanged.

Open `Jarvis Agent.slnx` with the entire repository present. Keep its Vendor projects loaded:
those assemblies implement reused tools, while the standalone Jarvis Code application is excluded
from Agent build/publish output. Root solution `../Jarvis.slnx` also includes the complete graph.

The platform assembly/file version is 1.0.75.0. A normal Desktop or CLI project build now closes over the dedicated browser runtime: `jarvis-browser-service.*` is copied to the Agent output root and `jarvis-browser-host.*` is copied under `browser/`. This matches the runtime layout already enforced by the official self-contained publish flow and prevents a developer-copied `bin` directory from losing Chrome connectivity because the browser companions were absent.
Desktop/CLI publish and Windows startup acceptance from earlier versions remains historical evidence. See [verification evidence](../docs/BUILD-STATUS.md)
for exact scope, the separate Visual Studio license limitation, and remaining production acceptance.

```powershell
# From the repository root, after package restore:
dotnet build 'jarvis-agent/Jarvis Agent.slnx' -c Debug --no-restore
.\scripts\Verify-AgentReferences.ps1 -Configuration Debug
```

Set `Jarvis.Agent.Desktop` as Startup Project to debug the UI. See the [agent guide](../docs/AGENT.md).

## 1.0.75: browser companions in normal build output

`Jarvis.Agent.Desktop` and `Jarvis.Agent.Cli` reference BrowserService and BrowserHost with `ReferenceOutputAssembly=false`, so MSBuild orders/builds those executable projects without linking them into the Agent. `Directory.Build.targets` then copies the service app files to the entry-point output root and native-host app files to `browser/`; any root-level native-host copies from transitive content are removed. `Verify-AgentOutput.ps1` rejects a root native host and still requires both browser companion executables.

The official `scripts/Build.ps1 -Component Agent` path remains the release packaging path and independently publishes the native host self-contained under `browser/`.

## 1.0.63: persistent process approval

The local approval dialog for `process.start` and `process.spawn` has three choices: `Deny`, `Approve once`, and `Always approve`. The permanent choice is persisted only after a successful atomic permission-file write, is keyed to the exact tool ID, and is reloaded by Desktop, AgentRuntime and CLI doctor/connect paths. Ordinary Full permission remains a separate setting and does not silently become a permanent constrained-process grant.

Tool permissions shows an `Always approved` state for active permanent process grants and a **Require approval again** action. Revoking the grant takes effect immediately and raises the same revocation signal used to stop owned activity; future calls return to scoped-lease/interactive approval behavior.

## 1.0.61: settings navigation cleanup

`Connection center` and `Tool permissions` now use the left sidebar as the only visible settings navigation. The selected destination has a persistent accent indicator and selected background, supports keyboard focus, and is two-way bound to the content host. The content host keeps the existing `TabControl` only as an internal view switcher with its header strip removed plus `Focusable="False"` / `IsTabStop="False"`, so keyboard users do not encounter a second invisible navigation stop. The legacy navigation commands were removed from `MainViewModel`, and the desktop footer version is generated from the running assembly so it cannot drift from the build version.

The UI smoke harness verifies sidebar/content selection synchronization, absence of a rendered tab-header strip, non-focusable internal content hosting, and the runtime version label in addition to the existing permission workflow checks.

## 1.0.23: multiple project directories and icon

Desktop supports multi-select in the folder picker, adding/removing additional project folders and choosing the primary working folder. CLI configure accepts multiple folders. The saved profile remains compatible with old single-directory profiles. Managed jobs accept an optional workingDirectory. The supplied Jarvis MCP icon is integrated in the executable, window, sidebar, tray and dialogs.

This update does not remove the file workspace guard and does not add full-permission configuration; these requested edits were blocked. Existing sensitive-action approvals remain in place. See docs/AGENT.md and docs/BUILD-STATUS.md in the platform root for operator details and executed verification.

## Codex-compatible public tools (1.0.89)

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
