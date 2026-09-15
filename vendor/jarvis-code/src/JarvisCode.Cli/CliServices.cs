using System.IO;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.Core.Models;
using JarvisCode.Core.Sessions;
using JarvisCode.Core.Settings;
using JarvisCode.Host;

namespace JarvisCode.Cli;

/// <summary>
/// The CLI's composition: the App's own service graph in headless mode (same
/// profile root, settings, sessions, MCP config and providers as the desktop
/// app — a CLI session shows up in the app's sidebar and vice versa), plus the
/// session/model/effort resolution the root command's flags ask for.
/// </summary>
internal sealed class CliServices : IDisposable
{
    public AppServices App { get; }
    public TurnContextFactory Factory { get; }
    public CliSettingsStore CliSettings { get; }
    public bool ChromeEnabled { get; private set; }
    public TurnCustomizations Customizations { get; set; } = new();
    private CliPluginScope? _plugins;
    private CliBrowserSessionHost? _browserSessionHost;
    public IReadOnlyDictionary<string, string> PluginRoots => _plugins?.PluginRoots ?? new Dictionary<string, string>();
    private readonly List<string> _temporaryMcpFiles = [];
    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);

    private CliServices(AppServices app, CliSettingsStore settings)
    {
        App = app;
        Factory = new TurnContextFactory(app);
        CliSettings = settings;
    }

    /// <summary>
    /// JARVISCODE_PROFILE mirrors the app's --profile isolation (used by tests
    /// and E2E runs); --settings points settings at a file or inline JSON.
    /// </summary>
    public static CliServices Create(CliOptions options,
        Func<Core.Providers.IProviderRegistry, Core.Providers.IProviderRegistry>? decorateProviders = null)
    {
        var paths = ProfilePaths.Create(Environment.GetEnvironmentVariable("JARVISCODE_PROFILE"));
        var scoped = new CliSettingsStore(new JsonSettingsStore(paths.SettingsFile, new DpapiSecretProtector()),
            options, Directory.GetCurrentDirectory(), userSettingsPath: paths.SettingsFile);
        CliBetaRegistry.Merge([], options.Betas);
        var browserSessionHost = new CliBrowserSessionHost(paths.Root);
        var app = new AppServices(paths, headless: true,
            decorateProviders: registry => new CliBetaRegistry(browserSessionHost.Wrap(decorateProviders?.Invoke(registry) ?? registry), options.Betas),
            settingsStoreOverride: scoped, connectDesktopBrowser: options.ChromeEnabled == true && !options.SafeMode);
        var services = new CliServices(app, scoped) { _browserSessionHost = browserSessionHost };
        services.ChromeEnabled = options.ChromeEnabled == true && !options.SafeMode;
        if (app.Settings.Current.DefaultModelId is { } defaultModel &&
            defaultModel.ToLowerInvariant() is "sonnet" or "opus" or "haiku" or "fable" or "mythos")
            app.Settings.Current.DefaultModelId = services.ResolveModel(defaultModel).ModelId;
        services.Customizations = new TurnCustomizations
        {
            DisableAutomaticDiscovery = options.Bare || options.SafeMode,
            DisableHooks = options.Bare || options.SafeMode,
            DisableSkills = options.SafeMode || options.DisableSlashCommands,
            DisableAgents = options.SafeMode,
            DisableLsp = options.Bare || options.SafeMode,
            DisableWorkflows = options.SafeMode,
            DisableOutputStyles = options.Bare || options.SafeMode,
            DisableAttribution = options.Bare,
            DisablePrefetch = options.Bare || options.SafeMode,
            SettingSources = options.SettingSources is null ? null : scoped.Sources,
            OutputStyleOverride = options.SafeMode ? null : scoped.OutputStyle,
            Agents = options.SafeMode ? [] : CliAgents.Parse(options.InlineAgents),
        };
        foreach (var (name, value) in scoped.EnvironmentValues) services.SetEnvironment(name, value);
        if (options.Bare) services.SetEnvironment("CLAUDE_CODE_SIMPLE", "1");
        if (options.SafeMode) services.SetEnvironment("CLAUDE_CODE_SAFE_MODE", "1");

        // --debug-file names where this run's debug log goes, which is what makes
        // /debug's "restart with `jarvis --debug`" sentence true here: the
        // reference's own --debug-file replaces the session log's path the same way.
        if (options.DebugFile is { Length: > 0 } debugFile)
        {
            var full = Path.GetFullPath(debugFile);
            void Attach() => JarvisCode.Core.Utilities.DiagnosticLog.Attach(line => AppendDebugLine(full, line));
            SessionDebugLog.Register(() => full, Attach);
            Attach();
        }

        if (CliOptions.MapEffort(options.Effort) is { } effortName)
        {
            // Session-scoped: the in-memory settings object is never saved here.
            app.Settings.Current.ThinkingEffortName = effortName;
        }

        scoped.MarkRuntimeInitialized();

        return services;
    }

    public async Task InitializeAsync(CliOptions options, string cwd, CancellationToken cancellationToken)
    {
        if (ChromeEnabled)
        {
            try
            {
                await App.Browser.RefreshDesktopConnectionAsync(cancellationToken);
                if (!App.Browser.ExtensionReady)
                    throw new InvalidOperationException("Connect the Jarvis Browser extension in the desktop profile before using --chrome.");
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException)
            { throw new CliError("--chrome: " + error.Message); }
        }
        if (options.Bare && App.Settings.GetKey("anthropic") is not { Length: > 0 } &&
            CliSettings.ApiKeyHelper is { Length: > 0 } helper)
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            foreach (var argument in OperatingSystem.IsWindows()
                         ? new[] { "-NoProfile", "-NonInteractive", "-Command", helper } : new[] { "-c", helper })
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new CliError("apiKeyHelper could not start.");
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
            var key = (await output).Trim();
            await error; // Credentials and helper stderr must never reach logs.
            if (process.ExitCode != 0 || key.Length == 0 || key.Any(char.IsWhiteSpace))
                throw new CliError("apiKeyHelper failed or did not return one API key.");
            App.Settings.Current.ApiKeySets["anthropic"] = [key];
        }
        if (!options.SafeMode && (options.PluginDirectories.Count > 0 || options.PluginUrls.Count > 0))
        {
            _plugins = await CliPluginScope.CreateAsync(options.PluginDirectories, options.PluginUrls,
                App.Http, cancellationToken, cwd, Path.Combine(App.Paths.Root, "plugin-data"));
            Customizations = Customizations with
            {
                Plugins = _plugins.Content, PluginOutputStylePaths = _plugins.ScopedOutputStylePaths,
                PluginWorkflowPaths = _plugins.ScopedWorkflowPaths, PluginLspFiles = _plugins.LspFiles,
            };
            foreach (var warning in _plugins.Warnings) Console.Error.WriteLine(warning);
        }
        CliSettings.MarkRuntimeInitialized();
    }

    private void SetEnvironment(string name, string value)
    {
        _originalEnvironment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    internal Core.Hooks.HookRunner LoadHooks(string cwd)
    {
        if (Customizations.DisableHooks) return new Core.Hooks.HookRunner([], cwd);
        var files = new List<string>(Customizations.Plugins.HookFiles);
        if (!Customizations.DisableAutomaticDiscovery)
        {
            if (CliSettings.Sources.Contains("user")) files.Add(App.Paths.UserHooksFile);
            if (CliSettings.Sources.Contains("project"))
            {
                files.Add(Path.Combine(cwd, ".jarvis", "hooks.json"));
                AddSettings("settings.json");
            }
            if (CliSettings.Sources.Contains("local")) AddSettings("settings.local.json");
        }
        return Core.Hooks.HookRunner.LoadOnlyFiles(cwd, files).WithExtra(CliSettings.ExplicitHooks);

        void AddSettings(string name)
        {
            var primary = Path.Combine(cwd, ".jarvis", name);
            files.Add(File.Exists(primary) ? primary : Path.Combine(cwd, ".claude", name));
        }
    }

    /// <summary>
    /// Resolves --model: an exact catalog id first, then the reference's alias
    /// form ('fable', 'opus', 'sonnet', …) against the newest matching family
    /// member. Throws <see cref="CliError"/> for a model the catalog lacks.
    /// </summary>
    public ModelInfo ResolveModel(string name)
    {
        var models = App.Settings.Models;
        if (ModelCatalog.Find(models, name) is { } exact)
        {
            return exact;
        }

        var alias = name.Trim().ToLowerInvariant();
        var match = models
            .Where(m => m.ModelId.Contains(alias, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static m => m.ModelId, StringComparer.Ordinal)
            .FirstOrDefault();
        return match ?? throw new CliError(
            $"Error: Unknown model: {name}. Add it on the provider card in Settings, " +
            "or pass a full model id (e.g. 'claude-fable-5').");
    }

    /// <summary>Builds the session the flags describe (new, --continue, --resume, --session-id).</summary>
    public async Task<Session> ResolveSessionAsync(CliOptions options, string cwd, CancellationToken cancellationToken)
    {
        var store = App.Sessions;
        Session session;
        if (options.FromPr)
        {
            var matches = await FindPrSessionsAsync(cwd, options.FromPrValue, cancellationToken);
            if (options.FromPrValue is not { Length: > 0 })
                throw new CliError("--from-pr without a PR number or URL requires an interactive picker.");
            if (!IsPrIdentity(options.FromPrValue))
                throw new CliError("Use a PR number or URL with --print, or open the interactive --from-pr picker.");
            var selected = matches.FirstOrDefault() ?? throw new CliError("No conversation found linked to PR " + options.FromPrValue);
            session = await store.LoadAsync(selected.Id, cancellationToken) ?? throw new CliError("Linked conversation could not be loaded.");
        }
        else if (options.Continue)
        {
            var summaries = await store.ListAsync(cancellationToken);
            var recent = summaries
                .Where(s => PathsEqual(s.WorkingDirectory, cwd))
                .OrderByDescending(static s => s.UpdatedAt)
                .FirstOrDefault()
                ?? throw new CliError("No conversation found to continue");
            session = await store.LoadAsync(recent.Id, cancellationToken)
                ?? throw new CliError("No conversation found to continue");
        }
        else if (options.Resume && options.ResumeValue is { Length: > 0 } wanted)
        {
            var summaries = await store.ListAsync(cancellationToken);
            var match = summaries.FirstOrDefault(s => s.Id == wanted)
                ?? summaries
                    .Where(s => s.Id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(static s => s.UpdatedAt)
                    .FirstOrDefault()
                ?? throw new CliError($"No conversation found with session ID {wanted}");
            session = await store.LoadAsync(match.Id, cancellationToken)
                ?? throw new CliError($"No conversation found with session ID {wanted}");
        }
        else if (options.Resume)
        {
            throw new CliError("Error: --resume without a session ID needs an interactive picker; " +
                               "run without --print, or pass the session ID.");
        }
        else
        {
            session = NewSession(options, cwd);
            return session;
        }

        if (options.ForkSession)
        {
            var fork = NewSession(options, cwd);
            fork.Title = session.Title;
            fork.ModelId = session.ModelId;
            fork.Messages = [.. session.Messages];
            fork.AdditionalDirectories = [.. session.AdditionalDirectories];
            return fork;
        }

        return session;
    }

    internal static bool IsPrIdentity(string value) => int.TryParse(value, out var number) && number > 0 ||
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" &&
        uri.AbsolutePath.TrimEnd('/').Split('/') is { Length: >= 3 } parts && parts[^2] == "pull" && int.TryParse(parts[^1], out _);

    internal async Task<IReadOnlyList<SessionSummary>> FindPrSessionsAsync(string cwd, string? term, CancellationToken cancellationToken)
    {
        var summaries = await App.Sessions.ListAsync(cancellationToken);
        var repository = GitStatusProbe.Read(cwd, "local", lookUpPullRequest: false).RepoUrl?.TrimEnd('/');
        bool Matches(SessionSummary summary)
        {
            if (!App.UiSettings.Current.SessionPullRequests.TryGetValue(summary.Id, out var links)) return false;
            return links.Any(link =>
                (repository is null ? PathsEqual(summary.WorkingDirectory, cwd) :
                    link.Repo.TrimEnd('/').Equals(repository, StringComparison.OrdinalIgnoreCase) ||
                    link.Url.StartsWith(repository + "/pull/", StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(term) || int.TryParse(term, out var number) && link.Number == number ||
                 Uri.TryCreate(term, UriKind.Absolute, out _) && link.Url.TrimEnd('/').Equals(term.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                 !IsPrIdentity(term) && (summary.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                     link.Branch.Contains(term, StringComparison.OrdinalIgnoreCase))));
        }
        return [.. summaries.Where(Matches).OrderByDescending(summary => summary.UpdatedAt)];
    }

    private Session NewSession(CliOptions options, string cwd)
    {
        var session = Session.CreateNew(cwd);
        if (options.SessionId is { Length: > 0 } wanted)
        {
            if (!Guid.TryParse(wanted, out _))
            {
                throw new CliError("Error: --session-id must be a valid UUID");
            }

            session = new Session { Id = wanted, WorkingDirectory = cwd };
        }

        if (options.SessionName is { Length: > 0 } name)
        {
            session.Title = name;
        }

        foreach (var dir in CliSettings.AdditionalDirectories.Concat(options.AddDirs).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (JarvisCode.Core.Utilities.NetworkPaths.IsNetworkPath(dir))
            {
                throw new CliError(JarvisCode.Core.Utilities.NetworkPaths.AddDirectoryRefusal(dir));
            }

            var full = Path.GetFullPath(dir, cwd);
            if (!Directory.Exists(full))
            {
                throw new CliError($"Error: --add-dir path is not a directory: {dir}");
            }

            session.AdditionalDirectories.Add(full);
        }

        return session;
    }

    /// <summary>Connects MCP servers the way the app does; --strict-mcp-config keeps only --mcp-config files.</summary>
    public async Task ConnectMcpAsync(CliOptions options, string cwd, CancellationToken cancellationToken)
    {
        if (options.SafeMode) return;
        var extraFiles = new List<string>();
        foreach (var config in options.McpConfigs)
        {
            var trimmed = config.TrimStart();
            if (trimmed.StartsWith('{'))
            {
                var file = Path.Combine(
                    Path.GetTempPath(), $"jarvis-mcp-{Guid.NewGuid():N}.json");
                File.WriteAllText(file, config);
                _temporaryMcpFiles.Add(file);
                extraFiles.Add(file);
            }
            else
            {
                extraFiles.Add(Path.GetFullPath(config, cwd));
            }
        }
        extraFiles.AddRange(Customizations.Plugins.McpFiles);

        if (options.StrictMcpConfig || options.Bare || options.SafeMode ||
            options.SettingSources is not null && !CliSettings.Sources.Contains("project") && !CliSettings.Sources.Contains("local"))
        {
            // Only --mcp-config servers: point the loader at a directory that
            // cannot hold a project config and skip the user-level file.
            var emptyDir = Path.Combine(Path.GetTempPath(), $"jarvis-mcp-strict-{Environment.ProcessId}");
            Directory.CreateDirectory(emptyDir);
            await App.Mcp.RefreshAsync(emptyDir,
                userConfigPath: !options.StrictMcpConfig && !options.Bare && !options.SafeMode && CliSettings.Sources.Contains("user")
                    ? App.Paths.UserMcpFile : null, cancellationToken, extraFiles);
            return;
        }

        await App.Mcp.RefreshAsync(cwd, CliSettings.Sources.Contains("user") ? App.Paths.UserMcpFile : null,
            cancellationToken, extraFiles);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Appends one debug line to the file --debug-file named, never throwing at it.</summary>
    private static void AppendDebugLine(string path, string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that throws would break the very run it is here to explain.
        }
    }

    public void Dispose()
    {
        Factory.Dispose();
        IdeServices.Bridge.Dispose();
        App.Dispose();
        _browserSessionHost?.Dispose();
        _plugins?.Dispose();
        foreach (var file in _temporaryMcpFiles)
            try { File.Delete(file); } catch (IOException) { }
        foreach (var (name, value) in _originalEnvironment) Environment.SetEnvironmentVariable(name, value);
    }

    public IReadOnlyList<Core.Tools.ITool> ChromeTools() => !ChromeEnabled || !App.Browser.ExtensionReady ? [] :
        InternalMcpServers.Compose([JarvisBrowserTools.Server(App.Browser,
            Path.Combine(App.Paths.Root, "browser-images"), App.BrowserOrigins)],
            new InternalMcpSessionContext { ChromeExtensionEnabled = true }, App.UiSettings.Current.InternalMcpTools,
            App.Mcp.ConnectedToolCounts.Keys);

    public IReadOnlyList<McpServerInstructions.Block> ChromeInstructions(string? modelId, bool canDefer = true)
    {
        var tools = ChromeTools();
        return McpServerInstructions.Collect([JarvisBrowserTools.Server(App.Browser)],
            new InternalMcpSessionContext { ChromeExtensionEnabled = ChromeEnabled,
                ToolSearchAvailable = canDefer && TurnContextFactory.WillDefer(tools, modelId) }, tools.Select(tool => tool.Name));
    }
}

/// <summary>A user-facing CLI failure: its message prints to stderr and the process exits 1.</summary>
internal sealed class CliError(string message) : Exception(message);
