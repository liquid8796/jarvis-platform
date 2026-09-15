using System.IO;
using System.Windows;
using Jarvis.Agent.Core;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;
namespace Jarvis.Agent.Windows;
public sealed class ToolInventory : IDisposable
{
    private readonly BrowserBridge _browser;
    private readonly TeachController? _teach;
    public IReadOnlyList<IAgentTool> Tools { get; }
    public ToolInventory(IUserQuestions questions, IArtifactSink artifacts, Func<Window?>? mainWindow = null, string? settingsRoot = null)
    {
        var root = settingsRoot ?? AgentProfile.Root;
        var settings = new UiSettingsStore(Path.Combine(root, "computer-settings.json"));
        settings.Current.ComputerUseFullPermissionProvider = () => ToolConsentScope.IsFullPermission;
        settings.Current.ComputerUseEnabled = true;
        settings.Current.ComputerUseRequireGrants = true;
        // Do not let computer-use request permission to operate this agent's own approval/settings windows.
        foreach (var name in new[] { "Jarvis Agent", "Jarvis.Agent.Desktop", "jarvis-agent" })
            if (!settings.Current.ComputerUseDeniedApps.Contains(name)) settings.Current.ComputerUseDeniedApps.Add(name);
        _browser = new BrowserBridge(BrowserIntegration.PipeName);
        if (mainWindow is not null) _teach = new TeachController(mainWindow, () => settings.Current);
        var tools = new List<IAgentTool>();
        void Add(string category, IEnumerable<ITool> source) => tools.AddRange(source.Select(t => new LegacyToolAdapter(t, category, questions, artifacts, root)));
        Add("computer", ComputerUseTools.Create(settings, _teach));
        Add("browser", JarvisBrowserTools.Create(_browser, Path.Combine(root, "browser-images")));
        Add("visualize", VisualizeTools.Create());
        Add("filesystem", [new ReadFileTool(), new ReadDocumentTool(), new WriteFileTool(), new EditFileTool(),
            new ListDirectoryTool(), new GlobTool(), new GrepTool(), new NotebookEditTool()]);
        Add("git", [new GitStatusTool(), new GitDiffTool(), new GitLogTool(), new GitShowTool(), new GitBlameTool()]);
        Add("shell", [new ShellTool(), new ShellTool(ShellKind.Bash)]);
        Add("workflow", [new TodoTool(), new AskUserQuestionTool()]);
        Tools = tools;
    }
    public void Pause() => _teach?.End();
    public void Dispose() { Pause(); _browser.Dispose(); }
}
