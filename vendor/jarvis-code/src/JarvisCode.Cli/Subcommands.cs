using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Mcp;

namespace JarvisCode.Cli;

/// <summary>
/// The reference CLI's subcommand surface. Commands whose feature exists in
/// this build run for real (doctor, mcp, plugin, project purge, auth status);
/// the ones bound to Anthropic's cloud or to the reference's own binary
/// management (install, update, setup-token, gateway, ultrareview, background
/// agent sessions) parse and then refuse with the reason, so a script gets a
/// loud failure instead of a silent no-op.
/// </summary>
internal static class Subcommands
{
    /// <summary>Every subcommand the reference declares, in its help order.</summary>
    public static readonly string[] Names =
    [
        "agents", "attach", "auth", "auto-mode", "doctor", "gateway", "import", "install",
        "logs", "mcp", "plugin", "plugins", "project", "respawn", "rm", "setup-token",
        "stop", "kill", "ultrareview", "update", "upgrade",
    ];

    /// <summary>Aliases the reference declares, mapped to the name its help is filed under.</summary>
    private static string Canonical(string name) => name switch
    {
        "plugins" => "plugin",
        "kill" => "stop",
        "upgrade" => "update",
        _ => name,
    };

    /// <summary>A command path ("mcp", "project purge") → the help text for it.</summary>
    public static string? HelpFor(string path)
    {
        var canonical = string.Join(
            ' ', path.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Canonical));
        return HelpTexts.ByPath.TryGetValue(canonical, out var help) ? help : null;
    }

    /// <summary>
    /// The help for the deepest command the arguments name. `project purge --help`
    /// prints the purge help, not the project one — the reference documents every
    /// level of its command tree separately.
    /// </summary>
    private static string HelpForInvocation(string name, IReadOnlyList<string> rest)
    {
        var path = new List<string> { name };
        foreach (var token in rest)
        {
            if (token.StartsWith('-'))
            {
                break;
            }

            path.Add(token);
        }

        // Deepest first: an unknown leaf falls back to its parent's help, which
        // is what the reference prints for `mcp bogus --help`.
        for (int depth = path.Count; depth >= 1; depth--)
        {
            if (HelpFor(string.Join(' ', path.Take(depth))) is { } help)
            {
                return help;
            }
        }

        return HelpTexts.Root;
    }

    public static async Task<int> DispatchAsync(
        string name, IReadOnlyList<string> rest, CancellationToken cancellationToken)
    {
        if (rest.Contains("-h") || rest.Contains("--help"))
        {
            Console.Write(HelpForInvocation(name, rest));
            return 0;
        }

        return name switch
        {
            "doctor" => await DoctorCommandAsync(cancellationToken),
            "mcp" => await McpAsync(rest, cancellationToken),
            "plugin" or "plugins" => Plugin(rest),
            "project" => Project(rest),
            "auth" => Auth(rest),
            "agents" or "attach" or "logs" or "stop" or "kill" or "rm" or "respawn" =>
                await CliBackground.CommandAsync(name, rest, cancellationToken),
            "install" or "update" or "upgrade" =>
                Unsupported("self-update", "Jarvis Code ships with the app; rebuild or reinstall it instead"),
            "setup-token" => Unsupported("Anthropic subscription tokens", "add an API key in Settings › Providers"),
            "gateway" => Unsupported("the enterprise gateway", null),
            "import" => Unsupported("config import from other agents", null),
            "ultrareview" => Unsupported("cloud-hosted multi-agent review", "use /code-review in a session"),
            "auto-mode" => Unsupported("the auto-mode classifier", "permission modes are set with --permission-mode"),
            _ => UnknownCommand(name),
        };
    }

    private static int UnknownCommand(string name)
    {
        Console.Error.WriteLine($"error: unknown command '{name}'");
        return 1;
    }

    private static int Unsupported(string feature, string? alternative)
    {
        Console.Error.WriteLine(alternative is null
            ? $"Error: {feature} is not available in Jarvis Code."
            : $"Error: {feature} is not available in Jarvis Code — {alternative}.");
        return 1;
    }

    public static async Task<int> DoctorCommandAsync(CancellationToken cancellationToken)
    {
        using var services = CliServices.Create(new CliOptions { Prompt = "" });
        var app = services.App;
        var settings = app.Settings.Current;
        Console.WriteLine("Jarvis Code doctor");
        Console.WriteLine();
        Console.WriteLine($"Running: {StreamJson.Version}");
        Console.WriteLine($"Platform: {(Environment.Is64BitProcess ? "win32-x64" : "win32-x86")}");
        Console.WriteLine($"Path: {Environment.ProcessPath}");
        Console.WriteLine($"Profile root: {app.Paths.Root}");
        Console.WriteLine();

        var cwd = Directory.GetCurrentDirectory();
        var model = JarvisCode.Core.Settings.ModelCatalog.Find(app.Settings.Models, settings.DefaultModelId);
        int configured = McpConfig.Load(cwd, app.Paths.UserMcpFile).Count;
        int connected = 0;
        if (configured > 0)
        {
            await services.ConnectMcpAsync(new CliOptions { Prompt = "" }, cwd, cancellationToken);
            connected = app.Mcp.ConnectedToolCounts.Count;
        }

        var report = await SessionInfo.RunDoctorAsync(
            cwd,
            app.Paths.Root,
            settings.SearxngBaseUrl,
            model is not null && !string.IsNullOrEmpty(app.Settings.GetKey(model.ProviderId)),
            model?.ModelId,
            configured,
            connected,
            app.Http,
            model?.MaxContextTokens ?? 0);
        Console.WriteLine(report);
        Console.WriteLine();
        Console.WriteLine("For a full setup checkup that can also fix issues, run /doctor in a Jarvis Code session.");
        return 0;
    }

    private static async Task<int> McpAsync(IReadOnlyList<string> rest, CancellationToken cancellationToken)
    {
        var sub = rest.Count > 0 ? rest[0] : "list";
        var args = rest.Skip(1).ToList();
        using var services = CliServices.Create(new CliOptions { Prompt = "" });
        var app = services.App;
        var cwd = Directory.GetCurrentDirectory();
        var userConfig = app.Paths.UserMcpFile;

        switch (sub)
        {
            case "list":
            {
                var servers = McpConfig.Load(cwd, userConfig);
                if (servers.Count == 0)
                {
                    Console.WriteLine("No MCP servers configured. Use `jarvis mcp add` to add one.");
                    return 0;
                }

                Console.WriteLine("Checking MCP server health…");
                Console.WriteLine();
                await app.Mcp.RefreshAsync(cwd, userConfig, cancellationToken);
                var connectedCounts = app.Mcp.ConnectedToolCounts;
                foreach (var server in servers)
                {
                    var target = server.IsRemote
                        ? $"{server.Url} ({server.Type.ToUpperInvariant()})"
                        : string.Join(' ', new[] { server.Command }.Concat(server.Args));
                    var status = connectedCounts.TryGetValue(server.Name, out var tools)
                        ? $"✓ Connected ({tools} tools)"
                        : "✗ Failed to connect";
                    Console.WriteLine($"{server.Name}: {target} - {status}");
                }

                return 0;
            }

            case "get":
            {
                if (args.Count == 0)
                {
                    Console.Error.WriteLine("error: missing required argument 'name'");
                    return 1;
                }

                var server = McpConfig.Load(cwd, userConfig)
                    .FirstOrDefault(s => s.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
                if (server is null)
                {
                    Console.Error.WriteLine($"No MCP server found with name: {args[0]}");
                    return 1;
                }

                Console.WriteLine($"{server.Name}:");
                Console.WriteLine($"  Type: {server.Type}");
                if (server.IsRemote)
                {
                    Console.WriteLine($"  URL: {server.Url}");
                    foreach (var header in server.Headers)
                    {
                        Console.WriteLine($"  Header: {header.Key}: ****");
                    }
                }
                else
                {
                    Console.WriteLine($"  Command: {server.Command}");
                    if (server.Args.Count > 0)
                    {
                        Console.WriteLine($"  Args: {string.Join(' ', server.Args)}");
                    }

                    foreach (var env in server.Env)
                    {
                        Console.WriteLine($"  Env: {env.Key}=****");
                    }
                }

                return 0;
            }

            case "add":
            {
                var options = CommandLine.Parse(args,
                [
                    new("--transport", Short: "-t", ValuePlaceholder: "<transport>"),
                    new("--env", Short: "-e", ValuePlaceholder: "<env...>", Variadic: true),
                    new("--header", ValuePlaceholder: "<header...>", Variadic: true),
                    new("--scope", Short: "-s", ValuePlaceholder: "<scope>"),
                ]);
                if (options.Error is { } error)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                if (options.Positionals.Count < 2)
                {
                    Console.Error.WriteLine("error: missing required argument 'name' or 'commandOrUrl'");
                    return 1;
                }

                var name = options.Positionals[0];
                var commandOrUrl = options.Positionals[1];
                var extraArgs = options.Positionals.Skip(2).ToList();
                var transport = options.Value("transport")
                    ?? (commandOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "http" : "stdio");

                var env = ParsePairs(options.ValueList("env"));
                var headers = ParsePairs(options.ValueList("header"), ':');
                var config = transport is "http" or "sse"
                    ? new McpServerConfig(name, "", [], env) { Type = transport, Url = commandOrUrl, Headers = headers }
                    : new McpServerConfig(name, commandOrUrl, extraArgs, env);

                var existing = McpConfig.LoadSingleFile(userConfig)
                    .Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                existing.Add(config);
                McpConfig.SaveUserServers(userConfig, existing);
                Console.WriteLine($"Added MCP server {name} to user config: {userConfig}");
                return 0;
            }

            case "add-json":
            {
                if (args.Count < 2)
                {
                    Console.Error.WriteLine("error: missing required argument 'name' or 'json'");
                    return 1;
                }

                JsonObject parsed;
                try
                {
                    parsed = JsonNode.Parse(args[1]) as JsonObject
                        ?? throw new JsonException("expected a JSON object");
                }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine($"Error: invalid JSON: {ex.Message}");
                    return 1;
                }

                var wrapper = new JsonObject { ["mcpServers"] = new JsonObject { [args[0]] = parsed } };
                var temp = Path.Combine(Path.GetTempPath(), $"jarvis-mcp-add-{Environment.ProcessId}.json");
                File.WriteAllText(temp, wrapper.ToJsonString());
                var added = McpConfig.LoadSingleFile(temp);
                File.Delete(temp);
                if (added.Count == 0)
                {
                    Console.Error.WriteLine("Error: the JSON did not describe a usable server " +
                                            "(expected \"command\" or \"url\")");
                    return 1;
                }

                var servers = McpConfig.LoadSingleFile(userConfig)
                    .Where(s => !s.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase))
                    .ToList();
                servers.AddRange(added);
                McpConfig.SaveUserServers(userConfig, servers);
                Console.WriteLine($"Added MCP server {args[0]} to user config: {userConfig}");
                return 0;
            }

            case "remove":
            {
                if (args.Count == 0)
                {
                    Console.Error.WriteLine("error: missing required argument 'name'");
                    return 1;
                }

                var servers = McpConfig.LoadSingleFile(userConfig);
                var kept = servers.Where(s => !s.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase)).ToList();
                if (kept.Count == servers.Count)
                {
                    Console.Error.WriteLine($"No MCP server found with name: {args[0]}");
                    return 1;
                }

                McpConfig.SaveUserServers(userConfig, kept);
                Console.WriteLine($"Removed MCP server {args[0]} from user config");
                return 0;
            }

            case "login":
            {
                if (args.Count == 0)
                {
                    Console.Error.WriteLine("error: missing required argument 'name'");
                    return 1;
                }

                try
                {
                    await app.Mcp.AuthorizeAsync(args[0], cwd, userConfig, cancellationToken);
                    Console.WriteLine($"Authenticated with {args[0]}");
                    return 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"Error: authentication failed: {ex.Message}");
                    return 1;
                }
            }

            case "logout":
            {
                if (args.Count == 0)
                {
                    Console.Error.WriteLine("error: missing required argument 'name'");
                    return 1;
                }

                var config = McpConfig.Load(cwd, userConfig)
                    .FirstOrDefault(s => s.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
                if (config is null)
                {
                    Console.Error.WriteLine($"No MCP server found with name: {args[0]}");
                    return 1;
                }

                var store = new McpTokenStore(
                    Path.Combine(app.Paths.Root, "mcp-tokens.json"), new JarvisCode.Host.DpapiSecretProtector());
                store.Remove(config);
                Console.WriteLine($"Cleared OAuth credentials for {args[0]}");
                return 0;
            }

            case "serve":
                return Unsupported("running Jarvis Code as an MCP server", null);

            case "add-from-claude-desktop":
                return Unsupported("importing from Claude Desktop", "add servers with `jarvis mcp add`");

            case "reset-project-choices":
                return Unsupported("project server approvals", "this build connects configured servers directly");

            default:
                Console.Error.WriteLine($"error: unknown command '{sub}'");
                return 1;
        }
    }

    private static int Plugin(IReadOnlyList<string> rest)
    {
        var sub = rest.Count > 0 ? rest[0] : "list";
        var args = rest.Skip(1).ToList();
        using var services = CliServices.Create(new CliOptions { Prompt = "" });
        var root = services.App.Paths.PluginsDirectory;

        switch (sub)
        {
            case "list":
            {
                var installed = Plugins.Load(root).Installed;
                if (installed.Count == 0)
                {
                    Console.WriteLine("No plugins installed.");
                    return 0;
                }

                Console.WriteLine("Installed plugins:");
                Console.WriteLine();
                foreach (var plugin in installed)
                {
                    Console.WriteLine($"  ❯ {plugin.Name}");
                    Console.WriteLine($"    Path: {plugin.Directory}");
                    Console.WriteLine($"    Contents: {plugin.SkillCount} skills · {plugin.CommandCount} commands · " +
                                      $"{plugin.AgentCount} agents{(plugin.HasHooks ? " · hooks" : "")}");
                    Console.WriteLine();
                }

                return 0;
            }

            case "marketplace":
            {
                var marketplaces = PluginMarketplaces.List(services.App.Paths.Root);
                if (marketplaces.Count == 0)
                {
                    Console.WriteLine("No marketplaces configured.");
                    return 0;
                }

                foreach (var marketplace in marketplaces)
                {
                    Console.WriteLine($"  ❯ {marketplace.Name}");
                    if (marketplace.Url is { Length: > 0 } url)
                    {
                        Console.WriteLine($"    {url}");
                    }

                    foreach (var plugin in PluginMarketplaces.Plugins(marketplace))
                    {
                        Console.WriteLine($"      - {plugin.Name}");
                    }
                }

                return 0;
            }

            case "install" or "i":
            {
                if (args.Count == 0)
                {
                    Console.Error.WriteLine("error: missing required argument 'plugin'");
                    return 1;
                }

                var source = args[0];
                var marketplaceName = source.Contains('@') ? source[(source.IndexOf('@') + 1)..] : null;
                var pluginName = marketplaceName is null ? source : source[..source.IndexOf('@')];
                var marketplaces = PluginMarketplaces.List(services.App.Paths.Root)
                    .Where(m => marketplaceName is null || m.Name.Equals(marketplaceName, StringComparison.OrdinalIgnoreCase));
                foreach (var marketplace in marketplaces)
                {
                    var match = PluginMarketplaces.Plugins(marketplace)
                        .FirstOrDefault(p => p.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        continue;
                    }

                    var target = Path.Combine(root, match.Name);
                    CopyDirectory(match.Directory, target);
                    Console.WriteLine($"Installed {match.Name} from {marketplace.Name} into {target}");
                    return 0;
                }

                Console.Error.WriteLine($"Error: plugin not found in any marketplace: {source}");
                return 1;
            }

            case "uninstall" or "remove":
            {
                if (args.Count == 0)
                {
                    Console.Error.WriteLine("error: missing required argument 'plugin'");
                    return 1;
                }

                var directory = Path.Combine(root, args[0]);
                if (!Directory.Exists(directory))
                {
                    Console.Error.WriteLine($"Error: plugin not installed: {args[0]}");
                    return 1;
                }

                Directory.Delete(directory, recursive: true);
                Console.WriteLine($"Uninstalled {args[0]}");
                return 0;
            }

            case "validate":
                return PluginValidation.Run(args);

            case "enable" or "disable" or "details" or "eval" or "init" or "new" or "prune"
                or "autoremove" or "tag" or "update":
                return Unsupported($"`plugin {sub}`", "manage plugins on the app's Customize › Plugins page");

            default:
                Console.Error.WriteLine($"error: unknown command '{sub}'");
                return 1;
        }
    }

    private static int Project(IReadOnlyList<string> rest)
    {
        if (rest.Count == 0 || rest[0] != "purge")
        {
            Console.Error.WriteLine($"error: unknown command '{(rest.Count > 0 ? rest[0] : "")}'");
            return 1;
        }

        var options = CommandLine.Parse(rest.Skip(1).ToList(),
        [
            new("--all"),
            new("--dry-run"),
            new("--interactive", Short: "-i"),
            new("--yes", Short: "-y"),
        ]);
        if (options.Error is { } error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        using var services = CliServices.Create(new CliOptions { Prompt = "" });
        var app = services.App;
        var path = options.Positionals.Count > 0
            ? Path.GetFullPath(options.Positionals[0])
            : Directory.GetCurrentDirectory();
        bool all = options.Has("all");
        if (all && options.Positionals.Count > 0)
        {
            Console.Error.WriteLine("error: --all is mutually exclusive with [path]");
            return 1;
        }

        var summaries = app.Sessions.ListAsync().GetAwaiter().GetResult();
        var doomed = all
            ? summaries
            : [.. summaries.Where(s => string.Equals(
                Path.TrimEndingDirectorySeparator(s.WorkingDirectory),
                Path.TrimEndingDirectorySeparator(path),
                StringComparison.OrdinalIgnoreCase))];
        if (doomed.Count == 0)
        {
            Console.WriteLine(all ? "No project state found." : $"No project state found for {path}");
            return 0;
        }

        Console.WriteLine(options.Has("dry-run")
            ? $"Would delete {doomed.Count} session(s):"
            : $"Deleting {doomed.Count} session(s):");
        foreach (var session in doomed)
        {
            Console.WriteLine($"  {session.Id}  {session.Title}");
        }

        if (options.Has("dry-run"))
        {
            return 0;
        }

        if (!options.Has("yes"))
        {
            Console.Write("Delete these permanently? [y/N]: ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not ("y" or "yes"))
            {
                Console.WriteLine("Cancelled.");
                return 0;
            }
        }

        foreach (var session in doomed)
        {
            app.Sessions.DeleteAsync(session.Id).GetAwaiter().GetResult();
        }

        Console.WriteLine($"Deleted {doomed.Count} session(s).");
        return 0;
    }

    private static int Auth(IReadOnlyList<string> rest)
    {
        var sub = rest.Count > 0 ? rest[0] : "status";
        if (sub is "login" or "logout")
        {
            return Unsupported(
                "account sign-in",
                "Jarvis Code authenticates per provider — add or remove API keys in Settings › Providers");
        }

        if (sub != "status")
        {
            Console.Error.WriteLine($"error: unknown command '{sub}'");
            return 1;
        }

        using var services = CliServices.Create(new CliOptions { Prompt = "" });
        var app = services.App;
        bool text = rest.Contains("--text");
        var rows = app.Providers.All
            .Select(provider => (
                provider.Id,
                provider.DisplayName,
                HasKey: !string.IsNullOrEmpty(app.Settings.GetKey(provider.Id)),
                provider.RequiresApiKey))
            .ToList();

        if (text)
        {
            foreach (var row in rows)
            {
                var state = !row.RequiresApiKey ? "no key needed" : row.HasKey ? "key stored" : "no key";
                Console.WriteLine($"  {row.DisplayName}: {state}");
            }

            return 0;
        }

        var json = new JsonArray();
        foreach (var row in rows)
        {
            json.Add(new JsonObject
            {
                ["provider"] = row.Id,
                ["displayName"] = row.DisplayName,
                ["requiresApiKey"] = row.RequiresApiKey,
                ["hasCredential"] = row.HasKey,
            });
        }

        Console.WriteLine(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static Dictionary<string, string> ParsePairs(IReadOnlyList<string> raw, char separator = '=')
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in raw)
        {
            int index = entry.IndexOf(separator);
            if (index > 0)
            {
                pairs[entry[..index].Trim()] = entry[(index + 1)..].Trim();
            }
        }

        return pairs;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
