using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.Providers.Tests;


public sealed class ChatGptCookieJarTests
{
    private const string SessionCookie = ChatGptCookieJar.SessionTokenName;

    [Fact]
    public void ReadsTheCookieEditorObjectExport()
    {
        var jar = ChatGptCookieJar.Parse($$"""
            {"url":"https://chatgpt.com","cookies":[
              {"name":"{{SessionCookie}}","value":"abc","domain":".chatgpt.com","path":"/",
               "secure":true,"httpOnly":true,"sameSite":"lax","expirationDate":1800000000},
              {"name":"cf_clearance","value":"xyz","domain":".chatgpt.com","path":"/","secure":true}
            ]}
            """);

        Assert.Equal(2, jar.Count);
        Assert.True(jar.IsUsable);
        var session = jar.Cookies[0];
        Assert.Equal("abc", session.Value);
        Assert.True(session.Secure);
        Assert.True(session.HttpOnly);
        Assert.Equal("lax", session.SameSite);
        Assert.Equal(1800000000, session.ExpirationDate);
    }

    [Fact]
    public void ReadsABareArrayExport()
    {
        var jar = ChatGptCookieJar.Parse($$"""[{"name":"{{SessionCookie}}","value":"abc"}]""");

        Assert.True(jar.IsUsable);
        Assert.Equal(ChatGptCookie.DefaultDomain, jar.Cookies[0].Domain);
        Assert.Equal("/", jar.Cookies[0].Path);
    }

    [Fact]
    public void ReadsAHeaderLine()
    {
        var jar = ChatGptCookieJar.Parse($"{SessionCookie}=abc; cf_clearance=xyz");

        Assert.Equal(2, jar.Count);
        Assert.True(jar.IsUsable);
        Assert.Equal($"{SessionCookie}=abc; cf_clearance=xyz", jar.ToHeader());
    }

    [Fact]
    public void ReadsNetscapeCookiesTxt()
    {
        var jar = ChatGptCookieJar.Parse(
            "# Netscape HTTP Cookie File\n"
            + $".chatgpt.com\tTRUE\t/\tTRUE\t1800000000\t{SessionCookie}\tabc\n"
            + ".chatgpt.com\tTRUE\t/\tTRUE\t0\tcf_clearance\txyz\n");

        Assert.Equal(2, jar.Count);
        Assert.True(jar.IsUsable);
        Assert.True(jar.Cookies[0].Secure);
        Assert.Equal(1800000000, jar.Cookies[0].ExpirationDate);
    }

    [Fact]
    public void ChunkedSessionTokensStillCount()
    {
        var jar = ChatGptCookieJar.Parse($"{SessionCookie}.0=first; {SessionCookie}.1=second");
        Assert.True(jar.HasSessionToken);
    }

    [Fact]
    public void CookiesWithoutTheSessionTokenAreNotUsable()
    {
        var jar = ChatGptCookieJar.Parse("cf_clearance=xyz; oai-did=123");

        Assert.False(jar.IsUsable);
        Assert.Contains(SessionCookie, jar.Describe());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json {{{")]
    public void MalformedInputYieldsAnEmptyJarRatherThanThrowing(string raw)
    {
        var jar = ChatGptCookieJar.Parse(raw);
        Assert.False(jar.IsUsable);
    }

    [Fact]
    public void DescribeNeverQuotesACookieValue()
    {
        var jar = ChatGptCookieJar.Parse($"{SessionCookie}=super-secret-value");

        Assert.DoesNotContain("super-secret-value", jar.Describe());
        Assert.Contains("session token present", jar.Describe());
    }

    [Fact]
    public void InjectionJsonKeepsTheScopingABrowserNeeds()
    {
        var jar = ChatGptCookieJar.Parse($$"""
            [{"name":"{{SessionCookie}}","value":"abc","domain":".chatgpt.com","path":"/api",
              "secure":true,"httpOnly":true,"sameSite":"no_restriction","expirationDate":123}]
            """);

        var cookie = JsonNode.Parse(jar.ToInjectionJson())!.AsArray()[0]!;
        Assert.Equal(".chatgpt.com", cookie["domain"]!.GetValue<string>());
        Assert.Equal("/api", cookie["path"]!.GetValue<string>());
        Assert.True(cookie["secure"]!.GetValue<bool>());
        Assert.True(cookie["httpOnly"]!.GetValue<bool>());
        Assert.Equal("no_restriction", cookie["sameSite"]!.GetValue<string>());
        Assert.Equal(123, cookie["expirationDate"]!.GetValue<double>());
    }
}

public sealed class ChatGptProjectTests
{
    /// <summary>
    /// The shape the live sidebar answers with, captured from chatgpt.com on 2026-09-04: the entry
    /// is <c>items[].gizmo</c>, and the name is under <c>display</c> rather than beside the id. The
    /// fixture this file used to carry put a "name" next to the id, which no ChatGPT account has
    /// ever sent — so the tests passed while every real project read as nameless.
    /// </summary>
    private const string Sidebar = """
        {"items":[
          {"gizmo":{"id":"g-p-aaa111","organization_id":"org-1","instructions":"",
                    "display":{"name":"Jarvis Test","description":"","emoji":null}}},
          {"gizmo":{"id":"g-p-bbb222","organization_id":"org-1",
                    "display":{"name":"Other project","description":""}}},
          {"gizmo":{"id":"g-zzz999","display":{"name":"Not a project"}}}
        ]}
        """;

    [Fact]
    public void ProjectsAreFoundWhereverTheySitInThePayload()
    {
        var found = ChatGptWebProvider.Projects(JsonNode.Parse(Sidebar)!).ToList();

        Assert.Equal(["g-p-aaa111", "g-p-bbb222"], found.Select(p => p.Id));
        Assert.Equal("Jarvis Test", found[0].Name);
    }

    [Fact]
    public void OnlyProjectIdsCount()
    {
        // A custom GPT is a gizmo too, but "g-" without "g-p-" is not a project.
        var found = ChatGptWebProvider.Projects(JsonNode.Parse(Sidebar)!);
        Assert.DoesNotContain("g-zzz999", found.Select(p => p.Id));
    }

    [Fact]
    public void TheCreatedProjectIsReadOutOfTheResponse()
    {
        var created = """{"resource":{"kind":"project"},"gizmo":{"id":"g-p-new777","name":"Fresh"}}""";
        Assert.Equal("g-p-new777", ChatGptWebProvider.Projects(JsonNode.Parse(created)!).First().Id);
    }

    /// <summary>
    /// A name beside the id is still read — the create response answers in that shape — so the
    /// lookup takes whichever of the two the payload happens to use.
    /// </summary>
    [Fact]
    public void ANameBesideTheIdIsStillTheName()
    {
        var flat = """{"gizmo":{"id":"g-p-flat","name":"Beside the id"}}""";
        Assert.Equal("Beside the id", ChatGptWebProvider.Projects(JsonNode.Parse(flat)!).First().Name);
    }

    /// <summary>
    /// A project whose name this app cannot read is one it cannot match, and matching nothing is
    /// what makes it create a second project of the same name — so it must not also throw.
    /// </summary>
    [Fact]
    public void AProjectWithNoReadableNameIsNamelessRatherThanAThrow()
    {
        var odd = """{"gizmo":{"id":"g-p-odd","name":42,"display":{"name":["not","a","string"]}}}""";
        Assert.Null(ChatGptWebProvider.Projects(JsonNode.Parse(odd)!).First().Name);
    }
}

public sealed class ChatGptRotationOptionsTests
{
    private sealed record Options(string ChatGptProjectName, int ChatGptRotateAfterMessages) : IChatGptWebOptions
    {
        public string ChatGptProjectId => "";

        public void SaveChatGptProjectId(string projectId)
        {
        }
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(20, 20)]
    [InlineData(19, 20)]
    [InlineData(0, 20)]
    [InlineData(-5, 20)]
    public void TheThresholdNeverDropsBelowTheFloor(int configured, int expected)
        => Assert.Equal(expected, ((IChatGptWebOptions)new Options("", configured)).ResolveRotateAfterMessages());

    [Fact]
    public void TheDefaultIsAHundred()
        => Assert.Equal(100, IChatGptWebOptions.DefaultRotateAfterMessages);

    [Fact]
    public void SettingsShipTheSameDefault()
        // Core cannot reference the provider, so the number is written in both places.
        => Assert.Equal(
            IChatGptWebOptions.DefaultRotateAfterMessages,
            new JarvisCode.Core.Settings.AppSettings().ChatGptRotateAfterMessages);
}

public sealed class ChatGptPromptTests
{
    private static LlmRequest Request(params (Role Role, string Text)[] turns) => new()
    {
        ModelId = "auto",
        SystemPrompt = "be helpful",
        Messages = [.. turns.Select(t => new ChatMessage(t.Role, [new TextBlock(t.Text)]))],
    };

    [Fact]
    public void AFreshChatIsGivenTheWholeConversation()
    {
        var text = ChatGptWebProvider.FlattenHistory(
            Request((Role.User, "first"), (Role.Assistant, "answer"), (Role.User, "second")));

        Assert.StartsWith("be helpful", text);
        Assert.Contains("User: first", text);
        Assert.Contains("Assistant: answer", text);
        Assert.EndsWith("=== CURRENT MESSAGE ===\nsecond", text);
    }

    [Fact]
    public void LeadingSystemBlocksPrecedeTheMainPromptInOrder()
    {
        var request = Request((Role.User, "hello")) with
        {
            LeadingSystemBlocks = [new("identity"), new("reporting outcomes", Cached: true)],
        };

        Assert.StartsWith("identity\n\nreporting outcomes\n\nbe helpful\n\n",
            ChatGptWebProvider.FlattenHistory(request));
    }

    [Fact]
    public void LoadedSkillInstructionsRemainCompleteAndAppearOnce()
    {
        var body = "START-SKILL\n" + new string('x', 12_000) + "\nEND-SKILL";
        var result = new ToolResultBlock("c1", "Skill", "Launching skill: long-skill", false)
        {
            FollowUpText = body,
        };
        var request = Request() with { Messages = [ChatMessage.FromToolResults([result])] };

        var prompt = ChatGptWebProvider.FlattenHistory(request);

        Assert.Contains(body, prompt);
        Assert.Equal(1, prompt.Split("START-SKILL", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("cut here", prompt);
    }

    [Fact]
    public void IdenticalSkillFollowUpsKeepTheirIndividualOccurrences()
    {
        var result = new ToolResultBlock("c1", "Skill", "ok", false) { FollowUpText = "skill-body" };
        var request = Request() with
        {
            Messages = [ChatMessage.FromToolResults([result, result with { ToolCallId = "c2" }])],
        };

        Assert.Equal(2, ChatGptWebProvider.FlattenHistory(request)
            .Split("skill-body", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ToolsAreCarriedAsTheTextProtocolRatherThanDeclaredAbsent()
    {
        var request = Request((Role.User, "edit my file")) with
        {
            Tools = [new ToolDefinition("Edit", "edits", new JsonObject())],
        };

        var text = ChatGptWebProvider.FlattenHistory(request);

        Assert.Contains("JARVIS_ACT", text);
        Assert.Contains("Edit", text);
        Assert.DoesNotContain("no tools in this mode", text);
    }

    [Fact]
    public void AToollessRequestSaysNothingAboutActions()
        => Assert.DoesNotContain("JARVIS_ACT", ChatGptWebProvider.FlattenHistory(Request((Role.User, "hello"))));

    [Fact]
    public void AContinuingChatOnlySendsTheNewMessage()
    {
        var request = Request((Role.User, "first"), (Role.Assistant, "answer"), (Role.User, "second"));
        Assert.Equal("second", ChatGptWebProvider.LatestTurnText(request));
    }

    /// <summary>
    /// A message holding only a tool call or only its result has no text of its own. Rendering them
    /// is what keeps the model from being asked to carry on about a tool it never saw run.
    /// </summary>
    [Fact]
    public void AToolCallAndItsResultAreBothInTheTranscript()
    {
        var request = new LlmRequest
        {
            ModelId = "auto",
            SystemPrompt = "be helpful",
            Messages =
            [
                new ChatMessage(Role.User, [new TextBlock("what is in a.txt?")]),
                new ChatMessage(Role.Assistant, [new ToolCallBlock("c1", "Read", """{"path":"a.txt"}""")]),
                new ChatMessage(Role.User, [new ToolResultBlock("c1", "Read", "hello world", false)]),
            ],
        };

        var text = ChatGptWebProvider.FlattenHistory(request);

        Assert.Contains("""Assistant: JARVIS_ACT {"name":"Read","arguments":{"path":"a.txt"}}""", text);
        Assert.Contains("=== CURRENT MESSAGE ===", text);
        Assert.EndsWith("RESULT Read: hello world", text);
    }

    [Fact]
    public void TheNewestTurnOfAResumedChatIsTheToolResult()
    {
        var request = new LlmRequest
        {
            ModelId = "auto",
            SystemPrompt = "",
            Messages =
            [
                new ChatMessage(Role.User, [new TextBlock("go")]),
                new ChatMessage(Role.Assistant, [new ToolCallBlock("c1", "PowerShell", """{"command":"dir"}""")]),
                new ChatMessage(Role.User, [new ToolResultBlock("c1", "PowerShell", "a.txt", false)]),
            ],
        };

        Assert.Equal("RESULT PowerShell: a.txt", ChatGptWebProvider.LatestTurnText(request));
    }
}

public sealed class ChatGptFailureMessageTests
{
    [Fact]
    public void ATransportFailureKeepsTheReasonItCameWith()
    {
        // "HTTP -1" tells nobody anything; the body is the sentence worth showing.
        var response = new ChatGptResponse(
            ChatGptResponse.TransportFailure, "The ChatGPT window was closed before the session was ready.");

        Assert.Equal("The ChatGPT window was closed before the session was ready.", ChatGptWebProvider.Summarize(response));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void ARejectedSessionPointsAtTheCookies(int status)
        => Assert.Contains("cookies", ChatGptWebProvider.Summarize(new ChatGptResponse(status, "")));

    [Fact]
    public void ARateLimitSaysSo()
        => Assert.Contains("rate-limited", ChatGptWebProvider.Summarize(new ChatGptResponse(429, "")));

    /// <summary>
    /// The transcript's error card places a failure by the words in it. These are the words that
    /// put each one where it belongs instead of in the card's catch-all, which offers the wrong
    /// thing to try — see ChatGptErrorCardTests for the card actually reading them.
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void ARejectedSessionSaysItFailedToAuthenticate(int status)
        => Assert.Contains(
            "failed to authenticate",
            ChatGptWebProvider.Summarize(new ChatGptResponse(status, "")),
            StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ARateLimitNamesTheLimitItMayHaveHit()
        => Assert.Contains("hit your usage limit", ChatGptWebProvider.Summarize(new ChatGptResponse(429, "")));

    [Fact]
    public void APageThatTimedOutSaysTheRequestTimedOut()
        => Assert.Equal(
            "Request timed out. the ChatGPT page did not open https://chatgpt.com/.",
            ChatGptWebProvider.Classified("TIMEOUT: the ChatGPT page did not open https://chatgpt.com/."));

    /// <summary>A marker inside the sentence stays where it is; only an opening one is folded in.</summary>
    [Fact]
    public void ATimeoutReportedMidSentenceKeepsItsOwnWords()
        => Assert.Equal(
            "Request timed out. the browser answered TIMEOUT while starting",
            ChatGptWebProvider.Classified("the browser answered TIMEOUT while starting"));

    [Fact]
    public void ASignedOutBrowserSaysItFailedToAuthenticate()
        => Assert.StartsWith(
            "Failed to authenticate.",
            ChatGptWebProvider.Classified("The embedded browser is not signed in to ChatGPT."));

    [Fact]
    public void AChallengeSaysTheSameThing()
        => Assert.StartsWith(
            "Failed to authenticate.",
            ChatGptWebProvider.Classified("Cloudflare is challenging this session."));

    /// <summary>Anything else is left exactly as it was written.</summary>
    [Fact]
    public void AnOrdinaryFailureIsNotDressedUp()
        => Assert.Equal(
            "the send did not take — ChatGPT never started an answer",
            ChatGptWebProvider.Classified("the send did not take — ChatGPT never started an answer"));
}

/// <summary>
/// Reading the answer off the page loses exactly what a coding assistant cannot afford to lose:
/// the rendered markdown eats backslash escapes and collapses runs of spaces, so a file's contents
/// come back mangled and the JSON around them stops parsing. These pin the walk that reads the
/// turn out of the stored conversation instead.
/// </summary>
public sealed class ChatGptExactAnswerTests
{
    private const string Prompt = "what is in package.json?";

    /// <summary>As <see cref="Conversation"/>, for turns where a message also carries a file.</summary>
    private static string ConversationWithAssets(
        string currentNode,
        params (string Id, string? Parent, string? Role, string? Type, string? Text, string? AssetPointer)[] nodes)
    {
        var mapping = new JsonObject();
        foreach (var (id, parent, role, type, text, assetPointer) in nodes)
        {
            var entry = new JsonObject { ["parent"] = parent };
            if (role is not null)
            {
                var parts = new JsonArray();
                if (assetPointer is not null)
                {
                    parts.Add(new JsonObject
                    {
                        ["content_type"] = "image_asset_pointer",
                        ["asset_pointer"] = assetPointer,
                    });
                }

                if (text is not null)
                {
                    parts.Add(text);
                }

                entry["message"] = new JsonObject
                {
                    ["author"] = new JsonObject { ["role"] = role },
                    ["content"] = new JsonObject
                    {
                        ["content_type"] = type,
                        ["parts"] = parts,
                    },
                };
            }

            mapping[id] = entry;
        }

        return new JsonObject { ["current_node"] = currentNode, ["mapping"] = mapping }.ToJsonString();
    }

    /// <summary>The shape most of these want: nodes carrying words and no file.</summary>
    private static string Conversation(
        string currentNode,
        params (string Id, string? Parent, string? Role, string? Type, string? Text)[] nodes)
        => ConversationWithAssets(
            currentNode,
            [.. nodes.Select(n => (n.Id, n.Parent, n.Role, n.Type, n.Text, (string?)null))]);

    /// <summary>The turn that showed the bug: a file's contents, escapes and indentation intact.</summary>
    private const string Action =
        """JARVIS_ACT {"name":"Write","arguments":{"path":"package.json","content":"{\n  \"name\": \"app\"\n}"}}""";

    [Fact]
    public void TheTurnComesBackAsItWasWritten()
    {
        var json = Conversation(
            "n5",
            ("n0", null, null, null, null),
            ("n1", "n0", "user", "text", Prompt),
            ("n2", "n1", "assistant", "thoughts", null),
            ("n3", "n2", "assistant", "text", "Writing it now."),
            ("n4", "n3", "assistant", "reasoning_recap", null),
            ("n5", "n4", "assistant", "text", Action));

        Assert.Equal($"Writing it now.\n\n{Action}", ChatGptWebProvider.ExactAnswer(json, 1)!.Text);
    }

    [Fact]
    public void TheAnswerStillParsesAsAnAction()
    {
        var json = Conversation(
            "n2",
            ("n0", null, null, null, null),
            ("n1", "n0", "user", "text", Prompt),
            ("n2", "n1", "assistant", "text", Action));

        var content = ChatGptToolProtocol.Parse(ChatGptWebProvider.ExactAnswer(json, 1)!.Text);

        var call = Assert.Single(content.Actions);
        Assert.Equal("Write", call.Name);
        Assert.Equal("{\n  \"name\": \"app\"\n}", JsonNode.Parse(call.ArgumentsJson)!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// The composer is a markdown editor and rewrites what it is handed: underscores come back
    /// escaped, a URL comes back as a link, and Windows CRLF comes back as LF. Measured on the turn
    /// that broke: 6053 characters went out and 6325 came back. Holding the stored message up
    /// against the sent one threw a good answer away and took the page's mangled copy instead —
    /// which is where a lone backslash in "src\style.css" came from, and with it a dead turn.
    /// </summary>
    [Fact]
    public void APromptTheComposerRewroteIsStillThisTurn()
    {
        var json = Conversation(
            "n2",
            ("n0", null, null, null, null),
            ("n1", "n0", "user", "text",
                @"RESULT read\_file: @import url('[https://fonts.googleapis.com](https://fonts.googleapis.com)')"),
            ("n2", "n1", "assistant", "text", Action));

        Assert.Equal(Action, ChatGptWebProvider.ExactAnswer(json, 1)!.Text);
    }

    [Fact]
    public void EveryTurnSentIntoTheChatHasToBeThere()
    {
        var json = Conversation(
            "n4",
            ("n0", null, null, null, null),
            ("n1", "n0", "user", "text", "first"),
            ("n2", "n1", "assistant", "text", "an answer"),
            ("n3", "n2", "user", "text", "second"),
            ("n4", "n3", "assistant", "text", Action));

        Assert.Equal(Action, ChatGptWebProvider.ExactAnswer(json, 2)!.Text);
        // A third went out and is not in here yet, so this is the turn before it.
        Assert.Null(ChatGptWebProvider.ExactAnswer(json, 3));
    }

    [Fact]
    public void AbandonedBranchesCannotMakeAnOldAnswerLookCurrent()
    {
        var json = Conversation(
            "n2",
            ("n0", null, null, null, null),
            ("n1", "n0", "user", "text", "first"),
            ("n2", "n1", "assistant", "text", Action),
            ("abandoned-user", "n0", "user", "text", "edited first"));

        Assert.Null(ChatGptWebProvider.ExactAnswer(json, 2));
    }

    [Fact]
    public void ARemoteBranchAheadOfTheExpectedTurnIsNotAccepted()
    {
        var json = Conversation(
            "n4",
            ("n0", null, null, null, null),
            ("n1", "n0", "user", "text", "first"),
            ("n2", "n1", "assistant", "text", "answer"),
            ("n3", "n2", "user", "text", "unexpected remote send"),
            ("n4", "n3", "assistant", "text", Action));

        Assert.Null(ChatGptWebProvider.ExactAnswer(json, 1));
    }

    /// <summary>
    /// Drawing a picture leaves the assistant's own message empty and the image on a tool message
    /// beside it, so the turn used to read as nothing at all and the app reported that ChatGPT had
    /// answered with nothing — while the picture sat on screen. Measured shapes: "sediment://" from
    /// image_gen, "file-service://" from an upload.
    /// </summary>
    [Theory]
    [InlineData("sediment://file_000000004120820bbfd4cf1cb095b51e", "file_000000004120820bbfd4cf1cb095b51e")]
    [InlineData("file-service://file-abc123", "file-abc123")]
    public void APictureIsCarriedOutOfTheTurnEvenWithNoWords(string pointer, string expectedId)
    {
        var json = ConversationWithAssets(
            "n4",
            ("n0", null, null, null, null, null),
            ("n1", "n0", "user", "text", Prompt, null),
            ("n2", "n1", "tool", "multimodal_text", null, pointer),
            ("n3", "n2", "assistant", "reasoning_recap", null, null),
            ("n4", "n3", "assistant", "text", "", null));

        var turn = ChatGptWebProvider.ExactAnswer(json, 1);

        Assert.NotNull(turn);
        Assert.Equal("", turn.Text);
        Assert.Equal(expectedId, Assert.Single(turn.Assets));
    }

    [Fact]
    public void TheSamePictureIsNotCarriedTwice()
    {
        // image_gen writes the pointer on two messages of the one turn.
        var json = ConversationWithAssets(
            "n4",
            ("n0", null, null, null, null, null),
            ("n1", "n0", "user", "text", Prompt, null),
            ("n2", "n1", "tool", "multimodal_text", null, "sediment://file_one"),
            ("n3", "n2", "tool", "multimodal_text", null, "sediment://file_one"),
            ("n4", "n3", "assistant", "text", "Here it is.", null));

        var turn = ChatGptWebProvider.ExactAnswer(json, 1);

        Assert.Equal("Here it is.", turn!.Text);
        Assert.Equal("file_one", Assert.Single(turn.Assets));
    }

    [Fact]
    public void ATurnWithNothingButThinkingIsNoAnswer()
    {
        var json = Conversation(
            "n2",
            ("n0", null, null, null, null),
            ("n1", "n0", "user", "text", Prompt),
            ("n2", "n1", "assistant", "thoughts", null));

        Assert.Null(ChatGptWebProvider.ExactAnswer(json, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"current_node":"missing","mapping":{}}""")]
    // Opens like JSON, so the cheap shape check waves it through; parsing must not throw.
    [InlineData("{\"current_node\":\"n1\",\"mapping\":{")]
    public void AnythingUnreadableFallsBackToNothing(string json)
        => Assert.Null(ChatGptWebProvider.ExactAnswer(json, 1));

    [Fact]
    public void AWalkThatNeverFindsThePromptGivesUp()
    {
        var nodes = new List<(string, string?, string?, string?, string?)> { ("n0", null, null, null, null) };
        for (var i = 1; i <= 60; i++)
        {
            nodes.Add(($"n{i}", $"n{i - 1}", "assistant", "text", $"line {i}"));
        }

        Assert.Null(ChatGptWebProvider.ExactAnswer(Conversation("n60", [.. nodes]), 1));
    }
}

/// <summary>
/// What the newest message carries. The harness appends turns of its own behind a batch of tool
/// results — the token block rides one — and every other wire folds those onto the user message
/// they accompany; this one used to send the last of them alone, which is the block and none of
/// the results the model was waiting for.
/// </summary>
public sealed class ChatGptContinuationTests
{
    private static LlmRequest Request(params ChatMessage[] messages) => new()
    {
        ModelId = "auto",
        SystemPrompt = "",
        Messages = messages,
    };

    private static ChatMessage Harness(string text) =>
        new(Role.User, [new TextBlock(text)]) { HarnessSystemTurn = true };

    [Fact]
    public void AHarnessTurnRidesWithTheResultsItFollows()
    {
        var request = Request(
            new ChatMessage(Role.User, [new TextBlock("go")]),
            new ChatMessage(Role.Assistant, [new ToolCallBlock("c1", "PowerShell", """{"command":"dir"}""")]),
            new ChatMessage(Role.User, [new ToolResultBlock("c1", "PowerShell", "a.txt", false)]),
            Harness("<total_tokens>1000 tokens left</total_tokens>"));

        var latest = ChatGptWebProvider.LatestTurnText(request);

        Assert.Contains("RESULT PowerShell: a.txt", latest);
        Assert.Contains("<total_tokens>1000 tokens left</total_tokens>", latest);
    }

    /// <summary>
    /// A conversation with nothing in it is what a test harness and a freshly rewound session both
    /// hand over; the split has to answer rather than reach past the end of the list.
    /// </summary>
    [Fact]
    public void AnEmptyConversationSplitsIntoNothing()
    {
        var request = Request();

        Assert.Equal("", ChatGptWebProvider.LatestTurnText(request));
        Assert.Empty(ChatGptWebProvider.LatestTurnImages(request));
        Assert.DoesNotContain("=== CURRENT MESSAGE ===", ChatGptWebProvider.FlattenHistory(request));
    }

    /// <summary>A run of user-side messages is the newest turn, however many of them there are.</summary>
    [Fact]
    public void EveryMessageSinceTheLastAnswerIsSent()
    {
        var request = Request(
            new ChatMessage(Role.User, [new TextBlock("go")]),
            new ChatMessage(Role.Assistant, [new TextBlock("on it")]),
            new ChatMessage(Role.User, [new TextBlock("a notification")]),
            new ChatMessage(Role.User, [new TextBlock("and the prompt")]));

        var latest = ChatGptWebProvider.LatestTurnText(request);

        Assert.Contains("a notification", latest);
        Assert.Contains("and the prompt", latest);
    }

    /// <summary>The whole history still reads as a transcript, with the newest turn marked once.</summary>
    [Fact]
    public void TheFlattenedHistoryMarksTheNewestTurnOnce()
    {
        var request = Request(
            new ChatMessage(Role.User, [new TextBlock("go")]),
            new ChatMessage(Role.Assistant, [new TextBlock("on it")]),
            new ChatMessage(Role.User, [new ToolResultBlock("c1", "Read", "hello", false)]),
            Harness("<total_tokens>1000 tokens left</total_tokens>"));

        var text = ChatGptWebProvider.FlattenHistory(request);

        Assert.Equal(1, text.Split("=== CURRENT MESSAGE ===").Length - 1);
        Assert.Contains("User: go", text);
        Assert.Contains("Assistant: on it", text);
        Assert.Contains("RESULT Read: hello", text);
        Assert.EndsWith("<total_tokens>1000 tokens left</total_tokens>", text);
    }

    /// <summary>
    /// The skill tool answers with an announcement and injects the skill itself beside it. Dropping
    /// that left the model with "Launching skill: x" and none of the instructions it announced.
    /// </summary>
    [Fact]
    public void AResultsFollowUpTextIsCarried()
    {
        var result = new ToolResultBlock("c1", "Skill", "Launching skill: greet", false)
        {
            FollowUpText = "Say hello in Vietnamese.",
        };

        var rendered = ChatGptToolProtocol.RenderResult(result);

        Assert.Contains("RESULT Skill: Launching skill: greet", rendered);
        Assert.Contains("Say hello in Vietnamese.", rendered);
    }

    [Fact]
    public void AToolResultsPicturesAreMarkedWhereTheyBelong()
    {
        var result = new ToolResultBlock(
            "c1", "screenshot", "captured", false, [new ImageBlock("image/png", "AAAA")]);

        Assert.Contains("[1 image(s) from this call]", ChatGptToolProtocol.RenderResult(result));
    }

    [Fact]
    public void PicturesOfTheNewestTurnAreCollected()
    {
        var request = Request(
            new ChatMessage(Role.User, [new TextBlock("go")]),
            new ChatMessage(Role.Assistant, [new ToolCallBlock("c1", "screenshot", "{}")]),
            new ChatMessage(Role.User,
            [
                new ToolResultBlock("c1", "screenshot", "captured", false, [new ImageBlock("image/png", "AAAA")]),
            ]),
            new ChatMessage(Role.User, [new ImageBlock("image/jpeg", "BBBB"), new TextBlock("and this one")]));

        var images = ChatGptWebProvider.LatestTurnImages(request);

        Assert.Equal(["AAAA", "BBBB"], images.Select(image => image.Base64Data));
    }

    /// <summary>
    /// The chat already holds the turns older pictures belonged to; re-attaching them would send
    /// the same screenshot again on every message.
    /// </summary>
    [Fact]
    public void OlderPicturesAreLeftWhereTheyAre()
    {
        var request = Request(
            new ChatMessage(Role.User, [new ImageBlock("image/png", "OLD")]),
            new ChatMessage(Role.Assistant, [new TextBlock("seen")]),
            new ChatMessage(Role.User, [new TextBlock("carry on")]));

        Assert.Empty(ChatGptWebProvider.LatestTurnImages(request));
        Assert.Contains("[image]", ChatGptWebProvider.FlattenHistory(request));
    }
}

/// <summary>
/// What the model is told about its actions. The whole JSON Schema is what an API takes; this
/// channel types the message into a web composer, and the reference's own tool documents run to
/// tens of kilobytes of schema for one turn.
/// </summary>
public sealed class ChatGptActionDocsTests
{
    [Fact]
    public void ArgumentsAreNamedWithTheirTypesAndWhichAreRequired()
    {
        var schema = (JsonObject)JsonNode.Parse("""
            {"type":"object",
             "properties":{"file_path":{"type":"string","description":"a very long description"},
                           "limit":{"type":"integer"}},
             "required":["file_path"]}
            """)!;

        Assert.Equal(schema.ToJsonString(), ChatGptToolProtocol.Arguments(schema));
    }

    [Fact]
    public void AnEnumeratedArgumentNamesItsChoices()
    {
        var schema = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{"mode":{"enum":["read","write"]}}}
            """)!;

        Assert.Equal(schema.ToJsonString(), ChatGptToolProtocol.Arguments(schema));
    }

    /// <summary>
    /// The browser pane's form_input declares <c>"type": ["string","boolean","number"]</c> and its
    /// server is always loaded, so a schema reader that treats "type" as a string threw on every
    /// Code turn this provider ran — before the message was even built.
    /// </summary>
    [Fact]
    public void AnArgumentThatTakesMoreThanOneKindListsThem()
    {
        var schema = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{"value":{"type":["string","boolean","number"]}}}
            """)!;

        Assert.Equal(schema.ToJsonString(), ChatGptToolProtocol.Arguments(schema));
    }

    /// <summary>An enumerated argument may hold numbers, which are not a shape error either.</summary>
    [Fact]
    public void AnEnumOfNumbersIsListedAsItsNumbers()
    {
        var schema = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{"scale":{"enum":[1,2,3]}}}
            """)!;

        Assert.Equal(schema.ToJsonString(), ChatGptToolProtocol.Arguments(schema));
    }

    /// <summary>Neither does a schema that says nothing about a property at all.</summary>
    [Fact]
    public void AnArgumentWithNoTypeReadsAsAny()
    {
        var schema = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{"anything":{"description":"whatever you like"}}}
            """)!;

        Assert.Equal(schema.ToJsonString(), ChatGptToolProtocol.Arguments(schema));
    }

    [Fact]
    public void AnArrayArgumentSaysWhatItHolds()
    {
        var schema = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{"paths":{"type":"array","items":{"type":"string"}}}}
            """)!;

        Assert.Equal(schema.ToJsonString(), ChatGptToolProtocol.Arguments(schema));
    }

    [Fact]
    public void ASchemaWithNoPropertiesStillReads()
        => Assert.Equal("""{"type":"object","properties":{}}""", ChatGptToolProtocol.Arguments(
            (JsonObject)JsonNode.Parse("""{"type":"object","properties":{}}""")!));

    /// <summary>Even properties near the end of a large schema remain callable.</summary>
    [Fact]
    public void AHugeSchemaRetainsEveryProperty()
    {
        var properties = new JsonObject();
        for (var i = 0; i < 200; i++)
        {
            properties[$"argument_number_{i}"] = new JsonObject { ["type"] = "string" };
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        var arguments = ChatGptToolProtocol.Arguments(schema);

        Assert.True(schema.ToJsonString().Length > 5000, "the schema this stands in for should be a big one");
        Assert.Equal(schema.ToJsonString(), arguments);
        Assert.Contains("argument_number_199", arguments);
    }

    /// <summary>
    /// The contract and the example ride the first message alone. A tool the session gained since —
    /// one tool search loaded, one an approved plan brought back — has never been named, so it is
    /// named before it can be asked for.
    /// </summary>
    [Fact]
    public void OnlyTheNewActionsAreAnnounced()
    {
        var announcement = ChatGptToolProtocol.AdditionalActions(
            [new ToolDefinition("Edit", "edits a file", new JsonObject())]);

        Assert.Contains("Edit", announcement);
        Assert.Contains("also available now", announcement);
    }

    [Fact]
    public void NothingNewSaysNothing()
        => Assert.Equal("", ChatGptToolProtocol.AdditionalActions([]));
}

/// <summary>
/// The web session reports no token figures at all. Zero is not a smaller lie than an estimate: it
/// is the one figure that makes the context ring, the auto-compaction threshold and the token
/// budget all read as an empty conversation however long it runs.
/// </summary>
public sealed class ChatGptUsageTests
{
    private static LlmRequest Request(params ChatMessage[] messages) => new()
    {
        ModelId = "auto",
        SystemPrompt = "be helpful",
        Messages = messages,
    };

    [Fact]
    public void TheInputSideIsTheWholeConversationTheAccountNowHolds()
    {
        var shortOne = Request(new ChatMessage(Role.User, [new TextBlock("hi")]));
        var longOne = Request(
            new ChatMessage(Role.User, [new TextBlock(new string('x', 4000))]),
            new ChatMessage(Role.Assistant, [new TextBlock("noted")]),
            new ChatMessage(Role.User, [new TextBlock("hi")]));

        var small = ChatGptWebProvider.EstimatedUsage(shortOne, "ok");
        var large = ChatGptWebProvider.EstimatedUsage(longOne, "ok");

        Assert.True(small.InputTokens > 0);
        Assert.True(large.InputTokens > small.InputTokens + 900, $"{large.InputTokens} against {small.InputTokens}");
    }

    [Fact]
    public void TheOutputSideIsTheAnswer()
    {
        var request = Request(new ChatMessage(Role.User, [new TextBlock("hi")]));

        Assert.True(
            ChatGptWebProvider.EstimatedUsage(request, new string('y', 400)).OutputTokens
            > ChatGptWebProvider.EstimatedUsage(request, "ok").OutputTokens);
    }
}

/// <summary>
/// What the page shows while it works. The turn used to sit silent for as long as the model
/// thought, which on this channel can be four minutes.
/// </summary>
public sealed class ChatGptProgressTests
{
    [Fact]
    public void TextThatGrewIsSentAsWhatGrew()
        => Assert.Equal(" and searching", ChatGptWebProvider.ProgressDelta("Thinking", "Thinking and searching"));

    [Fact]
    public void TheFirstTextIsSentWhole()
        => Assert.Equal("Thinking", ChatGptWebProvider.ProgressDelta("", "Thinking"));

    /// <summary>A new phase replaces the last one, so it is a new thing said rather than more.</summary>
    [Fact]
    public void TextThatWasReplacedStartsItsOwnLine()
        => Assert.Equal("\nSearched the web", ChatGptWebProvider.ProgressDelta("Thinking", "Searched the web"));

    /// <summary>
    /// The page re-measures itself as the answer grows and the working text can shrink back into
    /// what was already sent; repeating it there would print the thinking twice.
    /// </summary>
    [Theory]
    [InlineData("Thinking", "Thinking")]
    [InlineData("Thinking", "")]
    [InlineData("", "")]
    [InlineData("Thinking hard about it", "Thinking hard")]
    [InlineData("Thinking hard about it", "T")]
    public void NothingNewIsSentAsNothing(string reported, string current)
        => Assert.Equal("", ChatGptWebProvider.ProgressDelta(reported, current));
}

/// <summary>
/// The two halves of the answer to a model that has tools of its own: say, in the last thing it
/// reads, that its sandbox is not this machine — and notice when it used the sandbox anyway.
/// </summary>
public sealed class ChatGptOwnToolTests
{
    private static LlmRequest Request(bool withTools) => new()
    {
        ModelId = "auto",
        SystemPrompt = "be helpful",
        Messages = [new ChatMessage(Role.User, [new TextBlock("build it")])],
        Tools = withTools ? [new ToolDefinition("Edit", "edits", new JsonObject())] : [],
    };

    [Fact]
    public void TheClosingReminderIsTheLastThingASessionWithToolsReads()
    {
        var text = ChatGptWebProvider.FlattenHistory(Request(withTools: true));

        Assert.EndsWith(ChatGptToolProtocol.Closing().TrimEnd(), text);
        // It is a reminder, not the contract: the contract still opens the message.
        Assert.True(text.IndexOf("=== OUTPUT CONTRACT ===", StringComparison.Ordinal)
            < text.IndexOf("=== BEFORE YOU REPLY ===", StringComparison.Ordinal));
    }

    [Fact]
    public void AToollessRequestGetsNoReminderAboutActions()
        => Assert.DoesNotContain("=== BEFORE YOU REPLY ===", ChatGptWebProvider.FlattenHistory(Request(withTools: false)));

    [Theory]
    [InlineData("container.exec", true)]
    [InlineData("api_tool.call_tool", true)]
    [InlineData("python", true)]
    [InlineData("all", false)]
    [InlineData("", false)]
    // Web search rides the same field and answers a question honestly, so it is not work done
    // somewhere else — reading every non-"all" recipient as one would fail an answered question.
    [InlineData("web", false)]
    public void OnlyTheToolsThatDoWorkCountAsWorkDoneElsewhere(string recipient, bool expected)
    {
        var message = new JsonObject
        {
            ["author"] = new JsonObject { ["role"] = "assistant" },
            ["recipient"] = recipient,
        };

        Assert.Equal(expected, ChatGptWebProvider.AddressesOwnWorkTool(message));
    }

    [Fact]
    public void ATurnThatRanTheSandboxSaysSo()
    {
        var turn = ChatGptWebProvider.ExactAnswer(Conversation("n3", sandbox: true), 1);

        Assert.NotNull(turn);
        Assert.True(turn!.UsedOwnTools);
    }

    [Fact]
    public void AnOrdinaryTurnDoesNot()
    {
        var turn = ChatGptWebProvider.ExactAnswer(Conversation("n3", sandbox: false), 1);

        Assert.NotNull(turn);
        Assert.False(turn!.UsedOwnTools);
    }

    /// <summary>A user turn, one middle message, and the answer.</summary>
    private static string Conversation(string currentNode, bool sandbox)
    {
        static JsonObject Node(string? parent, JsonObject? message) =>
            message is null
                ? new JsonObject { ["parent"] = parent }
                : new JsonObject { ["parent"] = parent, ["message"] = message };

        static JsonObject Message(string role, string recipient, string? text) => new()
        {
            ["author"] = new JsonObject { ["role"] = role },
            ["recipient"] = recipient,
            ["content"] = new JsonObject
            {
                ["content_type"] = "text",
                ["parts"] = text is null ? new JsonArray() : new JsonArray(text),
            },
        };

        var mapping = new JsonObject
        {
            ["n0"] = Node(null, null),
            ["n1"] = Node("n0", Message("user", "all", "build it")),
            ["n2"] = Node("n1", Message("assistant", sandbox ? "container.exec" : "all", "working")),
            ["n3"] = Node("n2", Message("assistant", "all", "done")),
        };

        return new JsonObject { ["current_node"] = currentNode, ["mapping"] = mapping }.ToJsonString();
    }
}
