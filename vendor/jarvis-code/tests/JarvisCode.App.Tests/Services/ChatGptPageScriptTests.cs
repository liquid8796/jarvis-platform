using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The page script cannot be run from a test, but the mistake it made can still be pinned: the
/// model pill was fetched with a single query while every other element was waited for, so the
/// first turn of a session — the one where the page is still coming up — asked before the pill
/// existed and failed with "this page has no model picker".
/// </summary>
public sealed class ChatGptPageScriptTests
{
    private const string PillSelector = "__composer-pill";

    private static string Script(string? model) =>
        ChatGptWebViewTransport.ComposeScript(new ChatGptAsk("hello", model, null, null));

    [Fact]
    public void ThePickerIsWaitedForRatherThanAskedForOnce()
    {
        var script = Script("GPT-5.5");

        Assert.Contains(PillSelector, script);
        // However the lookup is spelled, it is reached through a wait and never queried once.
        Assert.Contains("await wait(picker,", script);
        Assert.Equal(1, script.Split(PillSelector).Length - 1);
    }

    [Fact]
    public void APageThatNeverGrowsAPickerSaysSoInTermsOfTheModelAskedFor()
    {
        var script = Script("GPT-5.5");

        Assert.Contains("never grew its model picker", script);
        Assert.DoesNotContain("this page has no model picker", script);
    }

    /// <summary>
    /// Most turns run on the account default, and those must not wait on a picker they never use.
    /// </summary>
    [Fact]
    public void WithNoModelChosenThePickerBranchIsInert()
        => Assert.Contains("const wanted = null;", Script(null));

    [Fact]
    public void AChosenModelReachesTheScriptAsAJsonString()
        => Assert.Contains("""const wanted = "GPT-5.5";""", Script("GPT-5.5"));

    /// <summary>Quotes in a prompt must not be able to close the string they are typed into.</summary>
    [Theory]
    [InlineData("""say "hi" \ now""")]
    [InlineData("line one\nline two")]
    [InlineData("""); alert('x'); //""")]
    public void ThePromptSurvivesEncodingWithoutEscapingItsString(string prompt)
    {
        var script = ChatGptWebViewTransport.ComposeScript(new ChatGptAsk(prompt, null, null, null));

        Assert.Equal(prompt, Declared(script, "const promptText = "));
    }

    /// <summary>
    /// The note is what the model reads when the page would not take the pictures, so it has to
    /// survive the same trip the prompt does.
    /// </summary>
    [Fact]
    public void TheOmissionNoteReachesTheScriptIntact()
    {
        var ask = new ChatGptAsk(
            "look at this",
            null,
            null,
            null,
            [new ChatGptImage("image/png", "AAAA")],
            "[1 image(s) omitted: this channel carries text only]");

        var script = ChatGptWebViewTransport.ComposeScript(ask);

        Assert.Equal("[1 image(s) omitted: this channel carries text only]", Declared(script, "const omissionNote = "));
        Assert.Contains("const expected = 1;", script);
    }

    [Fact]
    public void WithNoPicturesTheAttachBranchIsInert()
        => Assert.Contains("const expected = 0;", Script(null));

    /// <summary>
    /// The bytes travel in their own script so the composer script does not carry megabytes of
    /// base64 — and they must arrive as data, not as something the page could run.
    /// </summary>
    [Fact]
    public void TheStashedPicturesAreJsonRatherThanCode()
    {
        var script = ChatGptWebViewTransport.StashImagesScript(
            [new ChatGptImage("image/png", "');alert(1);//")]);

        var opening = script.IndexOf('[', StringComparison.Ordinal);
        var closing = script.LastIndexOf(']');
        var parsed = (JsonArray)JsonNode.Parse(script[opening..(closing + 1)])!;

        Assert.Equal("image/png", parsed[0]!["type"]!.GetValue<string>());
        Assert.Equal("');alert(1);//", parsed[0]!["b64"]!.GetValue<string>());
    }

    /// <summary>
    /// A thinking model can spend minutes with nothing of its own on the page, and the page's own
    /// working text is the only thing that says so.
    /// </summary>
    [Fact]
    public void TheWorkingTextIsReportedOutOfBothWaits()
    {
        var script = Script(null);

        Assert.Contains("window.__jarvisChatGpt.progress", script);
        // Once while the answer has not arrived, once while it is being written.
        Assert.Equal(2, script.Split("report();").Length - 1);
    }

    /// <summary>
    /// The value of a <c>const x = …;</c> line, read back as the string it encodes. Each of these
    /// is one line whatever went in, so the statement ends where the line does — looking for ");"
    /// instead would find one the value itself contains.
    /// </summary>
    private static string Declared(string script, string marker)
    {
        var line = script
            .Split('\n')
            .Single(l => l.Contains(marker, StringComparison.Ordinal))
            .TrimEnd('\r');

        return JsonNode
            .Parse(line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].TrimEnd(';'))!
            .GetValue<string>();
    }
}

/// <summary>
/// ChatGPT sits behind a check that reads the TLS handshake and the client hints beside the
/// user-agent header. A Chrome pinned to another year is a mismatch it can see.
/// </summary>
public sealed class ChatGptUserAgentTests
{
    [Fact]
    public void TheUserAgentNamesTheEngineThisAppActuallyRuns()
    {
        var major = ElectronRuntime.ChromiumVersion.Split('.')[0];

        Assert.Contains($"Chrome/{major}.0.0.0", ChatGptWebViewTransport.UserAgent);
        Assert.DoesNotContain("Electron", ChatGptWebViewTransport.UserAgent);
    }

    /// <summary>The engine moved and this string did not, which is the mismatch that was there.</summary>
    [Fact]
    public void ItIsNoLongerThePinnedChromeOfThePlainTransport()
        => Assert.NotEqual(
            JarvisCode.Providers.ChatGptWeb.ChatGptHttpTransport.UserAgent, ChatGptWebViewTransport.UserAgent);
}

/// <summary>
/// Reading the models the account can pick. There is no endpoint to ask, so the picker itself is
/// the list — and it has to be the same picker the composer clicks, or a label would be stored
/// that the turn cannot then select.
/// </summary>
public sealed class ChatGptModelDiscoveryTests
{
    /// <summary>
    /// The pill may say the concrete model behind an alias (for example, "6 Pro") while the
    /// selectable row still says "Latest". Both values must cross the page boundary; replacing
    /// the row with the pill would leave the app trying to click an option that does not exist.
    /// </summary>
    [Fact]
    public void DiscoveryReturnsTheResolvedPillSeparatelyFromTheSelectableRows()
    {
        var script = ChatGptWebViewTransport.ComposerControlsScript;

        Assert.Contains("const currentModelLabel = label(pill);", script);
        Assert.Contains("const models = options.map", script);
        Assert.Contains("key: text", script);
        Assert.Contains("label: text", script);
        Assert.Contains("currentModelLabel, models", script);
    }

    [Fact]
    public void ModelOnlyDiscoveryReturnsBeforeInspectingThePowerMarkup()
    {
        Assert.Contains("const includePower = false;", ChatGptWebViewTransport.ComposerModelsScript);
        Assert.Contains("if (!includePower)", ChatGptWebViewTransport.ComposerModelsScript);
    }

    [Fact]
    public void ColdPowerReadinessRequeriesTheLiveControlRatherThanKeepingItsFirstRow()
    {
        foreach (var script in new[]
        {
            ChatGptWebViewTransport.ComposerControlsScript,
            ChatGptWebViewTransport.ConfigureComposerScript("Latest", configurePower: true)
        })
        {
            Assert.Contains("await wait(livePowerControl,", script);
            Assert.Contains("node.isConnected", script);
            Assert.Contains("getClientRects().length > 0", script);
            Assert.DoesNotContain("await wait(() => power.querySelector", script);
            Assert.DoesNotContain("__POWER_HELPERS__", script);
        }
    }

    [Fact]
    public void PowerStateSkipsHiddenRowsButAllowsThePagesAriaHiddenVisualSlider()
    {
        var script = ChatGptWebViewTransport.PowerStateScript;

        Assert.Contains("const control = livePowerControl();", script);
        Assert.Contains("node.isConnected", script);
        Assert.Contains("getComputedStyle(node).visibility !== 'hidden'", script);
        Assert.DoesNotContain("[aria-hidden", script);
        Assert.DoesNotContain("__POWER_HELPERS__", script);
    }

    /// <summary>
    /// A model rolled out after this build is still just a picker string. Configuration embeds
    /// that string as data and finds the matching live row instead of consulting a release-time
    /// switch over known model names.
    /// </summary>
    [Fact]
    public void AnArbitraryFutureModelKeyNeedsNoScriptCatalogEntry()
    {
        const string future = "GPT-9 Quasar β / Preview";
        var script = ChatGptWebViewTransport.ConfigureComposerScript(future, configurePower: false);

        Assert.Contains($"const wanted = {JsonValue.Create(future)!.ToJsonString()};", script);
        Assert.Contains("label(row).toLowerCase() === wanted.toLowerCase()", script);
    }

    [Fact]
    public void DiscoveryReadsThePickerTheComposerClicks()
    {
        Assert.Contains("await wait(picker,", ChatGptWebViewTransport.ModelsScript);
        Assert.Contains("menuitemradio", ChatGptWebViewTransport.ModelsScript);
    }

    /// <summary>The label is read the one way, so a stored model still matches its own row.</summary>
    [Fact]
    public void TheLabelIsReadTheSameWayInBothScripts()
        => Assert.Contains("options.map(label)", ChatGptWebViewTransport.ModelsScript);

    /// <summary>
    /// The menu is opened once and left open. Measured against chatgpt.com on 2026-09-04: nothing
    /// this script can dispatch closes it — it answers trusted input only — and pressing the pill
    /// again is a no-op rather than a toggle, so the script does not pretend otherwise.
    /// </summary>
    [Fact]
    public void ThePickerIsPressedOnceAndLeftAsItIs()
        => Assert.Equal(1, ChatGptWebViewTransport.ModelsScript.Split("press(pill);").Length - 1);
}

/// <summary>
/// A failed turn leaves the reference's error card, and the card places the failure by the words in
/// it. These are the two ends joined: what the provider says, and where the card puts it.
/// </summary>
public sealed class ChatGptErrorCardTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void ARejectedSessionLandsOnTheAuthenticationCard(int status)
        => Assert.Equal(
            ApiErrorCategory.Auth,
            ErrorCards.Classify(ChatGptWebProvider.Summarize(new ChatGptResponse(status, ""))));

    [Fact]
    public void ARateLimitedAccountLandsOnTheUsageCard()
        => Assert.Equal(
            ApiErrorCategory.RateLimit,
            ErrorCards.Classify(ChatGptWebProvider.Summarize(new ChatGptResponse(429, ""))));

    [Fact]
    public void APageThatTimedOutLandsOnTheNetworkCard()
        => Assert.Equal(
            ApiErrorCategory.Network,
            ErrorCards.Classify(ChatGptWebProvider.Classified("TIMEOUT: the page did not open.")));

    [Fact]
    public void ASignedOutBrowserLandsOnTheAuthenticationCard()
        => Assert.Equal(
            ApiErrorCategory.Auth,
            ErrorCards.Classify(ChatGptWebProvider.Classified("The embedded browser is not signed in to ChatGPT.")));
}

/// <summary>
/// ChatGPT's markup is not a contract and it moves. Every element the turn depends on is looked up
/// two ways, so a renamed test id costs the fallback rather than the session — and cancelling has
/// to reach the page, since an answer nobody reads still spends the account's quota.
/// </summary>
public sealed class ChatGptSelectorTests
{
    private static string Script => ChatGptWebViewTransport.ComposeScript(new ChatGptAsk("hi", null, null, null));

    [Theory]
    [InlineData("#prompt-textarea", "contenteditable")]
    [InlineData("data-testid=\"send-button\"", "aria-label*=\"Send\"")]
    [InlineData("data-testid=\"stop-button\"", "aria-label*=\"Stop\"")]
    [InlineData("copy-turn-action-button", "aria-label*=\"Copy\"")]
    [InlineData("data-message-author-role", "agent-turn")]
    public void EveryElementTheTurnDependsOnIsLookedUpTwoWays(string primary, string fallback)
    {
        Assert.Contains(primary, Script);
        Assert.Contains(fallback, Script);
    }

    /// <summary>
    /// A fenced code block grows a copy button of its own the moment it opens, so an answer that
    /// starts with one would read as finished while it was still being written.
    /// </summary>
    [Fact]
    public void ACodeBlocksOwnCopyButtonDoesNotEndTheAnswer()
    {
        Assert.Contains("!b.closest('[data-message-author-role]')", Script);
    }

    [Fact]
    public void CancellingPressesChatGptsOwnStop()
    {
        Assert.Contains("data-testid=\"stop-button\"", ChatGptWebViewTransport.StopScript);
        Assert.Contains("aria-label*=\"Stop\"", ChatGptWebViewTransport.StopScript);
        Assert.Contains("stop.click()", ChatGptWebViewTransport.StopScript);
    }

    /// <summary>
    /// Large prompts are pasted in bounded chunks. The browser request gate enforces the exact
    /// prompt on the wire rather than trusting the editor's markdown serializer.
    /// </summary>
    [Fact]
    public void TheMessageIsPastedRatherThanTyped()
    {
        Assert.Contains("new ClipboardEvent('paste'", Script);
        Assert.Contains("data.setData('text/plain'", Script);
        Assert.Contains("const pasteLimit = 8000;", Script);
        Assert.Contains("Math.min(offset + pasteLimit, clean.length)", Script);
        Assert.DoesNotContain("document.execCommand('insertText'", Script);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeRecoveryOnlySelectsTheComposerInsteadOfReusingSyntheticDelete(bool selectAll)
    {
        var script = ChatGptWebViewTransport.NativeComposerScript(selectAll);

        Assert.Contains("range.selectNodeContents(node)", script);
        Assert.Contains("document.activeElement !== node", script);
        Assert.Contains($"selectComposer(node, {selectAll.ToString().ToLowerInvariant()})", script);
        Assert.DoesNotContain("execCommand('delete'", script);
        Assert.DoesNotContain("execCommand('selectAll'", script);
        Assert.DoesNotContain("__SELECT_ALL__", script);
    }

    /// <summary>
    /// The composer takes its time deciding a large message may be sent. Measured on
    /// 2026-09-04 with 103,032 characters in it, the send button was still disabled thirty
    /// seconds after the text had landed — so the twenty seconds this used to wait refused a
    /// message the page would have sent a moment later, and lost the whole turn to it.
    /// </summary>
    [Fact]
    public void TheSendButtonIsWaitedForLongEnoughForALargeMessage()
    {
        Assert.Contains("the send button never became available", Script);
        Assert.DoesNotContain("}, 20000);", Script);
        Assert.Contains("}, 180000);", Script);
    }

    /// <summary>
    /// The message is built with a StringBuilder, whose AppendLine writes CRLF on Windows; a
    /// carriage return pasted into the composer is a character the model is shown for no reason.
    /// </summary>
    [Fact]
    public void NewlinesAreNormalizedBeforeTheyReachTheComposer()
        => Assert.Contains(@"const clean = typed.replace(/\r\n?/g, '\n')", Script);
}
