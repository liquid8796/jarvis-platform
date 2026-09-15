using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Tests.ViewModels;

public class ToolGroupItemTests
{
    private static ToolCallItem Call(
        string tool, bool running = false, bool error = false, bool denied = false, string? args = null)
    {
        var item = new ToolCallItem
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = tool,
            Description = $"{tool}(x)",
            ArgumentsJson = args,
            IsRunning = running,
        };
        if (!running)
        {
            item.IsError = error;
            item.IsDenied = denied;
        }

        return item;
    }

    private static ToolCallItem Done(string tool, string? args = null, string result = "", bool error = false)
    {
        var item = new ToolCallItem
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = tool,
            Description = $"{tool}(x)",
            ArgumentsJson = args,
        };
        item.Complete(result, error);
        return item;
    }

    [Fact]
    public void RunningCallShowsItsLiveLabel()
    {
        var group = new ToolGroupItem();
        group.AddCall(Call("PowerShell", running: true, args: """{"command":"dotnet test"}"""));
        group.RefreshTitle();

        // The reference shows the live call's own label, not a generic "Running…".
        Assert.Equal("Running dotnet test", group.Title);
        Assert.True(group.IsRunning);
    }

    [Fact]
    public void RunningCallWithDescriptionShowsTheConjugatedForm()
    {
        var group = new ToolGroupItem();
        group.AddCall(Call("PowerShell", running: true,
            args: """{"command":"npm i","description":"Install package dependencies"}"""));
        group.RefreshTitle();
        Assert.Equal("Installing package dependencies", group.Title);
    }

    // The wording is the Code transcript's own (its hE), which is not the chat
    // surface's: singulars read "Ran a command" rather than "Ran 1 command", glob
    // and grep carry no count at all, and a kind the reference does not classify —
    // a skill, a browser action, a preview, tool search, this build's own memory
    // tool — is folded into "used {n} tools" rather than given a sentence.
    [Theory]
    [InlineData("PowerShell", 2, "Ran 2 commands")]
    [InlineData("PowerShell", 1, "Ran a command")]
    [InlineData("Bash", 1, "Ran a command")]
    [InlineData("Read", 4, "Read 4 files")]
    [InlineData("Read", 1, "Read a file")]
    [InlineData("Write", 1, "Created a file")]
    [InlineData("Write", 2, "Created 2 files")]
    [InlineData("Edit", 3, "Edited 3 files")]
    [InlineData("Edit", 1, "Edited a file")]
    [InlineData("NotebookEdit", 1, "Edited a notebook")]
    [InlineData("NotebookEdit", 2, "Edited 2 notebooks")]
    [InlineData("Glob", 1, "Found files")]
    [InlineData("Glob", 2, "Found files")]
    [InlineData("Grep", 1, "Searched code")]
    [InlineData("Grep", 2, "Searched code")]
    [InlineData("Agent", 1, "Ran an agent")]
    [InlineData("Agent", 3, "Ran 3 agents")]
    [InlineData("Skill", 1, "Used a tool")]
    [InlineData("memory", 2, "Used 2 tools")]
    [InlineData("todo_write", 1, "Updated todos")]
    [InlineData("tool_search", 1, "Used a tool")]
    [InlineData("ExitPlanMode", 1, "Proposed a plan")]
    [InlineData("mcp__claude-in-chrome__navigate", 1, "Used a tool")]
    [InlineData("mcp__claude-in-chrome__navigate", 2, "Used 2 tools")]
    [InlineData("preview_start", 1, "Used a tool")]
    [InlineData("WebFetch", 2, "Browsed the web")]
    [InlineData("WebSearch", 1, "Browsed the web")]
    [InlineData("git_status", 1, "Used a tool")]
    public void HomogeneousGroupsGetSpecificLabels(string tool, int count, string expected)
    {
        var group = new ToolGroupItem();
        for (var i = 0; i < count; i++)
        {
            group.AddCall(Call(tool));
        }

        group.RefreshTitle();
        Assert.Equal(expected, group.Title);
        Assert.False(group.IsRunning);
    }

    [Fact]
    public void MixedGroupJoinsKindsInFirstAppearanceOrder()
    {
        // The reference: "Edited 2 files, ran 2 commands (1 failed), searched code" —
        // comma-joined, later segments lowercased, failures attached per segment.
        var group = new ToolGroupItem();
        group.AddCall(Call("Edit", args: """{"file_path":"a.cs"}"""));
        group.AddCall(Call("PowerShell"));
        group.AddCall(Call("Edit", args: """{"file_path":"b.cs"}"""));
        group.AddCall(Call("PowerShell", error: true));
        group.AddCall(Call("Grep"));
        group.RefreshTitle();

        Assert.Equal("Edited 2 files, ran 2 commands (1 failed), searched code", group.Title);
    }

    [Fact]
    public void SingleKindFailuresLandInTheTitle()
    {
        var group = new ToolGroupItem();
        group.AddCall(Call("PowerShell"));
        group.AddCall(Call("PowerShell", error: true));
        group.AddCall(Call("PowerShell", denied: true));
        group.RefreshTitle();

        // A denied call reports no outcome at all in the reference's aggregation:
        // the user refused it, so it is not a command that failed. The row still
        // wears the failed treatment, which is what FailedCount counts.
        Assert.Equal("Ran 3 commands (1 failed)", group.Title);
        Assert.Equal(2, group.FailedCount);
        Assert.True(group.HasFailures);
    }

    // The reference's uQ: a run whose calls coalesce into one row is drawn as
    // that row alone — no summary sentence over it, no outline card around it.

    [Fact]
    public void OneCallDrawsAsABareRow()
    {
        var group = new ToolGroupItem();
        var call = group.AddCall(Call("Read", args: """{"file_path":"a/b/Base.xaml"}"""));
        group.RefreshTitle();

        Assert.True(group.RendersBareRow);
        Assert.False(group.RendersCard);
        Assert.Same(call, group.BareRow);
        Assert.True(call.IsBare);
        Assert.False(call.InCard);
        // inGroup is false for a run of exactly one call, which is what sends its
        // images outside the disclosure.
        Assert.False(call.InGroup);
    }

    [Fact]
    public void CoalescedCallsStillDrawBareButCountAsAGroup()
    {
        var group = new ToolGroupItem();
        group.AddCall(Call("Read", args: """{"file_path":"x.cs","offset":1,"limit":10}"""));
        group.AddCall(Call("Read", args: """{"file_path":"x.cs","offset":40,"limit":10}"""));
        group.RefreshTitle();

        Assert.Single(group.Calls);
        Assert.True(group.RendersBareRow);
        // The reference passes inGroup: tools.length > 1 — two calls drawn as one
        // row are still a group of calls.
        Assert.True(group.Calls[0].InGroup);
        Assert.False(group.Calls[0].InCard);
    }

    [Fact]
    public void TwoRowsDrawAsACard()
    {
        var group = new ToolGroupItem();
        group.AddCall(Call("Read", args: """{"file_path":"a.cs"}"""));
        group.AddCall(Call("PowerShell", args: """{"command":"dotnet build"}"""));
        group.RefreshTitle();

        Assert.False(group.RendersBareRow);
        Assert.Null(group.BareRow);
        Assert.All(group.Calls, call =>
        {
            Assert.True(call.InCard);
            Assert.True(call.InGroup);
        });
    }

    [Fact]
    public void ASecondRowPromotesABareRunIntoACard()
    {
        var group = new ToolGroupItem();
        var first = group.AddCall(Call("Read", args: """{"file_path":"a.cs"}"""));
        group.RefreshTitle();
        Assert.True(group.RendersBareRow);

        group.AddCall(Call("Grep", args: """{"pattern":"x"}"""));
        group.RefreshTitle();

        Assert.False(group.RendersBareRow);
        Assert.True(first.InCard);
    }

    [Fact]
    public void ARunWithMemoryOperationsKeepsItsHeader()
    {
        // uQ refuses a bare row for a run carrying memory operations: those
        // clauses live in the header, and a bare row has no header to carry them.
        var group = new ToolGroupItem { MemoryDirectory = "C:/profile/memory/proj" };
        group.AddCall(Call("Read", args: """{"file_path":"C:/profile/memory/proj/MEMORY.md"}"""));
        group.RefreshTitle();

        Assert.False(group.RendersBareRow);
        Assert.Equal("Recalled a memory", group.Title);
    }

    [Fact]
    public void ALoneCallsImagesSitOutsideTheDisclosure()
    {
        var group = new ToolGroupItem();
        var call = group.AddCall(Call("Read", args: """{"file_path":"shot.png"}"""));
        call.Complete("", isError: false,
            images: [new JarvisCode.Core.Models.ImageBlock("image/png", "AAAA")]);
        group.RefreshTitle();

        Assert.True(call.ImagesRideTheRow);
        Assert.False(call.ImagesRideTheBody);
    }

    // The reference's spawnTask bucket: a run of spawn_task calls is counted by
    // what became of the chips (its gE), never mixed with calls of another kind,
    // and a row whose chip started a session says so instead of naming the tool.

    private const string SpawnTask = "mcp__ccd_session__spawn_task";

    private static ToolCallItem Spawned(string taskId, bool error = false)
    {
        var call = new ToolCallItem
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = SpawnTask,
            Description = "spawn_task(Fix the badge)",
            ArgumentsJson = """{"title":"Fix the badge","prompt":"…"}""",
        };
        call.Complete(
            error ? "The queue is full." : $"Noted (position 1, task_id: {taskId}). A chip is showing.",
            error);
        return call;
    }

    [Fact]
    public void AStartedChipIsCountedApartFromTheSuggestedOnes()
    {
        var group = new ToolGroupItem { TaskStarted = id => id == "task_0000aaaa" };
        group.AddCall(Spawned("task_0000aaaa"));
        group.AddCall(Spawned("task_0000bbbb"));
        group.AddCall(Spawned("task_0000cccc"));
        group.RefreshTitle();

        Assert.True(group.IsSpawnTaskRun);
        Assert.Equal("Started a session, suggested 2 tasks", group.Title);
    }

    [Fact]
    public void EveryChipStartedLeavesOnlyTheStartedClause()
    {
        var group = new ToolGroupItem { TaskStarted = _ => true };
        group.AddCall(Spawned("task_0000aaaa"));
        group.AddCall(Spawned("task_0000bbbb"));
        group.RefreshTitle();

        Assert.Equal("Started 2 sessions", group.Title);
    }

    [Fact]
    public void NoChipStartedLeavesOnlyTheSuggestedClause()
    {
        var group = new ToolGroupItem { TaskStarted = _ => false };
        group.AddCall(Spawned("task_0000aaaa"));
        group.RefreshTitle();

        Assert.Equal("Suggested a task", group.Title);
    }

    [Fact]
    public void AFailedSpawnIsCountedAmongTheSuggestions()
    {
        var group = new ToolGroupItem { TaskStarted = _ => false };
        group.AddCall(Spawned("task_0000aaaa"));
        group.AddCall(Spawned("task_0000bbbb", error: true));
        group.RefreshTitle();

        Assert.Equal("Suggested 2 tasks (1 failed)", group.Title);
        Assert.False(group.Segments[0].IsError);
    }

    [Fact]
    public void EveryFailedSpawnColoursTheClause()
    {
        var group = new ToolGroupItem { TaskStarted = _ => false };
        group.AddCall(Spawned("task_0000aaaa", error: true));
        group.AddCall(Spawned("task_0000bbbb", error: true));
        group.RefreshTitle();

        Assert.Equal("Suggested 2 tasks", group.Title);
        Assert.True(group.Segments[0].IsError);
    }

    [Fact]
    public void AStartedChipRenamesItsRow()
    {
        var group = new ToolGroupItem { TaskStarted = id => id == "task_0000aaaa" };
        var started = group.AddCall(Spawned("task_0000aaaa"));
        var queued = group.AddCall(Spawned("task_0000bbbb"));
        group.RefreshTitle();

        Assert.Equal("Started session", started.PrimaryText);
        Assert.NotEqual("Started session", queued.PrimaryText);
    }

    [Fact]
    public void AFailedSpawnNamesNoTask()
    {
        // A failed call's result carries no task id, so it can never read as
        // started — while the call beside it, which did queue one, does.
        var group = new ToolGroupItem { TaskStarted = _ => true };
        var failed = group.AddCall(Spawned("task_0000aaaa", error: true));
        var queued = group.AddCall(Spawned("task_0000bbbb"));
        group.RefreshTitle();

        Assert.Null(failed.VerbOverride);
        Assert.Equal("Started session", queued.VerbOverride);
    }

    [Fact]
    public void ASpawnTaskRunIsNeverMixedWithOtherCalls()
    {
        var pure = new ToolGroupItem();
        pure.AddCall(Spawned("task_0000aaaa"));
        pure.RefreshTitle();
        Assert.True(pure.IsSpawnTaskRun);

        // The run splitting is the view model's; a group handed both kinds must
        // not claim the bucket, or it would take the wrong summary.
        var mixed = new ToolGroupItem();
        mixed.AddCall(Call("Read", args: """{"file_path":"a.cs"}"""));
        mixed.AddCall(Spawned("task_0000aaaa"));
        mixed.RefreshTitle();
        Assert.False(mixed.IsSpawnTaskRun);
        Assert.Equal("Read a.cs, used a tool", mixed.Title);
    }

    [Fact]
    public void ALoneSpawnTaskDrawsBare()
    {
        var group = new ToolGroupItem { TaskStarted = _ => true };
        group.AddCall(Spawned("task_0000aaaa"));
        group.RefreshTitle();

        Assert.True(group.RendersBareRow);
    }

    [Fact]
    public void AGroupWithoutFailuresShowsNoBadge()
    {
        var group = new ToolGroupItem();
        group.AddCall(Call("PowerShell"));
        group.RefreshTitle();

        Assert.Equal(0, group.FailedCount);
        Assert.False(group.HasFailures);
    }

    [Fact]
    public void ListDirectoryGroupsWithReads()
    {
        var reads = new ToolGroupItem();
        reads.AddCall(Call("Read", args: """{"file_path":"a.cs"}"""));
        reads.AddCall(Call("list_directory"));
        reads.RefreshTitle();
        // list_directory is this build's own tool and the reference classifies no
        // such name, so it lands in the unclassified bucket rather than with reads.
        Assert.Equal("Read a.cs, used a tool", reads.Title);
    }

    [Fact]
    public void SingleFileReadsShowTheFilenameAndRanges()
    {
        var group = new ToolGroupItem();
        group.AddCall(Call("Read", args: """{"file_path":"D:\\src\\ChatViewModel.cs","offset":919,"limit":140}"""));
        group.AddCall(Call("Read", args: """{"file_path":"D:\\src\\ChatViewModel.cs","offset":1058,"limit":120}"""));
        group.RefreshTitle();

        // The header names the file; the ranges ride the row, which is where the
        // reference's own Qd puts them.
        Assert.Equal("Read ChatViewModel.cs", group.Title);
        Assert.Equal("(919–1058, 1058–1177)", group.Calls[0].RangeText);
        // Consecutive reads of one file coalesce into a single row.
        Assert.Single(group.Calls);
        Assert.Equal(2, group.AllCalls.Count());
    }

    [Fact]
    public void ReadsWithoutOffsetsShowJustTheFilename()
    {
        var group = new ToolGroupItem();
        group.AddCall(Call("Read", args: """{"file_path":"a/b/Base.xaml"}"""));
        group.RefreshTitle();
        Assert.Equal("Read Base.xaml", group.Title);
    }

    [Fact]
    public void SingleFileEditShowsFilenameAndDiffStats()
    {
        var group = new ToolGroupItem();
        group.AddCall(Done("Edit",
            args: """{"file_path":"x/Base.xaml","old_string":"one\ntwo","new_string":"one\nthree\nfour"}"""));
        group.RefreshTitle();

        Assert.Equal("Edited Base.xaml", group.Title);
        Assert.Equal("+2", group.AddedText);
        Assert.Equal("-1", group.RemovedText);
        Assert.True(group.HasDiffStat);
    }

    [Fact]
    public void CreatedFileCountsAllLinesAsAdditions()
    {
        var group = new ToolGroupItem();
        group.AddCall(Done("Write",
            args: """{"file_path":"new.txt","content":"a\nb\nc"}""",
            result: "Created new.txt (3 lines)."));
        group.RefreshTitle();

        Assert.Equal("Created new.txt", group.Title);
        Assert.Equal("+3", group.AddedText);
        Assert.Equal("", group.RemovedText);
    }

    [Fact]
    public void OverwrittenFileReadsUpdated()
    {
        var group = new ToolGroupItem();
        group.AddCall(Done("Write",
            args: """{"file_path":"old.txt","content":"x"}""",
            result: "Overwrote old.txt (1 lines)."));
        group.RefreshTitle();
        // The header's verb table words the write kind "created" whatever the
        // result says; the row is the half that reads "Updated old.txt".
        Assert.Equal("Created old.txt", group.Title);
        Assert.Equal("Updated", group.Calls[0].Info.Verb);
    }

    [Fact]
    public void EditsOfDifferentFilesDoNotCoalesce()
    {
        var group = new ToolGroupItem();
        group.AddCall(Call("Edit", args: """{"file_path":"a.cs","old_string":"x","new_string":"y"}"""));
        group.AddCall(Call("Edit", args: """{"file_path":"b.cs","old_string":"x","new_string":"y"}"""));
        Assert.Equal(2, group.Calls.Count);
    }

    [Fact]
    public void MixedGroupSumsDiffStatsAcrossKinds()
    {
        var group = new ToolGroupItem();
        group.AddCall(Done("Edit",
            args: """{"file_path":"a.cs","old_string":"one","new_string":"two\nthree"}"""));
        group.AddCall(Call("PowerShell"));
        group.RefreshTitle();

        // One file touched, so its clause collapses onto the basename even with
        // another kind beside it.
        Assert.Equal("Edited a.cs, ran a command", group.Title);
        Assert.Equal("+2", group.AddedText);
        Assert.Equal("-1", group.RemovedText);
    }

    [Fact]
    public void AllFailedBrowserGroupSaysSo()
    {
        var group = new ToolGroupItem();
        group.AddCall(Call("mcp__claude-in-chrome__navigate", error: true));
        group.AddCall(Call("mcp__Claude_Browser__computer", error: true));
        group.RefreshTitle();
        // Every call of the clause failed, so the reference colours the clause and
        // drops the parenthetical rather than counting two out of two.
        Assert.Equal("Used 2 tools", group.Title);
        Assert.True(group.Segments[0].IsError);
    }

    [Fact]
    public void InterruptedCallIsNotAFailure()
    {
        var call = Call("PowerShell", running: true);
        call.Complete("[Request interrupted by user for tool use]", isError: false, interrupted: true);
        Assert.False(call.IsFailed);
        Assert.False(call.IsRunning);
        Assert.True(call.IsInterrupted);
    }

    [Fact]
    public void Thinking_recap_shows_only_in_thinking_view()
    {
        var group = new ToolGroupItem { ThinkingRecap = "I should check the config first." };
        Assert.False(group.RecapVisible);
        group.ShowRecap = true;
        Assert.True(group.RecapVisible);
        group.ThinkingRecap = null;
        Assert.False(group.RecapVisible);
    }

    [Fact]
    public void Queued_message_preview_is_single_line()
    {
        Assert.Equal("fix the bug then run tests",
            new QueuedMessageItem("fix the bug\nthen run tests").Preview);

        // The reference caps nothing here — its row clamps the text to two lines and
        // its collapsed header truncates the one it shows — so the preview carries
        // the whole message and the row does the trimming.
        var longText = new string('x', 200);
        Assert.Equal(200, new QueuedMessageItem(longText).Preview.Length);
    }

    [Fact]
    public void ThinkingCopyTextIsABlockquoteWithTokenEstimate()
    {
        var thinking = new ThinkingItem { Text = "First line\n\nSecond line" };
        var copy = thinking.CopyText;
        Assert.StartsWith("> **Thinking** (~6 tok)\n>\n", copy);
        Assert.Contains("> First line\n>\n> Second line", copy);
    }

    [Fact]
    public void AwaitingApprovalRowsReadAsLive()
    {
        var call = new ToolCallItem
        {
            CallId = "c1",
            ToolName = "Edit",
            Description = "Edit(a.cs)",
            ArgumentsJson = """{"file_path":"src/a.cs","old_string":"x","new_string":"y"}""",
            IsRunning = false,
            IsAwaitingApproval = true,
        };

        // Running wording while the permission card is up, and the group header
        // treats the row as the live one.
        Assert.Equal("Editing", call.PrimaryText);
        Assert.False(call.IsFailed);
        var group = new ToolGroupItem();
        group.AddCall(call);
        group.RefreshTitle();
        Assert.Equal("Editing a.cs", group.Title);
        Assert.True(group.IsRunning);

        // Approval flips it to a normal running row; completion settles it.
        call.IsAwaitingApproval = false;
        call.IsRunning = true;
        Assert.Equal("Editing", call.PrimaryText);
        call.Complete("ok", isError: false);
        Assert.Equal("Edited", call.PrimaryText);
    }

    [Fact]
    public void SubagentEventsBuildANestedTranscript()
    {
        var agent = Call("Agent", running: true, args: """{"prompt":"look around"}""");

        agent.AppendSubagentEvent(new JarvisCode.Core.Agent.ToolExecutionStarted(
            "s1", "Read", "Read(a.cs)", """{"file_path":"src/a.cs"}"""));
        agent.AppendSubagentEvent(new JarvisCode.Core.Agent.ToolExecutionCompleted(
            "s1", "Read", "content", IsError: false));
        agent.AppendSubagentEvent(new JarvisCode.Core.Agent.AssistantMessageCompleted(
            new JarvisCode.Core.Models.ChatMessage(
                JarvisCode.Core.Models.Role.Assistant,
                [new JarvisCode.Core.Models.TextBlock("Found it in a.cs.")])));

        Assert.True(agent.HasSubItems);
        Assert.Equal(2, agent.SubItems.Count);
        var group = Assert.IsType<ToolGroupItem>(agent.SubItems[0]);
        Assert.Equal("Read a.cs", group.Title);
        var nested = Assert.Single(group.Calls);
        Assert.False(nested.IsRunning);
        Assert.Equal("content", nested.Result);
        Assert.Equal("Found it in a.cs.", Assert.IsType<SubagentTextItem>(agent.SubItems[1]).Text);
    }

    [Fact]
    public void BackgroundShellRowTracksItsTask()
    {
        var call = new ToolCallItem
        {
            CallId = "bg1",
            ToolName = "PowerShell",
            Description = "Run dev server",
            ArgumentsJson = """{"command":"npm run dev","run_in_background":true}""",
        };
        call.Complete(
            "Started background task task-3. Poll it with TaskOutput(task_id=\"task-3\"); stop it with TaskStop.",
            isError: false);

        Assert.Equal("task-3", call.BackgroundTaskId);

        // Clean exit reads done; a bad exit code reads failed; killed reads interrupted.
        call.FinishBackground(killed: false, exitCode: 0);
        Assert.False(call.IsRunning);
        Assert.False(call.IsFailed);
        Assert.Null(call.BackgroundTaskId);

        var failing = new ToolCallItem
        {
            CallId = "bg2",
            ToolName = "PowerShell",
            Description = "d",
            ArgumentsJson = """{"command":"x","run_in_background":true}""",
        };
        failing.Complete("Started background task task-4. Poll it.", isError: false);
        failing.FinishBackground(killed: false, exitCode: 2);
        Assert.True(failing.IsError);
        Assert.Contains("[exited with code 2]", failing.Result);

        var killed = new ToolCallItem
        {
            CallId = "bg3",
            ToolName = "PowerShell",
            Description = "d",
            ArgumentsJson = """{"command":"x","run_in_background":true}""",
        };
        killed.Complete("Started background task task-5. Poll it.", isError: false);
        killed.FinishBackground(killed: true, exitCode: null);
        Assert.True(killed.IsInterrupted);
        Assert.False(killed.IsFailed);
    }

    [Fact]
    public void ForegroundShellDoesNotClaimABackgroundTask()
    {
        var call = Done("PowerShell", args: """{"command":"echo hi"}""", result: "hi");
        Assert.Null(call.BackgroundTaskId);
    }

    // ---- Verbose and the run's disclosure ----

    [Fact]
    public void OutsideVerboseTheDisclosureIsThePlainState()
    {
        var run = new ToolGroupItem();
        Assert.False(run.IsExpanded);

        run.IsExpanded = true;
        Assert.True(run.IsExpanded);

        run.IsExpanded = false;
        Assert.False(run.IsExpanded);
    }

    [Fact]
    public void VerboseOpensEveryRun()
    {
        var run = new ToolGroupItem { ForceExpanded = true };
        Assert.True(run.IsExpanded);
    }

    /// <summary>
    /// The reference's handler returns before touching state while verbose is
    /// forcing the run open, so a click there does nothing at all.
    /// </summary>
    [Fact]
    public void ARunCannotBeClosedWhileVerboseIsOn()
    {
        var run = new ToolGroupItem { ForceExpanded = true };

        run.IsExpanded = false;

        Assert.True(run.IsExpanded);
    }

    /// <summary>
    /// And it draws no disclosure while it is forced, because there is nothing
    /// a click could do - the reference renders none there either.
    /// </summary>
    [Fact]
    public void AForcedRunDrawsNoDisclosure()
    {
        var run = new ToolGroupItem();
        Assert.True(run.ShowsDisclosure);

        run.ForceExpanded = true;
        Assert.False(run.ShowsDisclosure);

        run.ForceExpanded = false;
        Assert.True(run.ShowsDisclosure);
    }

    [Fact]
    public void ClickingInVerboseLeavesTheOrdinaryViewAlone()
    {
        var run = new ToolGroupItem();
        run.IsExpanded = true;

        run.ForceExpanded = true;
        run.IsExpanded = false;
        Assert.True(run.IsExpanded);

        run.ForceExpanded = false;
        Assert.True(run.IsExpanded);
    }

    [Fact]
    public void LeavingVerboseRestoresAClosedRunToItsOwnState()
    {
        var run = new ToolGroupItem { ForceExpanded = true };
        run.IsExpanded = true;

        run.ForceExpanded = false;
        Assert.False(run.IsExpanded);
    }

    [Fact]
    public void ReenteringVerboseOpensARunClosedThere()
    {
        var run = new ToolGroupItem { ForceExpanded = true };
        run.IsExpanded = false;

        run.ForceExpanded = false;
        run.ForceExpanded = true;
        Assert.True(run.IsExpanded);
    }

    [Fact]
    public void TheDisclosureAnnouncesEveryChangeItMakes()
    {
        var run = new ToolGroupItem { ForceExpanded = true };
        var seen = 0;
        run.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolGroupItem.IsExpanded))
            {
                seen++;
            }
        };

        // A forced run swallows the click, so nothing is announced.
        run.IsExpanded = false;
        Assert.Equal(0, seen);

        run.ForceExpanded = false;
        seen = 0;

        run.IsExpanded = true;
        Assert.Equal(1, seen);

        // A repeat of the state it is already in is not a change.
        run.IsExpanded = true;
        Assert.Equal(1, seen);
    }
}
