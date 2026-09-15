using System.IO;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli;

/// <summary>The interactive background-agent list and its session dispatch controls.</summary>
internal static class AgentFleet
{
    internal static CliOptions DispatchOptions(IReadOnlyList<string> arguments)
    {
        var filtered = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is "--all" or "--json") continue;
            if (arguments[index] == "--cwd") { index++; continue; }
            filtered.Add(arguments[index]);
        }
        var parsed = CommandLine.Parse(filtered, RootOptions.Specs);
        if (parsed.Error is not null) throw new CliError(parsed.Error);
        return CliOptions.From(parsed) with { Background = true };
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        IConsole? console = null, Func<CliOptions, CancellationToken, Task<string>>? dispatch = null)
    {
        var options = DispatchOptions(arguments);
        var cwdIndex = arguments.ToList().IndexOf("--cwd");
        var selectedCwd = cwdIndex >= 0 && cwdIndex + 1 < arguments.Count ? Path.GetFullPath(arguments[cwdIndex + 1]) : null;
        var ownsConsole = console is null;
        console ??= new SystemConsole();
        var screen = new Screen(console);
        var ansi = new Ansi(console.SupportsAnsi, false);
        using var services = CliServices.Create(options);
        await services.InitializeAsync(options, Environment.CurrentDirectory, cancellationToken);
        var showCompleted = arguments.Contains("--all");
        var index = 0;
        string? notice = null;
        string? input = null;
        ChoiceDialog? choices = null;
        string? choiceKind = null;
        BackgroundSession? selected = null;
        ScrollableDocument? logs = null;
        Task<KeyPress?>? pending = null;
        string? previousFrame = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sessions = (await CliBackground.ListedSessionsAsync(showCompleted, cancellationToken))
                    .Where(session => selectedCwd is null || session.WorkingDirectory.Equals(selectedCwd, StringComparison.OrdinalIgnoreCase)).ToArray();
                index = Math.Clamp(index, 0, Math.Max(0, sessions.Length - 1));
                var lines = new List<string> { ansi.Bold("Manage background agents"),
                    $"Model: {options.Model ?? services.App.Settings.Current.DefaultModelId} · Effort: {options.Effort ?? "default"} · Agent: {options.AgentProfile ?? "default"}", "" };
                if (choices is not null) lines.AddRange(choices.Render(ansi, console.Width));
                else if (logs is not null) lines.AddRange(logs.Render(ansi, console.Width, console.Height - 6));
                else if (input is not null) { lines.Add("New agent"); lines.Add("> " + input); lines.Add("Enter dispatch · Esc cancel"); }
                else
                {
                    foreach (var row in sessions.Select((session, position) => (session, position)).Skip(Math.Max(0, index - 5)).Take(12))
                        lines.Add((row.position == index ? "> " : "  ") + (row.session.Pinned ? "★ " : "") +
                            row.session.Title + " · " + (CliBackground.IsRunning(row.session) ? "running" : row.session.Status) +
                            " · " + row.session.Id);
                    if (sessions.Length == 0) lines.Add("No background agents.");
                    lines.Add(ansi.Dim("↑/↓ select · Enter actions · n new · m model · e effort · a agent"));
                    lines.Add(ansi.Dim("Ctrl+S active/all · Ctrl+T pin · q exit"));
                }
                if (notice is not null) lines.Add(ansi.Dim(notice));
                var rendered = lines.Select(line => TextWidth.Truncate(line, console.Width, "…")).ToArray();
                var frame = string.Join('\n', rendered);
                if (frame != previousFrame) { screen.SetLive(rendered); previousFrame = frame; }
                pending ??= console.ReadKeyAsync(cancellationToken).AsTask();
                if (await Task.WhenAny(pending, Task.Delay(1000, cancellationToken)) != pending) continue;
                var key = await pending; pending = null;
                if (key is null) return 0;
                if (key.Chord is "ctrl+c" or "ctrl+d") return 0;
                if (logs is not null)
                {
                    if (key.Key is "escape" or "q") logs = null;
                    else logs.Scroll(key.Key switch { "up" => "scroll:lineUp", "down" => "scroll:lineDown", "pageup" => "scroll:pageUp",
                        "pagedown" => "scroll:pageDown", "home" => "scroll:top", "end" => "scroll:bottom", _ => null }, console.Height - 6);
                    continue;
                }
                if (choices is not null)
                {
                    var result = choices.Handle(null, key);
                    if (result.Outcome == DialogOutcome.Cancelled) { choices = null; continue; }
                    if (result.Outcome != DialogOutcome.Accepted) continue;
                    var selection = result.Value!;
                    choices = null;
                    if (choiceKind == "model") options = options with { Model = selection };
                    else if (choiceKind == "effort") options = options with { Effort = selection };
                    else if (choiceKind == "agent") options = options with { AgentProfile = selection == "default" ? null : selection };
                    else if (selected is not null)
                    {
                        if (selection == "logs")
                        {
                            var file = Path.Combine(CliBackground.SessionDirectory(selected.Id), "output.jsonl");
                            logs = new ScrollableDocument(selected.Title, (File.Exists(file) ? await CliBackground.ReadLogAsync(file, cancellationToken) : "No output yet.").Split('\n'));
                        }
                        else if (selection == "attach")
                        {
                            screen.ClearLive();
                            if (ownsConsole && console is IDisposable disposable) disposable.Dispose();
                            return await CliBackground.CommandAsync("attach", [selected.Id], cancellationToken);
                        }
                        else
                        {
                            try { await CliBackground.CommandAsync(selection, [selected.Id], cancellationToken); notice = selection + " " + selected.Id; }
                            catch (CliError error) { notice = error.Message; }
                        }
                    }
                    continue;
                }
                if (input is not null)
                {
                    if (key.Key == "escape") input = null;
                    else if (key.Key == "backspace") input = input.Length > 0 ? input[..^1] : "";
                    else if (key.Key == "enter" && input.Trim().Length > 0)
                    {
                        try
                        {
                            var launch = options with { Prompt = input.Trim() };
                            if (dispatch is not null) notice = "Started " + await dispatch(launch, cancellationToken);
                            else await CliBackground.StartAsync(launch, cancellationToken, id => notice = "Started " + id);
                            input = null;
                        }
                        catch (CliError error) { notice = error.Message; }
                    }
                    else if (key.IsPrintable) input += key.Text;
                    continue;
                }
                if (key.Chord == "ctrl+s") { showCompleted = !showCompleted; continue; }
                if (key.Chord == "ctrl+t" && sessions.Length > 0)
                {
                    var row = sessions[index];
                    if (row.Background) CliBackground.Save(row with { Pinned = !row.Pinned });
                    continue;
                }
                switch (key.Key)
                {
                    case "escape": case "q": return 0;
                    case "up": index = Math.Max(0, index - 1); break;
                    case "down": index = Math.Min(sessions.Length - 1, index + 1); break;
                    case "n": input = ""; break;
                    case "m":
                        choiceKind = "model";
                        choices = new ChoiceDialog("Select model", [.. services.App.Settings.Models.Select(model => new SelectOption(model.ModelId, model.DisplayName))]);
                        break;
                    case "e":
                        choiceKind = "effort";
                        choices = new ChoiceDialog("Select effort", [.. RootOptions.EffortChoices.Select(effort => new SelectOption(effort, effort))]);
                        break;
                    case "a":
                        choiceKind = "agent";
                        choices = new ChoiceDialog("Agent", [new("default", "Default"),
                            .. Core.Agent.SubagentTool.BuiltInAgentTypes.Select(agent => new SelectOption(agent, agent)),
                            .. CliAgents.Load(services, Environment.CurrentDirectory).Select(agent => new SelectOption(agent.Name, agent.Name, agent.Description))]);
                        break;
                    case "enter" when sessions.Length > 0:
                        selected = sessions[index]; choiceKind = "actions";
                        if (!selected.Background) { notice = "Foreground session " + selected.Id + " is open in its own terminal."; break; }
                        choices = new ChoiceDialog(selected.Title, [new("attach", "Attach"), new("logs", "Logs"), new("stop", "Stop"), new("rm", "Remove")]);
                        break;
                }
            }
            return 130;
        }
        finally { screen.ClearLive(); if (ownsConsole && console is IDisposable disposable) disposable.Dispose(); }
    }
}
