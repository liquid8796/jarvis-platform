# Jarvis Agent operator guide

Open the full extracted tree in Visual Studio 2026; publish the CLI and desktop together using `scripts/Build.ps1`. The resulting agent is an interactive user process on Windows, not a Windows Service. No elevation is requested. Win10/11 behavior, multi-monitor scaling, UAC limitations, clipboard and hotkeys must be exercised using ACCEPTANCE.md before use.

## GUI

Paste the HTTPS origin (no `/mcp` path), enrolled device UUID, one-time enrollment token, and an existing workspace. The private profile is `%LOCALAPPDATA%\JarvisAgent\agent.local.json`; token is DPAPI protected for the current Windows user. Connect saves the profile but does not arm. Use Arm control to permit requests without an automatic time limit until you press Pause, Disconnect or Exit. Sensitive calls open an approval dialog showing the exact tool and arguments. Computer/browser native permissions may ask again. Deny is the default.

Minimize/close hides to system tray; closing the window is not the same as Exit. The tray menu offers restore, pause, and exit. `Ctrl+Alt+Pause` pauses when registration succeeds; another application may occupy this hotkey, so tray Pause remains available. No auto-start entry is installed. Temporary connection loss cancels actions and owned managed processes but preserves your explicit arm choice for reconnect in the same running process. Calls are never replayed. Manual Disconnect/Exit clears the grant; restarting the application always starts paused.

## CLI

`configure` prompts for server/device/workspace/token; `connect` requires local terminal confirmation and one-time action approval. Redirected stdin is rejected. Ctrl+C pauses and exits. A temporary reconnect retains the explicit arm choice from this CLI session; Ctrl+C clears it, and a new CLI process asks again. `list-tools` outputs the actual manifest, and must not be run alongside another host holding the same native bridge. Tool stdout/stderr never pollutes browser native-host stdout.

## Browser

Publish CLI and select `jarvis-agent.exe` from GUI Browser integration or run `jarvis-agent.exe browser-install`. Then load the copied unpacked extension directory shown by the host in Chrome/Edge/CocCoc extension developer mode. The server does not control this installation. The native manifest permits only the derived extension's fixed ID. Do not modify its public manifest key without updating the native-host allowed-origin ID as well. A Chrome extension public identity key is not an SSH/private signing key.

Agent native host: `com.jarvis.agent.browser`; named pipe: `JarvisAgent-browser`. Original Jarvis Code native integration remains separate. Browser installation requires HKCU write access and a browser permitting unpacked extensions. Enterprise browser policies may forbid it; those policies are not bypassed.

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
