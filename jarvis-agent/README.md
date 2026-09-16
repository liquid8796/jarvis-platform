# Jarvis Agent - 1.0.61

Open `Jarvis Agent.slnx` with the entire repository present. Keep its Vendor projects loaded:
those assemblies implement reused tools, while the standalone Jarvis Code application is excluded
from Agent build/publish output. Root solution `../Jarvis.slnx` also includes the complete graph.

The platform assembly version is 1.0.61.0. This patch simplifies Agent settings navigation: the persistent left sidebar is now the single navigation surface for Connection center and Tool permissions, the duplicated tab-header row is no longer rendered, the hidden content host is removed from keyboard focus, and the selected sidebar destination stays synchronized with the active settings content. The desktop footer now reads its version from the running assembly instead of a hard-coded label.
Desktop/CLI publish and Windows startup acceptance from earlier versions remains historical evidence. See [verification evidence](../docs/BUILD-STATUS.md)
for exact scope, the separate Visual Studio license limitation, and remaining production acceptance.

```powershell
# From the repository root, after package restore:
dotnet build 'jarvis-agent/Jarvis Agent.slnx' -c Debug --no-restore
.\scripts\Verify-AgentReferences.ps1 -Configuration Debug
```

Set `Jarvis.Agent.Desktop` as Startup Project to debug the UI. See the [agent guide](../docs/AGENT.md).

## 1.0.61: settings navigation cleanup

`Connection center` and `Tool permissions` now use the left sidebar as the only visible settings navigation. The selected destination has a persistent accent indicator and selected background, supports keyboard focus, and is two-way bound to the content host. The content host keeps the existing `TabControl` only as an internal view switcher with its header strip removed plus `Focusable="False"` / `IsTabStop="False"`, so keyboard users do not encounter a second invisible navigation stop. The legacy navigation commands were removed from `MainViewModel`, and the desktop footer version is generated from the running assembly so it cannot drift from the build version.

The UI smoke harness verifies sidebar/content selection synchronization, absence of a rendered tab-header strip, non-focusable internal content hosting, and the runtime version label in addition to the existing permission workflow checks.

## 1.0.23: multiple project directories and icon

Desktop supports multi-select in the folder picker, adding/removing additional project folders and choosing the primary working folder. CLI configure accepts multiple folders. The saved profile remains compatible with old single-directory profiles. Managed jobs accept an optional workingDirectory. The supplied Jarvis MCP icon is integrated in the executable, window, sidebar, tray and dialogs.

This update does not remove the file workspace guard and does not add full-permission configuration; these requested edits were blocked. Existing sensitive-action approvals remain in place. See docs/AGENT.md and docs/BUILD-STATUS.md in the platform root for operator details and executed verification.
