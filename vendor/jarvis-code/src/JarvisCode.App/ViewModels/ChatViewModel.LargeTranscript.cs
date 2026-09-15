namespace JarvisCode.App.ViewModels;

public partial class ChatViewModel
{
    /// <summary>
    /// Poses a transcript with more rows than any real session, so the virtualizer
    /// can be driven against something it cannot possibly build in one pass. The
    /// rows are the kinds a real Code session produces, in the proportions one
    /// produces them: mostly answers and tool runs, a prompt every few turns.
    /// </summary>
    public void ShowLargeTranscript(int turns)
    {
        for (int turn = 0; turn < turns; turn++)
        {
            Transcript.Add(new UserMessageItem
            {
                Text = $"Prompt {turn}: port the estimator and keep the suite green.",
                MessageIndex = turn * 4,
                TurnNumber = turn + 1,
                RowKey = Services.TranscriptKeys.ForHistory(turn * 4, 0),
            });

            Transcript.Add(new AssistantTextItem
            {
                Markdown =
                    $"### Turn {turn}\n\n" +
                    "The estimator answers a row's height before anybody has looked at it, " +
                    "which is what a scrollbar over a thousand rows is computed from.\n\n" +
                    "```csharp\n" +
                    $"var height = TranscriptRowEstimates.For(row, {960 + turn}, 1);\n" +
                    "```\n\n" +
                    "| kind | px |\n| --- | --- |\n| tool row | 40 |\n| thinking | 24 |\n",
                IsStreaming = false,
                MessageIndex = (turn * 4) + 1,
                RowKey = Services.TranscriptKeys.ForHistory((turn * 4) + 1, 0),
            });

            var run = new ToolGroupItem
            {
                RowKey = Services.TranscriptKeys.ForHistory((turn * 4) + 2, 0),
            };
            for (int call = 0; call < 3; call++)
            {
                run.AddCall(new ToolCallItem
                {
                    CallId = $"pose-{turn}-{call}",
                    ToolName = call == 0 ? "Read" : call == 1 ? "Edit" : "PowerShell",
                    Description = $"row {turn}.{call}",
                    IsRunning = false,
                });
            }

            run.RefreshTitle();
            Transcript.Add(run);

            Transcript.Add(new ThinkingItem
            {
                Text = $"Turn {turn}: the offsets are rebuilt behind a dirty flag.",
                IsStreaming = false,
                IsChatCell = !IsCodeSurface,
                RowKey = Services.TranscriptKeys.ForHistory((turn * 4) + 3, 0),
            });
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Poses one expanded tool run holding far more calls than a screen, which is
    /// the row this port used to build whole the moment somebody opened it.
    /// </summary>
    public ToolGroupItem ShowWideToolRun(int calls)
    {
        var run = new ToolGroupItem { IsExpanded = true };
        for (int call = 0; call < calls; call++)
        {
            run.AddCall(new ToolCallItem
            {
                CallId = $"wide-{call}",
                ToolName = call % 2 == 0 ? "Read" : "Edit",
                Description = $"call {call}",
                IsRunning = false,
            });
        }

        run.RefreshTitle();
        Transcript.Add(run);
        OnPropertyChanged(nameof(IsEmpty));
        return run;
    }

    /// <summary>
    /// Poses a session file with more stored messages than one page holds, then
    /// builds the transcript from it the way opening the session does — through
    /// the real rebuild, so what the self-test drives is the paging that ships
    /// rather than a stand-in for it.
    /// </summary>
    public void ShowPagedHistory(int messages)
    {
        Session.Messages.Clear();
        for (int i = 0; i < messages; i++)
        {
            Session.Messages.Add(i % 2 == 0
                ? JarvisCode.Core.Models.ChatMessage.FromUserText($"Prompt {i / 2}: keep the suite green.")
                : new JarvisCode.Core.Models.ChatMessage(
                    JarvisCode.Core.Models.Role.Assistant,
                    [new JarvisCode.Core.Models.TextBlock($"Answer {i / 2}. The offsets are rebuilt lazily.")]));
        }

        _transcriptPaged = false;
        _transcriptFrom = 0;
        ResetTranscript();
        RebuildTranscriptFromHistory(Session);
        OnPropertyChanged(nameof(TranscriptHasOlder));
    }
}
