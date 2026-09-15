using System.Diagnostics;
using System.IO;
using System.Text;
using JarvisCode.App.Services;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Cli.Repl.Input;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Checkpoints;
using JarvisCode.Core.Sessions;

namespace JarvisCode.Cli;

/// <summary>
/// The session flows: starting over, compacting, switching model and effort,
/// and the reference's resume / rename / export / rewind / branch / fork /
/// subtask family.
/// </summary>
internal sealed partial class InteractiveRepl
{

    /// <summary>
    /// /clear: the reference starts a <em>new</em> session and leaves the old
    /// one on disk, so it is still resumable — it does not empty the current
    /// one.
    /// </summary>
    private async Task ClearAsync(CancellationToken cancellationToken)
    {
        if (!options.NoSessionPersistence && _session.Messages.Count > 0 && _turn is not { IsCompleted: false })
        {
            await services.App.Sessions.SaveAsync(_session, cancellationToken);
        }

        var previous = _session;
        _session = Session.CreateNew(previous.WorkingDirectory);
        _session.ModelId = previous.ModelId;
        _session.AdditionalDirectories = [.. previous.AdditionalDirectories];
        _runner = NewRunner();
        _navigator = new HistoryNavigator(_history, _session.WorkingDirectory, _session.Id);
        _screen.ClearAll();
        EmitNotice($"Started a new session. The previous one is still resumable ({previous.Id}).");
    }

    private async Task CompactAsync(string instructions, CancellationToken cancellationToken)
    {
        if (services.Factory.ResolveModel(_session) is not { } model || _session.Messages.Count == 0)
        {
            EmitError(ConversationCompactor.NotEnoughMessages);
            return;
        }

        var compactor = new ConversationCompactor();
        if (!compactor.CanCompact([.. _session.Messages]))
        {
            EmitError(ConversationCompactor.NotEnoughMessages);
            return;
        }

        _compacting = true;
        EmitNotice(StatusRow.CompactingMessage + "…");
        try
        {
            var provider = services.App.Providers.Get(model.ProviderId);
            var compaction = await compactor.CompactAsync(
                provider, model.ModelId, [.. _session.Messages], cancellationToken,
                instructions.Length > 0 ? instructions : null);
            _session.ArchivedMessages.AddRange(compaction.Archived);
            _session.Messages.Clear();
            foreach (var message in compaction.Messages)
            {
                _session.Messages.Add(message);
            }

            if (!options.NoSessionPersistence)
            {
                await services.App.Sessions.SaveAsync(_session, cancellationToken);
            }

            EmitNotice("Compacted session.");
        }
        catch (Exception ex) when (ex is Core.Providers.ProviderException or InvalidOperationException)
        {
            EmitError($"Compaction failed: {ex.Message}");
        }
        finally
        {
            _compacting = false;
        }
    }

    /// <summary>
    /// /model: with no argument it lists what is configured; with one it
    /// switches through the reference's model-switch hooks, which may refuse.
    /// </summary>
    private async Task SwitchModelCommandAsync(string argument, CancellationToken cancellationToken,
        Core.Hooks.ModelSwitchSource source = Core.Hooks.ModelSwitchSource.Command)
    {
        if (argument.Length == 0)
        {
            var models = services.App.Settings.Models;
            if (models.Count == 0)
            {
                EmitNotice("No models yet — add one in Settings › Providers.");
                return;
            }

            _choice = new ChoiceDialog("Select model", [.. models.Select(candidate =>
                new SelectOption(candidate.ModelId, candidate.DisplayName, candidate.ModelId + " · " + candidate.ProviderId))],
                "ModelPicker", models.ToList().FindIndex(candidate => candidate.ModelId == _model.ModelId));
            _choice.EffortIndex = Math.Max(0, Array.FindIndex(RootOptions.EffortChoices,
                level => CliOptions.MapEffort(level) == EffortName()));
            _choiceAccepted = async (dialog, model, token) =>
            {
                await SwitchModelCommandAsync(model, token, Core.Hooks.ModelSwitchSource.Picker);
                if (_model.ModelId == model)
                {
                    EffortCommand(RootOptions.EffortChoices[dialog.EffortIndex] + (dialog.SessionOnly ? " s" : ""));
                    if (!dialog.SessionOnly) { services.App.Settings.Current.DefaultModelId = model; services.App.Settings.Save(); }
                }
            };
            return;
        }

        ModelInfo resolved;
        try
        {
            resolved = services.ResolveModel(argument.Trim());
        }
        catch (CliError error)
        {
            EmitError(error.Message);
            return;
        }

        if (Core.Hooks.ModelSwitch.From(
                _session.ModelId ?? _model.ModelId, resolved.ModelId, argument.Trim(), source,
                _runner.LastContextTokens, _runner.LastUsageAt, DateTimeOffset.Now, targetModel: resolved) is { } change)
        {
            var hooks = _runner.LastHooks ?? services.LoadHooks(_session.WorkingDirectory);
            var decision = await hooks.RunPreModelSwitchAsync(change, cancellationToken, ConfirmModelSwitchAsync);
            if (!decision.Allowed)
            {
                EmitError($"Model unchanged: {decision.BlockReason}");
                return;
            }

            _session.ModelId = resolved.ModelId;
            _model = resolved;
            await hooks.RunEventAsync(Core.Hooks.HookEvent.PostModelSwitch, change.ToPayload(), cancellationToken);
        }
        else
        {
            _session.ModelId = resolved.ModelId;
            _model = resolved;
        }

        EmitNotice($"Model set to {resolved.ModelId}");
        _runner.ModelSelected = true;
    }

    private async Task<bool> ConfirmModelSwitchAsync(string explanation, CancellationToken cancellationToken)
    {
        var previous = _choice;
        var dialog = new ChoiceDialog("Confirm model switch", [new("yes", "Switch model", explanation), new("no", "Keep current model")], selected: 1);
        _choice = dialog;
        try
        {
            while (true)
            {
                Redraw();
                var key = await ReadNextAsync(cancellationToken);
                if (key is null || key.Chord == "ctrl+c") return false;
                if (key.Key == ReplRedrawKey) continue;
                if (key.Text is "y" or "Y") return true;
                if (key.Text is "n" or "N") return false;
                var result = dialog.Handle(_router.Route("Select", key).Action, key);
                if (result.Outcome == DialogOutcome.Cancelled) return false;
                if (result.Outcome == DialogOutcome.Accepted) return result.Value == "yes";
            }
        }
        finally { _choice = previous; }
    }

    /// <summary>
    /// /effort: the reference's trailing <c>s</c> makes the level session-only;
    /// without it the level is saved, and saved per model.
    /// </summary>
    private void EffortCommand(string argument)
    {
        if (!Core.Providers.ProviderCapabilities.For(services.App.Providers.Get(_model.ProviderId)).SupportsThinkingEffort)
        {
            EmitNotice("Thinking is controlled in the ChatGPT browser; this provider does not expose an effort control.");
            return;
        }

        if (string.IsNullOrWhiteSpace(argument))
        {
            _choice = new ChoiceDialog("Select effort", [.. RootOptions.EffortChoices.Select(level => new SelectOption(level, level))],
                "EffortSlider", Math.Max(0, Array.FindIndex(RootOptions.EffortChoices, level => CliOptions.MapEffort(level) == EffortName())));
            _choiceAccepted = (dialog, selected, _) =>
            { EffortCommand(selected + (dialog.SessionOnly ? " s" : "")); return Task.CompletedTask; };
            return;
        }
        var words = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool sessionOnly = words.Length > 1 && words[^1].Equals("s", StringComparison.OrdinalIgnoreCase);
        var level = words.Length > 0 ? words[0].ToLowerInvariant() : "";
        if (CliOptions.MapEffort(level) is not { } effortName)
        {
            EmitNotice($"Effort: {EffortName()} " +
                       "(levels: low, medium, high, xhigh, max; add s to change this session only)");
            return;
        }

        if (sessionOnly)
        {
            _runner.SessionEffortName = effortName;
            EmitNotice($"Effort set to {level} for this session");
            return;
        }

        _runner.SessionEffortName = null;
        services.App.Settings.Current.ThinkingEffortName = effortName;
        if (_session.ModelId is { Length: > 0 } modelId)
        {
            services.App.Settings.Current.EffortByModel[modelId] = effortName;
        }

        services.App.Settings.Save();
        EmitNotice($"Effort set to {level}");
    }

    private async Task RenameAsync(string argument, CancellationToken cancellationToken)
    {
        var title = argument.Trim();
        if (title.Length == 0)
        {
            EmitNotice($"This session is “{_session.Title}”. /rename <new title> renames it.");
            return;
        }

        _session.Title = title;
        if (!options.NoSessionPersistence)
        {
            await services.App.Sessions.SaveAsync(_session, cancellationToken);
        }

        EmitNotice($"Renamed to “{title}”.");
    }

    /// <summary>The reference's export: a dated, slugged file beside the working directory.</summary>
    internal static string ExportFileName(string title, DateTimeOffset now)
    {
        var slug = new string(title.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        if (slug.Length == 0)
        {
            slug = "conversation";
        }

        return $"{now:yyyy-MM-dd-HHmmss}-{slug}.txt";
    }

    private void ExportSession(string argument)
    {
        var path = argument.Trim().Length > 0
            ? Path.GetFullPath(argument.Trim(), _session.WorkingDirectory)
            : Path.Combine(_session.WorkingDirectory, ExportFileName(_session.Title, DateTimeOffset.Now));
        var text = new StringBuilder();
        foreach (var message in _session.Messages)
        {
            var body = SystemReminders.VisibleText(message);
            if (body.Trim().Length == 0)
            {
                continue;
            }

            text.AppendLine(message.Role == Role.User ? "> " + body : body);
            text.AppendLine();
        }

        try
        {
            File.WriteAllText(path, text.ToString());
            EmitNotice($"Conversation exported to: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EmitError($"Could not export: {ex.Message}");
        }
    }

    private async Task ForkAsync(CancellationToken cancellationToken)
    {
        if (_turn is { IsCompleted: false })
        {
            EmitError("Wait for the current turn to finish before forking.");
            return;
        }

        var (fork, error) = await SessionActions.ForkAsync(services.App, _session);
        if (fork is null)
        {
            EmitError(error ?? "Fork failed.");
            return;
        }

        _session = fork;
        _runner = NewRunner();
        EmitNotice("Forked into a new session — the original is untouched.");
        await Task.CompletedTask;
    }

    /// <summary>/branch: the conversation up to the last prompt, in a new session.</summary>
    private async Task BranchAsync(CancellationToken cancellationToken)
    {
        int lastPrompt = _session.Messages.FindLastIndex(m => m.Role == Role.User);
        if (lastPrompt < 0)
        {
            EmitError("Nothing to branch from yet.");
            return;
        }

        var branch = Session.CreateNew(_session.WorkingDirectory);
        branch.Title = _session.Title;
        branch.ModelId = _session.ModelId;
        branch.Messages = [.. _session.Messages.Take(lastPrompt)];
        branch.AdditionalDirectories = [.. _session.AdditionalDirectories];
        if (!options.NoSessionPersistence)
        {
            await services.App.Sessions.SaveAsync(branch, cancellationToken);
        }

        var prompt = SystemReminders.VisibleText(_session.Messages[lastPrompt]);
        _session = branch;
        _runner = NewRunner();
        _composer.Set(prompt);
        EmitNotice("Branched into a new session — your prompt is back in the box.");
    }

    /// <summary>
    /// /resume and --resume: with a search term the newest match is opened,
    /// otherwise the reference's picker comes up.
    /// </summary>
    private async Task OpenResumePickerAsync(string argument, CancellationToken cancellationToken)
    {
        var summaries = await services.App.Sessions.ListAsync(cancellationToken);
        var rows = summaries
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s =>
            {
                var git = CliGitLocation.Read(s.WorkingDirectory);
                return new ResumeRow(s, s.Title is { Length: > 0 } title ? title : s.Id,
                    Format.Relative(s.UpdatedAt, DateTimeOffset.Now), git.Branch)
                { Repository = git.Repository, Worktree = git.Worktree };
            })
            .ToList();
        if (rows.Count == 0)
        {
            EmitNotice(ResumePicker.NoneHere);
            return;
        }

        if (argument.Trim() is { Length: > 0 } term)
        {
            var match = rows.FirstOrDefault(r =>
                r.Session.Id.StartsWith(term, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                await ResumeSessionAsync(match.Session.Id, cancellationToken);
                return;
            }
        }
        _resume = new ResumePicker(rows, _session.WorkingDirectory, _ansi, argument.Trim());
    }

    private async Task HandleResumeChoiceAsync(
        ResumePicker picker, Repl.Dialogs.DialogResult result, CancellationToken cancellationToken)
    {
        switch (result.Value)
        {
            case "resume":
                _resume = null;
                await ResumeSessionAsync(result.Text ?? "", cancellationToken);
                return;

            case "rename":
                if (picker.Current is { } row && result.Text is { Length: > 0 } title)
                {
                    var loaded = await services.App.Sessions.LoadAsync(row.Session.Id, cancellationToken);
                    if (loaded is not null)
                    {
                        loaded.Title = title;
                        await services.App.Sessions.SaveAsync(loaded, cancellationToken);
                        EmitNotice($"Renamed to “{title}”.");
                    }
                }

                return;

            case "preview":
                if (result.Text is { Length: > 0 } id)
                {
                    var preview = await services.App.Sessions.LoadAsync(id, cancellationToken);
                    if (preview is not null)
                    {
                        var first = preview.Messages
                            .Select(SystemReminders.VisibleText)
                            .FirstOrDefault(t => t.Trim().Length > 0) ?? "";
                        EmitNotice(TextWidth.Truncate(first.ReplaceLineEndings(" "), 200, "…"));
                    }
                }

                return;
        }
    }

    private async Task ResumeSessionAsync(string id, CancellationToken cancellationToken)
    {
        var previousModel = _model;
        var previousTokens = _runner.LastContextTokens;
        var previousAt = _runner.LastUsageAt;
        var loaded = await services.App.Sessions.LoadAsync(id, cancellationToken);
        if (loaded is null)
        {
            EmitError($"No conversation found with session ID {id}");
            return;
        }

        _session = loaded;
        _model = services.Factory.ResolveModel(_session) ?? _model;
        _runner = NewRunner();
        if (Core.Hooks.ModelSwitch.From(previousModel.ModelId, _model.ModelId, null, Core.Hooks.ModelSwitchSource.Resume,
            previousTokens, previousAt, DateTimeOffset.Now, targetModel: _model) is { } change)
            await (_runner.LastHooks ?? services.LoadHooks(_session.WorkingDirectory)).RunEventAsync(
                Core.Hooks.HookEvent.PostModelSwitch, change.ToPayload(), cancellationToken);
        _navigator = new HistoryNavigator(_history, _session.WorkingDirectory, _session.Id);
        _screen.ClearAll();
        EmitNotice($"Resumed {_session.Title} ({_session.Messages.Count} messages).");
        ReplayTranscript();
    }

    /// <summary>
    /// /rewind and the double-escape selector: the reference restores the code
    /// and/or the conversation to before a chosen prompt.
    /// </summary>
    private async Task RewindAsync(CancellationToken cancellationToken)
    {
        var prompts = _session.Messages.Select((message, index) => (message, index))
            .Where(entry => entry.message.Role == Role.User && !entry.message.IsMeta && !entry.message.HarnessSystemTurn).ToArray();
        if (prompts.Length == 0)
        {
            EmitNotice("Nothing to rewind to yet.");
            return;
        }

        _choice = new ChoiceDialog(RewindNotice, [.. prompts.Select(entry => new SelectOption(entry.index.ToString(),
            TextWidth.Truncate(SystemReminders.VisibleText(entry.message).ReplaceLineEndings(" "), 120, "…")))],
            "MessageSelector", prompts.Length - 1);
        _choiceAccepted = (_, selected, _) =>
        {
            var index = int.Parse(selected);
            _choice = new ChoiceDialog(RewindNotice, [new("both", "Restore code and conversation"),
                new("conversation", "Restore conversation"), new("code", "Restore code")]);
            _choiceAccepted = (_, mode, token) => RestoreAsync(index, mode, token);
            return Task.CompletedTask;
        };
        await Task.CompletedTask;
    }

    private async Task RestoreAsync(int index, string mode, CancellationToken cancellationToken)
    {
        var prompt = SystemReminders.VisibleText(_session.Messages[index]);
        var store = new FileCheckpointStore(services.App.Paths.CheckpointsDirectory);
        try
        {
            if (mode != "conversation")
            {
                var checkpoints = await store.ListAsync(_session.Id, cancellationToken);
                var checkpoint = checkpoints.FirstOrDefault(entry => entry.MessageIndexAtTurnStart >= index);
                if (checkpoint is not null)
                {
                    var result = await store.RewindAsync(_session.Id, checkpoint.TurnNumber, cancellationToken);
                    if (result?.Skipped.Count > 0)
                    { EmitError("Some files could not be restored: " + string.Join(", ", result.Skipped)); return; }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            EmitError($"Could not restore files: {ex.Message}");
            return;
        }

        while (mode != "code" && _session.Messages.Count > index)
        {
            _session.Messages.RemoveAt(_session.Messages.Count - 1);
        }

        if (mode != "code")
        {
            _composer.Set(prompt);
            _session.SystemPromptSnapshot = null;
            _session.SystemPromptSnapshotKey = null;
            if (!options.NoSessionPersistence) await services.App.Sessions.SaveAsync(_session, cancellationToken);
        }
        EmitNotice("Restored " + mode + " before the selected prompt.");
        EmitNotice(RewindFilesNotice);
    }

    /// <summary>The reference's own two sentences under the rewind selector.</summary>
    public const string RewindNotice =
        "Restore the code and/or conversation to the point before…";

    public const string RewindFilesNotice =
        "Rewinding does not affect files edited manually or via bash.";

    /// <summary>/subtask: this conversation's context, handed to a background session.</summary>
    private async Task SubtaskAsync(string argument, CancellationToken cancellationToken)
    {
        if (argument.Trim().Length == 0)
        {
            EmitError("Usage: /subtask <prompt>");
            return;
        }

        var subtask = Session.CreateNew(_session.WorkingDirectory);
        subtask.Title = "Subtask: " + argument.Trim();
        subtask.ModelId = _session.ModelId;
        subtask.Messages = [.. _session.Messages];
        subtask.AdditionalDirectories = [.. _session.AdditionalDirectories];
        await services.App.Sessions.SaveAsync(subtask, cancellationToken);
        EmitNotice($"Subtask session created ({subtask.Id}). Resume it with /resume {subtask.Id[..8]}.");
    }
}
