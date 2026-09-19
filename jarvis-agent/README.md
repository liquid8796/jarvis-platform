# Jarvis Agent - 1.0.75

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
