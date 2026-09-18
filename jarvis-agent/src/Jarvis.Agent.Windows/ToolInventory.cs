using System.IO;
using System.Windows;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;
namespace Jarvis.Agent.Windows;
public sealed class ToolInventory : IDisposable
{
    private readonly IBrowserRuntimeClient _browser;
    private readonly SessionFileObservations _fileObservations = new();
    private readonly SessionBrowserToolSet _browserTools;
    private readonly PerSessionToolSet _computerTools;
    public event Action<string>? BrowserSessionStopRequested;
    private readonly TeachController? _teach;
    private readonly ComputerStateTracker _computerStates = new();
    public IReadOnlyList<IAgentTool> Tools { get; }
    public ToolInventory(IUserQuestions questions, IArtifactSink artifacts, Func<Window?>? mainWindow = null, string? settingsRoot = null,
        IBrowserRuntimeClient? browserRuntime = null)
    {
        var root = settingsRoot ?? AgentProfile.Root;
        var settings = new UiSettingsStore(Path.Combine(root, "computer-settings.json"));
        settings.Current.ComputerUseFullPermissionProvider = () => ToolConsentScope.IsFullPermission;
        settings.Current.ComputerUseEnabled = true;
        settings.Current.ComputerUseRequireGrants = true;
        // Do not let computer-use request permission to operate this agent's own approval/settings windows.
        foreach (var name in new[] { "Jarvis Agent", "Jarvis.Agent.Desktop", "jarvis-agent" })
            if (!settings.Current.ComputerUseDeniedApps.Contains(name)) settings.Current.ComputerUseDeniedApps.Add(name);
        _browser = browserRuntime ?? new BrowserRuntimeClient();
        _browser.ApplicationStopRequested += id => BrowserSessionStopRequested?.Invoke(id);
        if (mainWindow is not null) _teach = new TeachController(mainWindow, () => settings.Current);
        var tools = new List<IAgentTool>();
        void Add(string category, IEnumerable<ITool> source) => tools.AddRange(source.Select(t => new LegacyToolAdapter(t, category, questions, artifacts, root, category == "filesystem" ? _fileObservations : null)));

        var observer = new WindowsComputerObservationProvider();
        _computerTools = new PerSessionToolSet(() => ComputerUseTools.Create(settings, _teach)
            .Select(tool => (IAgentTool)new LegacyToolAdapter(tool, "computer", questions, artifacts, root))
            .Select(tool => (IAgentTool)new StatefulComputerToolAdapter(tool, _computerStates, observer)).ToArray());
        tools.AddRange(_computerTools.Tools);
        tools.Add(new ComputerStateTool(_computerStates, observer));
        _browserTools = new SessionBrowserToolSet(_browser, _computerStates);
        tools.AddRange(_browserTools.Tools);
        Add("visualize", VisualizeTools.Create());
        Add("filesystem", [new ReadFileTool(), new ReadDocumentTool(), new WriteFileTool(), new EditFileTool(),
            new ListDirectoryTool(), new GlobTool(), new GrepTool(), new NotebookEditTool()]);
        Add("git", [new GitStatusTool(), new GitDiffTool(), new GitLogTool(), new GitShowTool(), new GitBlameTool()]);
        Add("shell", [new ShellTool(), new ShellTool(ShellKind.Bash)]);
        Add("workflow", [new TodoTool(), new AskUserQuestionTool()]);
        Tools = tools;
    }
    public async Task StopSessionAsync(AgentSessionIdentity identity, bool close, CancellationToken ct)
    {
        _computerStates.Invalidate(identity.SessionId);
        if (close) { _computerTools.Forget(identity); _fileObservations.Forget(identity); }
        try { await _browserTools.StopSessionAsync(identity, close, ct); }
        finally { if (close) _computerStates.InvalidateAll(); }
    }
    public void Pause() { _computerStates.InvalidateAll(); _teach?.End(); }
    public void Dispose() { Pause(); _browser.Dispose(); }
}
