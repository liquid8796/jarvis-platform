using JarvisCode.Cli;

return await Cli.MainAsync(args);

namespace JarvisCode.Cli
{
    /// <summary>
    /// `jarvis` — the terminal front-end for Jarvis Code, at the command-line
    /// surface of the reference CLI 2.1.251: the same flags, subcommands, help
    /// text, commander error wording and print-mode output shapes, driving this
    /// repo's own engine and the app's own profile (settings, sessions, MCP
    /// servers, skills and hooks are shared with the desktop app).
    /// </summary>
    internal static class Cli
    {
        public static async Task<int> MainAsync(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                return await RunAsync(args, cts.Token);
            }
            catch (CliError error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
            catch (OperationCanceledException)
            {
                return 130;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
            finally { Console.CancelKeyPress -= cancel; }
        }

        private static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
        {
            if (args is ["--internal-background", var backgroundId])
                return await CliBackground.RunChildAsync(backgroundId, cancellationToken);
            // A subcommand only counts as one in the first position, exactly like
            // commander — "jarvis doctor" dispatches, "jarvis -p doctor" is a prompt.
            if (args.Length > 0 && Subcommands.Names.Contains(args[0], StringComparer.Ordinal))
            {
                return await Subcommands.DispatchAsync(args[0], args.Skip(1).ToList(), cancellationToken);
            }

            var parsed = CommandLine.Parse(args, RootOptions.Specs);
            if (parsed.HelpRequested)
            {
                Console.Write(HelpTexts.Root);
                return 0;
            }

            if (parsed.VersionRequested)
            {
                Console.WriteLine($"{StreamJson.Version} (Jarvis Code)");
                return 0;
            }

            if (parsed.Error is { } error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            if (UnsupportedFlag(parsed) is { } refusal)
            {
                Console.Error.WriteLine(refusal);
                return 1;
            }

            var options = CliOptions.From(parsed);
            if (options.Tmux && !options.Worktree) throw new CliError("--tmux requires --worktree.");
            if (options.Tmux && OperatingSystem.IsWindows())
                throw new CliError("--tmux requires tmux on Linux or macOS; use a WSL terminal on Windows.");
            if (options.Tmux) return await CliBackground.StartTmuxAsync(args, cancellationToken);
            if (options.Background) return await CliBackground.StartAsync(options, cancellationToken);
            var originalDirectory = Environment.CurrentDirectory;
            await using var workspace = options.Worktree
                ? await CliWorkspace.CreateAsync(originalDirectory, options.WorktreeName, cancellationToken) : null;
            if (workspace is not null) Environment.CurrentDirectory = workspace.Path;
            try
            {
            // Workflow scripts read this as `budget`; with no flag the target stays
            // null, which is the reference's "no target set". The value itself was
            // validated at parse time, the way commander validates an option.
            if (options.TaskBudget is { Length: > 0 } budgetText &&
                long.TryParse(budgetText, out var budgetTokens) && budgetTokens > 0)
            {
                JarvisCode.App.Services.TurnContextFactory.WorkflowTurnBudget =
                    new JarvisCode.Core.Agent.WorkflowBudget(budgetTokens);
            }

            if (options.Effort is { Length: > 0 } effort && CliOptions.MapEffort(effort) is null)
            {
                // Warned before anything else runs, like the reference — the run
                // then continues at the session's default effort.
                Console.Error.WriteLine(RootOptions.UnknownEffortWarning(effort));
            }

            if (options.Print)
            {
                return await PrintRunner.RunAsync(options, cancellationToken);
            }

            if (Console.IsInputRedirected)
            {
                // Piped or redirected stdin: there is no terminal to hold a REPL,
                // so the turn runs once and prints, like the reference does.
                return await PrintRunner.RunAsync(options with { Print = true }, cancellationToken);
            }

            using var services = CliServices.Create(options);
            using var repl = new InteractiveRepl(services, options);
            return await repl.RunAsync(cancellationToken);
            }
            finally { Environment.CurrentDirectory = originalDirectory; }
        }

        /// <summary>
        /// The first declared-but-unsupported flag on the command line, as the
        /// message to print. Parsing accepts every reference flag so scripts get
        /// a precise refusal instead of "unknown option".
        /// </summary>
        private static string? UnsupportedFlag(ParsedArgs parsed)
        {
            foreach (var (key, feature) in RootOptions.Unsupported)
            {
                if (parsed.Has(key))
                {
                    var spec = RootOptions.Specs.First(s => s.Key == key);
                    return $"Error: {spec.Long} ({feature}) is not available in Jarvis Code.";
                }
            }

            return null;
        }
    }
}
