using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Core.Models;
using JarvisCode.Core.Utilities;

namespace JarvisCode.Cli;

internal sealed partial class InteractiveRepl
{

    private async Task OpenDiffAsync(string argument, CancellationToken cancellationToken)
    {
        if (State.Diff is not null) { State.Diff = null; return; }
        var sources = new List<DiffSource>();
        foreach (var staged in new[] { false, true })
        {
            var arguments = new List<string> { "diff" };
            if (staged) arguments.Add("--cached");
            arguments.Add("--name-only"); arguments.Add("-z");
            var listing = await CliWorkspace.GitAsync(_session.WorkingDirectory, cancellationToken, [.. arguments]);
            var files = new List<DiffFile>();
            if (listing.Code == 0)
                foreach (var file in listing.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    var command = new List<string> { "--no-pager", "diff", "--no-ext-diff", "--no-color" };
                    if (staged) command.Add("--cached");
                    command.Add("--"); command.Add(file);
                    var diff = await CliWorkspace.GitAsync(_session.WorkingDirectory, cancellationToken, [.. command]);
                    files.Add(new DiffFile(file, diff.Output.Split('\n')));
                }
            sources.Add(new DiffSource(staged ? "Staged changes" : "Uncommitted changes", files));
        }
        var checkpoints = await services.App.Checkpoints.ListAsync(_session.Id, cancellationToken);
        var records = new List<Core.Checkpoints.CheckpointRecord>();
        foreach (var checkpoint in checkpoints)
            if (await services.App.Checkpoints.ReadAsync(_session.Id, checkpoint.TurnNumber, cancellationToken) is { } record)
                records.Add(record);
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var files = new List<DiffFile>();
            foreach (var before in record.Files)
            {
                if (before.Existed && before.OriginalContent is null || File.Exists(before.Path) && new FileInfo(before.Path).Length > 5 * 1024 * 1024)
                {
                    files.Add(new DiffFile(Path.GetRelativePath(_session.WorkingDirectory, before.Path),
                        ["This file exceeds the checkpoint text preview limit."]));
                    continue;
                }
                var next = records.Skip(index + 1).SelectMany(entry => entry.Files)
                    .FirstOrDefault(file => file.Path.Equals(before.Path, StringComparison.OrdinalIgnoreCase));
                var after = next is not null ? next.OriginalContent ?? ""
                    : File.Exists(before.Path) ? await File.ReadAllTextAsync(before.Path, cancellationToken) : "";
                var diff = LineDiff.Compute(before.OriginalContent ?? "", after);
                files.Add(new DiffFile(Path.GetRelativePath(_session.WorkingDirectory, before.Path), diff is null
                    ? ["Diff is too large to calculate; current file:", .. after.Split('\n')]
                    : [.. diff.Select(line => (line.Kind switch { DiffKind.Added => "+", DiffKind.Removed => "-", _ => " " }) + line.Text)]));
            }
            sources.Add(new DiffSource("Turn " + record.TurnNumber, files));
        }
        State.Diff = new DiffViewer(sources);
        if (argument.Trim() == "staged") State.Diff.Handle("diff:nextSource", _console.Height);
    }

    private void OpenTranscript(bool toggleDetails = false)
    {
        if (toggleDetails) State.ShowAllTranscript = !State.ShowAllTranscript;
        var lines = new List<string>();
        var markdown = new Repl.Render.MarkdownTerminal(_ansi, _console.Width);
        foreach (var message in _session.Messages.ToArray())
        {
            if (!State.ShowAllTranscript && (message.IsMeta || message.HarnessSystemTurn)) continue;
            var text = State.ShowAllTranscript ? message.GetText() : SystemReminders.VisibleText(message);
            if (text.Length > 0)
            {
                lines.Add(_ansi.Bold(message.Role == Role.User ? "You" : "Jarvis"));
                lines.AddRange(markdown.Render(text).Split('\n'));
            }
            if (State.ShowAllTranscript)
                foreach (var block in message.Content)
                {
                    if (block is ToolCallBlock call) lines.Add(call.Name + " " + call.ArgumentsJson);
                    if (block is ToolResultBlock result) lines.AddRange(result.Content.Split('\n'));
                }
            lines.Add("");
        }
        State.Document = new ScrollableDocument("Transcript · Ctrl+E " + (State.ShowAllTranscript ? "hide details" : "show all"),
            [.. lines.SelectMany(line => Repl.Render.TextWidth.Wrap(line, _console.Width))]);
        State.Document.Scroll("scroll:bottom", _console.Height - 2);
    }

    private async Task BabysitPrAsync(string argument, CancellationToken cancellationToken)
    {
        if (argument.Trim() == "status")
        { EmitNotice(_prMonitor is null ? "PR monitoring is off." : "Monitoring this session's bound pull request."); return; }
        if (argument.Trim() == "off")
        {
            if (_prMonitor is not null) await _prMonitor.DisposeAsync();
            _prMonitor = null; _gate.PrAutoFixActive = false;
            services.App.UiSettings.Current.SessionPrAutoFix.Remove(_session.Id);
            services.App.UiSettings.Save();
            EmitNotice("PR monitoring stopped."); return;
        }
        var info = GitStatusProbe.Read(_session.WorkingDirectory, "local");
        if (info.Pr is not { } pr || pr.State is PrDisplayState.Closed or PrDisplayState.Merged || info.BranchName is not { Length: > 0 } branch)
        { EmitError("This branch has no open pull request to monitor."); return; }
        var binding = new PrAutoFixBinding(pr.Number, pr.Url, _session.WorkingDirectory, branch)
        { AutoFix = true, BaseBranch = pr.BaseRefName };
        if (!binding.IsValid) { EmitError("The pull request could not be bound to this repository and branch."); return; }
        if (_prMonitor is not null) await _prMonitor.DisposeAsync();
        var lifetime = _sessionLifetime!.Token;
        var owner = State;
        _prMonitor = new PrAutoFixMonitor(binding, notice =>
        {
            if (lifetime.IsCancellationRequested) return;
            InSession(owner, () => EmitNotice(notice.Summary));
            QueueNotification(ChatMessage.FromUserText(notice.Render()) with { IsMeta = true }, lifetime, owner);
        });
        services.App.UiSettings.Current.SessionPrAutoFix[_session.Id] = binding;
        services.App.UiSettings.Save();
        _gate.PrAutoFixActive = true;
        _prMonitor.Start();
        EmitNotice($"Monitoring PR #{pr.Number} on {branch}. Use /babysit-pr off to stop.");
    }
}
