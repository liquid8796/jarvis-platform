using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Tests.ViewModels;

public class ToolCallItemTests
{
    private static ToolCallItem Call(string tool, string? argumentsJson) => new()
    {
        CallId = "c1",
        ToolName = tool,
        Description = "does a thing",
        ArgumentsJson = argumentsJson,
    };

    [Fact]
    public void ShellInputIsTheBareCommandLine()
    {
        var call = Call("PowerShell", """{"command":"  ls -la src/  "}""");

        Assert.True(call.IsCommand);
        Assert.True(call.HasInput);
        Assert.Equal("ls -la src/", call.InputText);
    }

    [Fact]
    public void OtherToolsListTheirArgumentsOnePerLine()
    {
        var call = Call("Read", """{"file_path":"a.txt"}""");

        // Only the shell card prints an input; every other body reaches the
        // arguments through the reference's own "key: value" list instead of a
        // JSON blob, with a path drawn as something to click.
        Assert.False(call.IsCommand);
        Assert.False(call.HasInput);
        var row = Assert.Single(call.InputRows);
        Assert.Equal("file_path", row.Key);
        Assert.Equal("file_path:", row.Label);
        Assert.Equal("a.txt", row.Value);
        Assert.True(row.IsFileRef);
    }

    [Fact]
    public void ArgumentValuesTakeTheFaceTheirKeyEarns()
    {
        var call = Call("Grep", """{"pattern":"a|b","description":"Find it","head_limit":20}""");
        var rows = call.InputRows;

        Assert.Equal(["pattern", "description", "head_limit"], rows.Select(r => r.Key));
        Assert.True(rows[0].IsCode);
        Assert.True(rows[1].IsPlain);
        // A non-string arrives as its JSON, in the code face.
        Assert.True(rows[2].IsCode);
        Assert.Equal("20", rows[2].Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // A tool that takes no arguments arrives as an empty object.
    [InlineData("{}")]
    public void CallsWithoutArgumentsHaveNoInputBlock(string? argumentsJson)
    {
        var call = Call("todo_write", argumentsJson);

        Assert.False(call.HasInput);
        Assert.Equal("", call.InputText);
    }

    [Fact]
    public void MalformedArgumentsAreShownVerbatimRatherThanSwallowed()
    {
        var call = Call("PowerShell", " {not json ");

        Assert.True(call.HasInput);
        Assert.Equal("{not json", call.InputText);
    }

    [Fact]
    public void NonObjectArgumentsAreShownVerbatim()
    {
        var call = Call("PowerShell", "[1,2]");

        Assert.Equal("[1,2]", call.InputText);
    }

    [Fact]
    public void ShellArgumentsWithoutAStringCommandPrintNoCommandLine()
    {
        var call = Call("PowerShell", """{"command":42}""");

        // There is no command to prompt, so the shell card has nothing to draw;
        // the argument still reaches the reader through the argument list.
        Assert.Equal("", call.InputText);
        Assert.False(call.HasInput);
        Assert.Equal("42", Assert.Single(call.InputRows).Value);
    }

    [Fact]
    public void TheShellPromptFollowsTheShell()
    {
        Assert.Equal(">", Call("PowerShell", """{"command":"dir"}""").CommandPrompt);
        Assert.Equal("$", Call("Bash", """{"command":"ls"}""").CommandPrompt);
        Assert.Equal("ls", Call("Bash", """{"command":"ls"}""").InputText);
    }

    [Fact]
    public void ErrorsAndDenialsBothCountAsFailed()
    {
        var errored = Call("PowerShell", null);
        errored.IsError = true;
        Assert.True(errored.IsFailed);

        var denied = Call("PowerShell", null);
        denied.IsDenied = true;
        Assert.True(denied.IsFailed);

        Assert.False(Call("PowerShell", null).IsFailed);
    }

    [Fact]
    public void FailedRaisesChangeNotificationsSoTheRowRecolours()
    {
        var call = Call("PowerShell", null);
        var changed = new List<string?>();
        call.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        call.IsError = true;
        call.IsDenied = true;
        call.Result = "boom";

        Assert.Equal(2, changed.Count(name => name == nameof(ToolCallItem.IsFailed)));
        Assert.Contains(nameof(ToolCallItem.HasResult), changed);
    }

    [Fact]
    public void ResultDrivesTheOutputBlock()
    {
        var call = Call("PowerShell", null);
        Assert.False(call.HasResult);

        call.Result = "ok";
        Assert.True(call.HasResult);
    }
}
