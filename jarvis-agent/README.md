# Jarvis Agent - 1.0.21

Open `Jarvis Agent.slnx` with the entire repository present. Keep its Vendor projects loaded:
those assemblies implement reused tools, while the standalone Jarvis Code application is excluded
from Agent build/publish output. Root solution `../Jarvis.slnx` also includes the complete graph.

The platform assembly version is 1.0.21.0. This patch changes server OAuth consent navigation, not Agent tools.
Desktop/CLI publish and Windows startup acceptance from version 1.0.19 remains historical evidence. See [verification evidence](../docs/BUILD-STATUS.md)
for exact scope, the separate Visual Studio license limitation, and remaining production acceptance.

```powershell
# From the repository root, after package restore:
dotnet build 'jarvis-agent/Jarvis Agent.slnx' -c Debug --no-restore
.\scripts\Verify-AgentReferences.ps1 -Configuration Debug
```

Set `Jarvis.Agent.Desktop` as Startup Project to debug the UI. See the [agent guide](../docs/AGENT.md).

## 1.0.23: multiple project directories and icon

Desktop supports multi-select in the folder picker, adding/removing additional project folders and choosing the primary working folder. CLI configure accepts multiple folders. The saved profile remains compatible with old single-directory profiles. Managed jobs accept an optional workingDirectory. The supplied Jarvis MCP icon is integrated in the executable, window, sidebar, tray and dialogs.

This update does not remove the file workspace guard and does not add full-permission configuration; these requested edits were blocked. Existing sensitive-action approvals remain in place. See docs/AGENT.md and docs/BUILD-STATUS.md in the platform root for operator details and executed verification.
