using System.Net.Http;
using JarvisCode.App.Services;
using JarvisCode.App.Theming;
using JarvisCode.Core.Agent;
using JarvisCode.Core.BackgroundTasks;
using JarvisCode.Core.Checkpoints;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Sessions;
using JarvisCode.Core.Settings;
using JarvisCode.Host;
using JarvisCode.Providers;
using JarvisCode.Providers.Anthropic;
using JarvisCode.Providers.ChatGptWeb;
using JarvisCode.Providers.Custom;
using JarvisCode.Providers.DeepSeek;
using JarvisCode.Providers.Gemini;
using JarvisCode.Providers.Kimi;
using JarvisCode.Providers.LlmApi;
using JarvisCode.Providers.MiniMax;
using JarvisCode.Providers.Nvidia;
using JarvisCode.Providers.Ollama;
using JarvisCode.Providers.OpenAi;
using JarvisCode.Providers.OpenRouter;
using JarvisCode.Providers.TokenRouter;
using JarvisCode.Providers.Zhipu;

namespace JarvisCode.App.Composition;

/// <summary>
/// The composition root. The engine has no DI container by design; everything
/// is constructed here once and handed to the UI.
/// </summary>
public sealed class AppServices : IDisposable
{
    public ProfilePaths Paths { get; }

    /// <summary>True when this composition root serves the CLI rather than the window.</summary>
    public bool Headless { get; }

    public UiSettingsStore UiSettings { get; }
    public DesktopExtensionUpdates ExtensionUpdates { get; }
    public SessionGroupsStore SessionGroups { get; }

    /// <summary>The Chat surface's projects and the chats filed under them.</summary>
    public ProjectStore Projects { get; }

    /// <summary>The prompts the user has sent, for the composer's history search.</summary>
    public ComposerHistoryStore ComposerHistory { get; }
    public ComposerDraftStore ComposerDrafts { get; }

    /// <summary>Per-conversation switches on the Chat surface (web search, thinking, tool access).</summary>
    public ChatConversationSettings ChatConversations { get; }
    /// <summary>
    /// One pull request per working directory, shared by the sidebar's PR badge, the
    /// home view's Pull requests section and the PR bar, so a folder is looked up once.
    /// </summary>
    public PrStatusCache PullRequests { get; } = new();

    public ThemeService Theme { get; }
    public SettingsService Settings { get; }
    /// <summary>
    /// This build's providers plus the user's own. Rebuilt whenever settings are saved,
    /// so a hand-written endpoint is usable the moment it is added.
    /// </summary>
    public IProviderRegistry Providers { get; }
    public AgentOrchestrator Orchestrator { get; }

    /// <summary>Code-surface sessions (agentic, per-project).</summary>
    public JsonSessionStore Sessions { get; }

    /// <summary>Chat-surface conversations (toolless).</summary>
    public JsonSessionStore ChatSessions { get; }
    public UsageStatsStore UsageStats { get; }
    public FileCheckpointStore Checkpoints { get; }
    public BackgroundTaskManager BackgroundTasks { get; }

    /// <summary>Live phase/agent progress of the workflow runs the pane lists.</summary>
    public Services.WorkflowRuns WorkflowRuns { get; } = new();

    /// <summary>
    /// The toasts the window shows. Held here rather than on the window so the
    /// sidebar, the session menu and the surfaces can all raise one without
    /// reaching for a parent that may be a split pane.
    /// </summary>
    public Services.ToastQueue Toasts { get; } = new();
    public McpManager Mcp { get; }

    /// <summary>
    /// The OAuth grants for remote MCP servers, so the Connectors page can sign
    /// one in and disconnecting can drop what it stored.
    /// </summary>
    public McpTokenStore McpTokens { get; }
    public BrowserBridge Browser { get; }
    private readonly BrowserBridgeHost? _browserHost;
    public HttpClient Http { get; }

    /// <summary>
    /// The last few calls the providers made, for Settings › Debug. Empty in a headless
    /// host, which has no pane to show them in.
    /// </summary>
    public ModelTrafficLog ModelTraffic { get; } = new();

    /// <summary>Teach mode's overlay session — shared so a turn's end can close it.</summary>
    public TeachController Teach { get; }

    /// <summary>Per-site consent for the browser tools; one grant list per app run.</summary>
    public BrowserOriginGate BrowserOrigins { get; }

    /// <summary>
    /// The plugin monitors this app run has armed. The reference dedupes them by
    /// name so a plugin reload or a repeat skill invoke cannot spawn a second
    /// process, which is why the runner outlives any one session.
    /// </summary>
    public Services.PluginMonitorRunner PluginMonitors { get; } = new();

    private readonly DiagnosticLogFile _log;

    /// <param name="headless">
    /// True when a non-UI host (the jarvis CLI) builds the graph: the browser
    /// bridge listens on a process-private pipe instead of the shared one — a
    /// second server on "JarvisCode-browser" would race the running app for the
    /// extension's relay connections — and the native-messaging manifest is left
    /// alone, because EnsureInstalled points it at the current process's exe.
    /// </param>
    /// <param name="settingsFile">
    /// Overrides where settings load from (the CLI's --settings flag); null
    /// keeps the profile's own settings.json.
    /// </param>
    public AppServices(ProfilePaths paths, bool headless = false, string? settingsFile = null,
        Func<IProviderRegistry, IProviderRegistry>? decorateProviders = null,
        IDesktopExtensionDirectory? extensionDirectory = null,
        ISettingsStore? settingsStoreOverride = null, bool connectDesktopBrowser = false)
    {
        Paths = paths;
        Headless = headless;
        paths.EnsureDirectories();

        // The panels that have no services of their own raise their toasts
        // through this; a headless run has no window to show them in.
        if (!headless)
        {
            Services.ElectronEngine.UseProfile(Services.BrowserStorageProfile.EngineDirectory(paths.Root));
            Services.ToastQueue.Current = Toasts;
        }

        // Attached first so anything the rest of startup traces is captured. The sink
        // is process-wide, so the last composition root built in a process owns it.
        _log = new DiagnosticLogFile(paths.LogsDirectory);
        _log.PruneOldFiles();
        JarvisCode.Core.Utilities.DiagnosticLog.Attach(_log.Write);
        SessionDebugLog.Register(() => _log.CurrentFile, () => JarvisCode.Core.Utilities.DiagnosticLog.Attach(_log.Write));
        JarvisCode.Core.Utilities.DiagnosticLog.Write(
            $"--- started, profile={paths.ProfileName ?? "default"} ---");

        UiSettings = new UiSettingsStore(paths.UiSettingsFile);
        ExtensionUpdates = new DesktopExtensionUpdates(paths, () => UiSettings.Current.ExtensionsAutoUpdate, extensionDirectory);
        if (!headless) ExtensionUpdates.Start();
        Teach = new TeachController(
            // MainWindow is not always the app's own shell (the tray host can
            // claim it), so resolve the real one by type.
            () => System.Windows.Application.Current?.Windows
                .OfType<Views.MainWindow>()
                .FirstOrDefault(w => w.IsVisible),
            () => UiSettings.Current);
        SessionGroups = new SessionGroupsStore(System.IO.Path.Combine(paths.Root, "session-groups.json"));
        Projects = new ProjectStore(System.IO.Path.Combine(paths.Root, "projects.json"));
        ComposerHistory = new ComposerHistoryStore(System.IO.Path.Combine(paths.Root, "prompt-history.json"));
        ComposerDrafts = new ComposerDraftStore(System.IO.Path.Combine(paths.Root, "composer-drafts.json"));
        ChatConversations = new ChatConversationSettings(UiSettings);
        Theme = new ThemeService(ThemeCatalog.Load(paths.ThemesDirectory));

        Settings = new SettingsService(settingsStoreOverride ??
            new JsonSettingsStore(settingsFile ?? paths.SettingsFile, new DpapiSecretProtector()), browserStateRoot: paths.Root);

        // Core loads instruction files by working directory; this is where it is
        // told where this installation keeps the user's and the machine's.
        Services.InstructionScopes.Install(paths, Settings, UiSettings);

        // The prompt form a model on another provider takes. The reference's
        // family rule answers for every model it knows; this answers for the
        // ones it has never seen, and both front-ends read the same setting.
        Services.PromptFormPreference.Install(Settings);

        var transport = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        Http = new HttpClient(transport)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // Providers get their own client so Settings › Debug records model calls and only
        // model calls — MCP, web fetch and the search endpoint share Http and stay out of
        // it. Both clients drive the one transport, and Http owns disposing it.
        var providerHttp = headless
            ? Http
            : new HttpClient(new ModelTrafficHandler(ModelTraffic, transport), disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };

        var providers = new AppProviderRegistry(
            [
                new AnthropicProvider(providerHttp, Settings,
                    endpoint: () => AnthropicProvider.EndpointFromBaseUrl(Settings.Current.AnthropicBaseUrl)),
                new OpenAiProvider(providerHttp, Settings),
                new GeminiProvider(providerHttp, Settings),
                new OllamaProvider(providerHttp, Settings, Settings),
                new NvidiaProvider(providerHttp, Settings, Settings),
                new KimiProvider(providerHttp, Settings),
                OpenRouterProvider.Create(providerHttp, Settings, Settings),
                TokenRouterProvider.Create(providerHttp, Settings, Settings),
                DeepSeekProvider.Create(providerHttp, Settings, Settings),
                ZhipuProvider.Create(providerHttp, Settings, Settings),
                MiniMaxProvider.Create(providerHttp, Settings, Settings),
                new LlmApiProvider(providerHttp, Settings, Settings, Settings),
                new JarvisCode.Providers.Bedrock.BedrockProvider(providerHttp, Settings, Settings),
                new JarvisCode.Providers.Vertex.VertexProvider(providerHttp, Settings, Settings),
                new ChatGptWebProvider(Settings, Settings),
            ],
            () => Settings.Current.CustomProviders,
            spec => CustomProviderFactory.Create(providerHttp, Settings, spec, Settings));
        // The custom list is only ever edited through a save, so that is when it is re-read.
        Settings.SettingsSaved += (_, _) => providers.Refresh();
        Providers = decorateProviders?.Invoke(providers) ?? providers;

        Orchestrator = new AgentOrchestrator();
        Sessions = new JsonSessionStore(System.IO.Path.Combine(paths.SessionsDirectory, "code"));
        ChatSessions = new JsonSessionStore(System.IO.Path.Combine(paths.SessionsDirectory, "chat"));
        UsageStats = new UsageStatsStore(paths.UsageStatsFile);
        Checkpoints = new FileCheckpointStore(paths.CheckpointsDirectory);
        BackgroundTasks = new BackgroundTaskManager
        {
            // The reference mirrors a background command's output to a file and
            // tells the model to Read it; the file lives in the project's own
            // tasks/ directory, beside the session scratchpads.
            OutputDirectoryFor = Services.SessionScratchpad.TasksDirectory,
        };
        McpTokens = new McpTokenStore(
            System.IO.Path.Combine(paths.Root, "mcp-tokens.json"), new DpapiSecretProtector());
        // A headersHelper executes a configured command to mint this server's
        // credential, so it is asked about once per server per session — the
        // same decision a hook command is. A headless run has nobody to ask and
        // therefore refuses, which is also what the reference does when its own
        // trust gate is unmet.
        Mcp = new McpManager(
            Http,
            McpTokens,
            headless ? null : AskToRunHeadersHelperAsync,
            // The reference caches a remote server's discovery listing between
            // runs unless the entry opts out; the store is one directory beside
            // the profile's other state.
            new McpDiscoveryCache(System.IO.Path.Combine(paths.Root, "mcp-discovery-cache")));
        // Native verification uses disposable profiles and must not redirect the user's installed browser integration.
        var isolateBrowserIntegration = headless || Environment.GetEnvironmentVariable("JARVIS_PARITY_ISOLATED") == "1";
        Browser = headless && connectDesktopBrowser
            ? BrowserBridge.ForDesktopProfile(paths.Root)
            : isolateBrowserIntegration
            ? new BrowserBridge($"{BrowserBridge.PipeName}-headless-{Environment.ProcessId}")
            : new BrowserBridge();
        BrowserOrigins = new BrowserOriginGate(Browser, UiSettings);
        if (!headless) _browserHost = new BrowserBridgeHost(paths.Root, Browser);
        if (!isolateBrowserIntegration)
        {
            JarvisBrowserSetup.EnsureInstalled(paths);
        }
    }

    /// <summary>
    /// Asks, on the UI thread, whether a connector's headersHelper command may
    /// run. <see cref="McpManager"/> remembers a yes for the session, keyed by
    /// the server and the command text, so a changed command asks again.
    /// </summary>
    private static Task<bool> AskToRunHeadersHelperAsync(
        JarvisCode.Core.Mcp.McpServerConfig server, string command, CancellationToken cancellationToken)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.FromResult(false);
        }

        return dispatcher.InvokeAsync(() => Views.ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            Services.McpHeadersHelperConsent.Title,
            Services.McpHeadersHelperConsent.Body(server),
            Services.McpHeadersHelperConsent.ConfirmLabel,
            subject: command,
            footnote: Services.McpHeadersHelperConsent.Footnote(server),
            focusCancel: true)).Task;
    }

    public void Dispose()
    {
        ExtensionUpdates.Dispose();
        JarvisCode.Core.Utilities.DiagnosticLog.Write("--- stopping ---");
        JarvisCode.Core.Utilities.DiagnosticLog.Attach(null);
        _browserHost?.Dispose();
        Browser.Dispose();
        BackgroundTasks.Dispose();
        try
        {
            Mcp.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Shutdown must not hang on a wedged MCP server process.
        }

        Http.Dispose();
    }
}
