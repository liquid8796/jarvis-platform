using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.App.Tests.Services;

public sealed class ChatGptTransportBehaviorTests
{
    [Theory]
    [InlineData("https://chatgpt.com/c/abc", "https://chatgpt.com/", null, false)]
    [InlineData("https://chatgpt.com/g/g-p-1/c/abc", "https://chatgpt.com/g/g-p-1/project", null, false)]
    [InlineData("https://chatgpt.com/g/g-p-1/project", "https://chatgpt.com/g/g-p-1/project", null, true)]
    [InlineData("https://chatgpt.com/g/g-p-1/c/abc", "https://chatgpt.com/c/abc", "abc", true)]
    [InlineData("https://chatgpt.com/c/abcd", "https://chatgpt.com/c/abc", "abc", false)]
    [InlineData("https://example.org/c/abc", "https://chatgpt.com/c/abc", "abc", false)]
    public void NavigationKeepsOnlyTheExactRequestedConversation(string current, string target, string? id, bool expected)
        => Assert.Equal(expected, ChatGptWebViewTransport.IsAtTarget(current, target, id));

    [Theory]
    [InlineData("https://chatgpt.com/g/g-p-1/c/abc", true)]
    [InlineData("https://chatgpt.com/g/g-p-1-project-name/c/abc", true)]
    [InlineData("https://chatgpt.com/c/abc", false)]
    [InlineData("https://chatgpt.com/g/g-p-10/c/abc", false)]
    [InlineData("https://chatgpt.com/g/g-p-other-project-name/c/abc", false)]
    public void ConfiguredContinuationMustRemainInItsExactProject(string current, bool expected)
    {
        const string target = "https://chatgpt.com/g/g-p-1/c/abc";

        Assert.Equal(expected, ChatGptWebViewTransport.IsAtTarget(current, target, "abc", "g-p-1"));
    }

    [Theory]
    [InlineData("https://chatgpt.com/")]
    [InlineData("https://chatgpt.com/g/g-p-other/project")]
    public void ProjectRedirectIsRejectedBeforeTheComposerCanSend(string redirectedTo)
    {
        const string project = "https://chatgpt.com/g/g-p-1/project";

        var error = ChatGptWebViewTransport.NavigationError(redirectedTo, project, conversationId: null);

        Assert.Equal($"BROWSER_ERROR: ChatGPT redirected away from {project}. Nothing was sent.", error);
    }

    [Fact]
    public void ExactProjectNavigationCanProceedToTheComposer()
    {
        const string project = "https://chatgpt.com/g/g-p-1/project";

        Assert.Null(ChatGptWebViewTransport.NavigationError(project, project, conversationId: null));
    }

    [Fact]
    public void ScopeAndAccountBothIsolateTheBrowserProfile()
    {
        var one = new ChatGptCookieContext("", "account-a") { ScopeId = "session-a" };
        Assert.Equal(ChatGptWebViewTransport.ScopeKey(one), ChatGptWebViewTransport.ScopeKey(one with { }));
        Assert.NotEqual(ChatGptWebViewTransport.ScopeKey(one), ChatGptWebViewTransport.ScopeKey(one with { ScopeId = "agent-b" }));
        Assert.NotEqual(ChatGptWebViewTransport.ScopeKey(one), ChatGptWebViewTransport.ScopeKey(one with { InjectionJson = "account-b" }));
        Assert.Matches("^[a-f0-9]{64}$", ChatGptWebViewTransport.ScopeKey(one));
    }

    [Fact]
    public void SuccessfulImportPreservesRefreshedCookiesAcrossRestarts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-cookie-marker-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(ChatGptWebViewTransport.NeedsCookieImport(directory));
            ChatGptWebViewTransport.RecordCookieImport(directory, installedCookies: 0);
            Assert.True(ChatGptWebViewTransport.NeedsCookieImport(directory));
            ChatGptWebViewTransport.RecordCookieImport(directory, installedCookies: 1);
            Assert.False(ChatGptWebViewTransport.NeedsCookieImport(directory));
            Assert.True(ChatGptWebViewTransport.NeedsCookieImport(Path.Combine(directory, "different-cookie-source")));
        }
        finally
        {
            var marker = Path.Combine(directory, ".jarvis-cookies-imported");
            if (File.Exists(marker)) File.Delete(marker);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void LongPromptReachesTheWireExactlyAndKeepsImageParts()
    {
        var prompt = string.Concat(Enumerable.Repeat("# Heading\r\nfile_path \\test 💡 <action>\n", 4000));
        const string body = """{"messages":[{"author":{"role":"user"},"content":{"content_type":"multimodal_text","parts":[{"content_type":"image_asset_pointer","asset_pointer":"file-1"},"escaped old prompt"]}}],"model":"chosen-model","parent_message_id":"parent"}""";
        var rewritten = JsonNode.Parse(ChatGptWebViewTransport.RewritePromptBody(body, prompt))!;
        Assert.Equal(ChatGptWebViewTransport.NormalizePrompt(prompt), rewritten["messages"]![0]!["content"]!["parts"]![1]!.GetValue<string>());
        Assert.Equal("file-1", rewritten["messages"]![0]!["content"]!["parts"]![0]!["asset_pointer"]!.GetValue<string>());
        Assert.Equal("chosen-model", rewritten["model"]!.GetValue<string>());
        Assert.Equal("parent", rewritten["parent_message_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"messages":[{"author":{"role":"assistant"},"content":{"parts":["wrong"]}}]}""")]
    [InlineData("""{"messages":[{"author":{"role":"user"},"content":{"parts":["one","two"]}}]}""")]
    public void UnrecognizedWireShapesAreRejectedBeforeSending(string body)
        => Assert.Throws<InvalidOperationException>(() => ChatGptWebViewTransport.RewritePromptBody(body, "exact"));

    [Theory]
    [InlineData("composer")]
    [InlineData("paste")]
    [InlineData("send")]
    [InlineData("complete")]
    public async Task PageScriptRunsAgainstAnOfflineDom(string scenario)
    {
        var prompt = string.Concat(Enumerable.Repeat("# Test\nfile_path 💡 `code` \\windows\n", 3000));
        var ask = new ChatGptAsk(prompt, null, null, null);
        var input = JsonSerializer.Serialize(new
        {
            scenario,
            prompt,
            kickoff = ChatGptWebViewTransport.KickoffScript("test", ChatGptWebViewTransport.ComposeScript(ask)),
            cancel = ChatGptWebViewTransport.CancelOperationScript("test"),
        });
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Fixtures", "chatgpt-page-behavior.cjs"));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }
}
