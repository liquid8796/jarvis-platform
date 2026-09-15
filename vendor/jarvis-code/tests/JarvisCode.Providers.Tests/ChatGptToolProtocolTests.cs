using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.Providers.Tests;

/// <summary>
/// The web session carries text and nothing else, so a tool call is a line the model writes and
/// this parser reads back. What it must never do is invent a call out of something malformed:
/// that would run a command on the user's machine that the model did not ask for.
/// </summary>
public sealed class ChatGptToolProtocolParsingTests
{
    [Fact]
    public void AnActionOnItsOwnIsTheWholeReply()
    {
        var content = ChatGptToolProtocol.Parse(
            """JARVIS_ACT {"name":"Read","arguments":{"path":"README.md"}}""");

        var action = Assert.Single(content.Actions);
        Assert.Equal("Read", action.Name);
        Assert.Equal("""{"path":"README.md"}""", action.ArgumentsJson);
        Assert.Equal("", content.Text);
    }

    [Fact]
    public void ProseAroundAnActionIsKeptEntirelyAsText()
    {
        const string reply = """
            Let me look at that file.
            JARVIS_ACT {"name":"Read","arguments":{"path":"a.txt"}}
            """;
        var content = ChatGptToolProtocol.Parse(reply);

        Assert.Empty(content.Actions);
        Assert.Equal(reply, content.Text);
    }

    [Fact]
    public void BracesAndQuotesInsideTheArgumentsSurvive()
    {
        var content = ChatGptToolProtocol.Parse(
            """JARVIS_ACT {"name":"PowerShell","arguments":{"command":"echo \"{ }\" && ls"}}""");

        var action = Assert.Single(content.Actions);
        Assert.Equal("PowerShell", action.Name);
        Assert.Equal("echo \"{ }\" && ls", JsonNode.Parse(action.ArgumentsJson)!["command"]!.GetValue<string>());
    }

    [Fact]
    public void TwoActionsInOneReplyBothCome()
    {
        var content = ChatGptToolProtocol.Parse(
            """
            JARVIS_ACT {"name":"Read","arguments":{"path":"a.txt"}}
            JARVIS_ACT {"name":"Read","arguments":{"path":"b.txt"}}
            """);

        Assert.Equal(2, content.Actions.Count);
        Assert.Equal("""{"path":"a.txt"}""", content.Actions[0].ArgumentsJson);
        Assert.Equal("""{"path":"b.txt"}""", content.Actions[1].ArgumentsJson);
    }

    [Theory]
    // An explicit but malformed action must fail before any part of its batch can dispatch.
    [InlineData("""JARVIS_ACT {"name":"Read","arguments":{"path":"a.txt" """)]
    [InlineData("JARVIS_ACT please read the file")]
    [InlineData("""JARVIS_ACT {"arguments":{"path":"a.txt"}}""")]
    [InlineData("""JARVIS_ACT {"name":"","arguments":{}}""")]
    public void AMalformedActionFailsWithoutDispatch(string reply)
    {
        var failure = Assert.Throws<ProviderException>(() => ChatGptToolProtocol.Parse(reply));
        Assert.Contains("No actions were run", failure.Message);
    }

    [Fact]
    public void AnOrdinaryAnswerIsLeftAlone()
    {
        var content = ChatGptToolProtocol.Parse("README.md documents the engine.");

        Assert.Empty(content.Actions);
        Assert.Equal("README.md documents the engine.", content.Text);
    }

    [Fact]
    public void AMissingArgumentsObjectIsRejected()
        => Assert.Throws<ProviderException>(() =>
            ChatGptToolProtocol.Parse("""JARVIS_ACT {"name":"git_status"}"""));

    [Fact]
    public void WhatIsRenderedParsesBackToWhatWentIn()
    {
        var line = ChatGptToolProtocol.RenderCall("PowerShell", """{"command":"dir"}""");

        var action = Assert.Single(ChatGptToolProtocol.Parse(line).Actions);
        Assert.Equal("PowerShell", action.Name);
        Assert.Equal("""{"command":"dir"}""", action.ArgumentsJson);
    }
}

public sealed class ChatGptToolResultRenderingTests
{
    private static ToolResultBlock Result(string content, bool isError = false, int images = 0) =>
        new("id", "Read", content, isError,
            images == 0 ? null : [.. Enumerable.Range(0, images).Select(_ => new ImageBlock("image/png", "x"))]);

    [Fact]
    public void AResultCarriesTheToolItCameFrom()
        => Assert.Equal("RESULT Read: hello", ChatGptToolProtocol.RenderResult(Result("hello")));

    [Fact]
    public void AFailureSaysSo()
        => Assert.StartsWith("RESULT Read FAILED:", ChatGptToolProtocol.RenderResult(Result("nope", isError: true)));

    [Fact]
    public void ALongResultReachesTheModelCompletely()
    {
        var body = new string('x', 20_000) + "\nLAST LINE MUST SURVIVE";
        var rendered = ChatGptToolProtocol.RenderResult(Result(body));
        Assert.Equal("RESULT Read: " + body, rendered);
    }

    /// <summary>
    /// The pictures themselves now ride the message as attachments, so the result only ties them
    /// back to the call that produced them; whether they arrived is the message's own business.
    /// </summary>
    [Fact]
    public void ImagesAreMarkedWhereTheyBelong()
        => Assert.Contains("2 image(s) from this call", ChatGptToolProtocol.RenderResult(Result("shot", images: 2)));

    [Fact]
    public void APictureTheChannelCannotCarryIsDeclaredMissing()
        => Assert.Equal("[2 image(s) omitted: not attached to this message]", ChatGptToolProtocol.ImagesOmitted(2));

    [Fact]
    public void NoPicturesNeedNoNote()
        => Assert.Equal("", ChatGptToolProtocol.ImagesOmitted(0));
}

/// <summary>
/// The instruction block is the part that decides whether any of this works: asked for its tools
/// plainly, the model answers that it cannot reach the workspace. These pin the three things that
/// turned the refusal into compliance.
/// </summary>
public sealed class ChatGptToolInstructionTests
{
    private static readonly ToolDefinition[] Tools =
    [
        new("Read", "Read a file from the workspace.\n\nPaths are workspace-relative.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
        }),
    ];

    [Fact]
    public void TheContractNamesBothShapesAReplyMayTake()
    {
        var text = ChatGptToolProtocol.Instructions(Tools);

        Assert.Contains("Exactly two replies are valid", text);
        Assert.Contains("JARVIS_ACT {\"name\":\"<action>\"", text);
        Assert.Contains("final answer with no action line", text);
    }

    [Fact]
    public void RefusingIsNamedAProtocolError()
        => Assert.Contains("protocol error", ChatGptToolProtocol.Instructions(Tools));

    /// <summary>
    /// Asked to delete a Vercel project, ChatGPT reached for its own connector and ended the turn
    /// on a card waiting to be clicked — its side of the wire cannot touch this machine, so that is
    /// the work stopping rather than happening. Naming the shell as the way out sends the same
    /// request to the user's own CLI instead (checked against chatgpt.com on 2026-08-29: the turn
    /// that used to ask for Vercel now reads .vercel/project.json and runs "vercel project rm").
    /// </summary>
    [Fact]
    public void WorkBeyondTheMachineIsPointedAtTheShellNotAConnector()
    {
        var text = ChatGptToolProtocol.Instructions(Tools);

        Assert.Contains("program's installed CLI or approved MCP actions", text);
        Assert.Contains("cannot touch anything here", text);
        Assert.Contains("authentication through those actions", text);
        Assert.DoesNotContain("credentials already signed in", text);
    }

    [Fact]
    public void InitialAndContinuationContractsRouteWorkspaceWorkThroughJarvisOnly()
    {
        foreach (var text in new[] { ChatGptToolProtocol.Instructions(Tools), ChatGptToolProtocol.Closing() })
        {
            Assert.Contains("JARVIS ONLY", text);
            Assert.Contains("JARVIS_ACT", text);
            Assert.Contains("final response text", text);
            Assert.Contains("RESULT", text);
        }
    }

    [Fact]
    public void AFailedReadOrToolSearchCannotAuthorizeSwitchingToTheWebsRuntime()
    {
        var initial = ChatGptToolProtocol.Instructions(Tools);
        var closing = ChatGptToolProtocol.Closing();

        Assert.Contains("A failed Read", initial);
        Assert.Contains("ToolSearch only loads an action schema", initial);
        Assert.Contains("do not suggest ChatGPT plugin installs", initial);
        Assert.Contains("not permission to switch execution environments", closing);
        Assert.Contains("ToolSearch returns schemas", closing);
        Assert.Contains("Earlier assistant prose is not execution evidence", closing);
        Assert.Contains("sandbox:/ or /mnt/data", closing);
    }

    /// <summary>
    /// The example teaches harder than the schemas do: an argument name it invents is one the model
    /// then calls with. This parses the block's own example back through the parser and checks it
    /// against the tool it names.
    /// </summary>
    [Fact]
    public void TheWorkedExampleCallsTheToolTheWayItsSchemaReads()
    {
        var readFile = new ToolDefinition("Read", "Reads a text file.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["file_path"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("file_path"),
        });

        var example = ChatGptToolProtocol.Instructions([readFile])
            .Split('\n')
            .Single(line => line.StartsWith(ChatGptToolProtocol.Marker + " {\"name\":\"Read\"", StringComparison.Ordinal));
        var call = Assert.Single(ChatGptToolProtocol.Parse(example).Actions);

        Assert.Equal("Read", call.Name);
        Assert.NotNull(JsonNode.Parse(call.ArgumentsJson)!["file_path"]);
    }

    [Fact]
    public void AWorkedExchangeIsShownInTranscriptForm()
    {
        var text = ChatGptToolProtocol.Instructions(Tools);

        Assert.Contains("User: what is in notes.txt?", text);
        Assert.Contains("Assistant reply (send only the next line, without this label):", text);
        Assert.Contains("JARVIS_ACT {\"name\":\"Read\",\"arguments\":{\"path\":\"notes.txt\"}}", text);
        Assert.Contains("User: RESULT Read: hello world", text);
    }

    /// <summary>
    /// The browser model receives the same description and input constraints an API model does.
    /// </summary>
    [Fact]
    public void EveryToolIsListedWithItsArguments()
    {
        var text = ChatGptToolProtocol.Instructions(Tools);

        Assert.Contains("Read", text);
        Assert.Contains(Tools[0].Description, text);
        Assert.Contains("arguments (JSON Schema): " + Tools[0].InputSchema.ToJsonString(), text);
    }

    [Fact]
    public void EveryParagraphOfALongDescriptionIsCarried()
    {
        var wordy = new ToolDefinition("PowerShell", new string('d', 900) + "\n\nRequired final instructions.", new JsonObject());

        var text = ChatGptToolProtocol.Instructions([wordy]);

        Assert.Contains(wordy.Description, text);
    }
}
