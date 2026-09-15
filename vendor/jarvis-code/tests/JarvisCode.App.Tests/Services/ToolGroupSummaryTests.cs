using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The clauses of a tool group's header, against the reference's own rules —
/// the ones a sentence that merely reads the same would get wrong.
/// </summary>
public class ToolGroupSummaryTests
{
    private static ToolCallItem Call(string tool, string? args = null, bool error = false, bool denied = false)
    {
        var call = new ToolCallItem
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = tool,
            Description = tool,
            ArgumentsJson = args,
            IsRunning = false,
        };
        call.IsError = error;
        call.IsDenied = denied;
        return call;
    }

    private static string Sentence(IEnumerable<ToolCallItem> calls, string? memory = null) =>
        ToolGroupSummary.Sentence(ToolGroupSummary.Build([.. calls.Cast<IToolSummaryCall>()], memory));

    [Fact]
    public void AClauseCountsFilesNotCalls()
    {
        // Reading one file twice is one file: the reference keys a kind's count on
        // the path it touched, so a re-read does not inflate it.
        var sentence = Sentence(
        [
            Call("Read", """{"file_path":"/a/x.cs","offset":1,"limit":10}"""),
            Call("Read", """{"file_path":"/a/x.cs","offset":40,"limit":10}"""),
        ]);

        Assert.Equal("Read x.cs", sentence);
    }

    [Fact]
    public void VerbsCollapseOntoTheOneFileTheyAllTouched()
    {
        var sentence = Sentence(
        [
            Call("Read", """{"file_path":"/a/x.cs"}"""),
            Call("Edit", """{"file_path":"/a/x.cs"}"""),
        ]);

        Assert.Equal("Read and edited x.cs", sentence);
    }

    [Fact]
    public void ThreeVerbsOnOneFileTakeTheOxfordComma()
    {
        var sentence = Sentence(
        [
            Call("Read", """{"file_path":"/a/x.cs"}"""),
            Call("Edit", """{"file_path":"/a/x.cs"}"""),
            Call("Write", """{"file_path":"/a/x.cs"}"""),
        ]);

        Assert.Equal("Read, edited, and created x.cs", sentence);
    }

    [Fact]
    public void TwoFilesLeaveTheClausesApart()
    {
        var sentence = Sentence(
        [
            Call("Read", """{"file_path":"/a/x.cs"}"""),
            Call("Edit", """{"file_path":"/a/y.cs"}"""),
        ]);

        Assert.Equal("Read a file, edited a file", sentence);
    }

    [Fact]
    public void APartialFailureIsCountedAndAWholeOneIsColoured()
    {
        var partial = ToolGroupSummary.Build(
        [
            Call("PowerShell"),
            Call("PowerShell", error: true),
        ]);
        Assert.Equal("ran 2 commands (1 failed)", partial[0].Text);
        Assert.False(partial[0].IsError);

        var whole = ToolGroupSummary.Build(
        [
            Call("PowerShell", error: true),
            Call("PowerShell", error: true),
        ]);
        Assert.Equal("ran 2 commands", whole[0].Text);
        Assert.True(whole[0].IsError);
    }

    [Fact]
    public void ADeniedCallReportsNoOutcome()
    {
        var segments = ToolGroupSummary.Build([Call("PowerShell", denied: true)]);
        Assert.Equal("ran a command", segments[0].Text);
        Assert.False(segments[0].IsError);
    }

    [Fact]
    public void MemoryOperationsLeadTheSentence()
    {
        var memory = @"C:\profile\memory\proj";
        var sentence = Sentence(
        [
            Call("Read", """{"file_path":"C:\\profile\\memory\\proj\\MEMORY.md"}"""),
            Call("Write", """{"file_path":"C:\\profile\\memory\\proj\\a-fact.md"}"""),
            Call("PowerShell", """{"command":"dotnet build"}"""),
        ], memory);

        Assert.Equal("Recalled a memory, saved a memory, ran a command", sentence);
    }

    [Fact]
    public void AFileOutsideTheMemoryFolderIsAnOrdinaryFile()
    {
        var sentence = Sentence(
            [Call("Write", """{"file_path":"C:\\src\\a.cs"}""")], @"C:\profile\memory\proj");

        Assert.Equal("Created a.cs", sentence);
    }

    [Theory]
    [InlineData("PowerShell", "bash")]
    [InlineData("Bash", "bash")]
    [InlineData("MultiEdit", "edit")]
    [InlineData("NotebookEdit", "notebook_edit")]
    [InlineData("WebSearch", "web")]
    [InlineData("Agent", "task")]
    [InlineData("todo_write", "todo")]
    [InlineData("ExitPlanMode", "exit_plan_mode")]
    [InlineData("Skill", "skill")]
    [InlineData("mcp__claude-in-chrome__navigate", "browser")]
    [InlineData("list_directory", null)]
    public void ToolNamesResolveToTheReferencesKinds(string tool, string? expected) =>
        Assert.Equal(expected, ToolGroupSummary.KindOf(tool));

    [Fact]
    public void ToolSearchIsAKindOnlyUnderItsOwnName()
    {
        Assert.Equal("tool_search", ToolGroupSummary.KindOf("tool_search"));
        Assert.Null(ToolGroupSummary.KindOf("mcp__something__tool_search"));
    }
}
