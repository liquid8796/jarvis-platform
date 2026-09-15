using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The visualize server's plain parts: the composition rule, the call parsing,
/// the widget document and the link question. The 24 recorded read_me answers
/// are reproduced by <c>VisualizeCorpusParityTests</c>, which has the fixture;
/// what is here is the behaviour around them.
/// </summary>
public sealed class VisualizeTests
{
    private static ITool Find(string name) =>
        VisualizeTools.Create().Single(tool => tool.Name == name);

    [Theory]
    [InlineData("mobile", 380)]
    [InlineData("desktop", 680)]
    [InlineData("unknown", 680)]
    [InlineData(null, 680)]
    [InlineData("watch", 680)]
    public void An_unknown_platform_gets_desktop_sizing(string? platform, int width) =>
        Assert.Equal(width, VisualizeCorpus.WidthFor(platform));

    [Fact]
    public void Read_me_with_no_modules_is_base_then_footer()
    {
        var answer = VisualizeCorpus.ReadMe([], "desktop");
        Assert.Equal(
            VisualizeCorpus.Section("base") + VisualizeCorpus.Separator + VisualizeCorpus.Section("footer"),
            answer);
    }

    [Fact]
    public void A_section_two_modules_share_is_sent_once_in_first_seen_order()
    {
        // chart and data_viz name the same four sections; diagram opens with the
        // palette, which mockup also carries.
        var both = VisualizeCorpus.ReadMe(["chart", "data_viz"], "desktop");
        Assert.Equal(VisualizeCorpus.ReadMe(["chart"], "desktop"), both);

        var order = VisualizeCorpus.ReadMe(["diagram", "mockup"], "desktop");
        Assert.True(
            order.IndexOf(VisualizeCorpus.Section("palette"), StringComparison.Ordinal) <
            order.IndexOf(VisualizeCorpus.Section("w680/svg"), StringComparison.Ordinal),
            "diagram is asked for first, so its palette keeps the first slot");
        Assert.Equal(
            1,
            CountOf(order, VisualizeCorpus.Section("palette")));
    }

    [Fact]
    public void A_module_the_reference_has_no_entry_for_contributes_nothing()
    {
        Assert.Empty(VisualizeCorpus.SectionsFor(680, "watercolour"));
        Assert.Equal(
            VisualizeCorpus.ReadMe([], "desktop"),
            VisualizeCorpus.ReadMe(["watercolour"], "desktop"));
    }

    [Fact]
    public async Task Read_me_reads_its_modules_off_the_call()
    {
        var result = await Find("read_me").ExecuteAsync(
            new JsonObject
            {
                ["modules"] = new JsonArray("art", 7, "art"),
                ["platform"] = "mobile",
            },
            null!,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(VisualizeCorpus.ReadMe(["art"], "mobile"), result.Content);
    }

    [Fact]
    public async Task Show_widget_renders_nothing_and_says_so()
    {
        var result = await Find("show_widget").ExecuteAsync(
            new JsonObject { ["title"] = "q4_revenue", ["widget_code"] = "<svg/>" },
            null!,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(VisualizeTools.ShowWidgetResult, result.Content);
        Assert.DoesNotContain("<svg", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_tools_are_read_only_and_neither_is_deferrable()
    {
        Assert.All(VisualizeTools.Create(), tool => Assert.True(tool.IsReadOnly));
        Assert.All(
            InternalMcpServers.Compose([VisualizeTools.Server()], new InternalMcpSessionContext()),
            tool => Assert.True(InternalMcpServers.IsAlwaysLoad(tool)));
    }

    [Fact]
    public void A_show_widget_call_is_recognised_under_its_wire_name_only()
    {
        Assert.True(VisualizeWidgetCalls.IsShowWidget("mcp__visualize__show_widget"));
        Assert.False(VisualizeWidgetCalls.IsShowWidget("show_widget"));
        Assert.False(VisualizeWidgetCalls.IsShowWidget("mcp__visualize__read_me"));
    }

    [Fact]
    public void A_call_is_read_argument_by_argument_and_never_refused()
    {
        var call = VisualizeWidgetCalls.Parse(
            """{"title":"oauth_login_flow","widget_code":"<svg/>","loading_messages":["a",2,"b"]}""");
        Assert.Equal("oauth_login_flow", call.Title);
        Assert.Equal("<svg/>", call.WidgetCode);
        Assert.Equal(["a", "b"], call.LoadingMessages);

        foreach (var broken in new[] { null, "", "not json", "[]", "{}" })
        {
            var empty = VisualizeWidgetCalls.Parse(broken);
            Assert.Null(empty.Title);
            Assert.Equal("", empty.WidgetCode);
            Assert.Empty(empty.LoadingMessages);
        }
    }

    [Fact]
    public void The_widget_document_carries_the_declared_csp()
    {
        var policy = VisualizeWidgetPage.ContentSecurityPolicy(VisualizeTools.RuntimeSecurity);
        Assert.Contains("connect-src https://esm.sh https://cdnjs.cloudflare.com", policy, StringComparison.Ordinal);
        Assert.Contains("frame-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains(VisualizeWidgetPage.FontOrigin, policy, StringComparison.Ordinal);

        // The font origin is the reference's own addition to resourceDomains and
        // must not leak into connect-src, which its schema maps separately.
        var connect = policy.Split("connect-src ")[1].Split(';')[0];
        Assert.DoesNotContain(VisualizeWidgetPage.FontOrigin, connect, StringComparison.Ordinal);

        var document = VisualizeWidgetPage.WithSecurityPolicy(
            "<!doctype html><html><head><title>x</title></head><body></body></html>",
            VisualizeTools.RuntimeSecurity);
        Assert.Contains("<head>\n<meta http-equiv=\"Content-Security-Policy\"", document, StringComparison.Ordinal);
        Assert.True(
            document.IndexOf("Content-Security-Policy", StringComparison.Ordinal) <
            document.IndexOf("<title>", StringComparison.Ordinal),
            "the policy has to precede everything it governs");
    }

    [Fact]
    public void The_runtime_is_sandboxed_without_the_hosts_origin()
    {
        var wrapper = VisualizeWidgetPage.WrapperHtml(clipboardWrite: true);
        Assert.Contains("sandbox=\"allow-scripts allow-popups allow-forms allow-modals\"", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-same-origin", wrapper, StringComparison.Ordinal);
        Assert.Contains("allow=\"clipboard-write\"", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("allow=\"clipboard-write\"", VisualizeWidgetPage.WrapperHtml(false), StringComparison.Ordinal);
    }

    [Fact]
    public void The_proxy_answers_the_host_through_the_binding_it_was_given()
    {
        // The engine has no chrome.webview: the binding name is chosen by the
        // host and added before the page loads, so the proxy has to carry
        // whichever name it was handed rather than a compiled-in one.
        var wrapper = VisualizeWidgetPage.WrapperHtml(clipboardWrite: true, "__someOtherName");
        Assert.Contains("__someOtherName", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain(VisualizeWidgetPage.DefaultPostBinding, wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("chrome.webview", wrapper, StringComparison.Ordinal);
        Assert.Contains(VisualizeWidgetPage.HostFunction + " = function", wrapper, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("http://example.com/a", false)]
    [InlineData("file:///c:/secret", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Only_https_links_are_offered(string? url, bool followable) =>
        Assert.Equal(followable, WidgetLinkPrompt.IsFollowable(url, out _));

    [Fact]
    public void A_deceptive_address_is_named_in_the_question()
    {
        Assert.True(WidgetLinkPrompt.IsFollowable("https://xn--80ak6aa92e.com/", out var punycode));
        Assert.True(WidgetLinkPrompt.HasPunycode(punycode!));
        Assert.Contains(WidgetLinkPrompt.PunycodeWarning, WidgetLinkPrompt.Detail(punycode!), StringComparison.Ordinal);

        Assert.True(WidgetLinkPrompt.IsFollowable("https://bank.example.com@evil.test/", out var credentials));
        Assert.True(WidgetLinkPrompt.HasCredentials(credentials!));
        Assert.Contains(
            WidgetLinkPrompt.CredentialsWarning, WidgetLinkPrompt.Detail(credentials!), StringComparison.Ordinal);

        Assert.True(WidgetLinkPrompt.IsFollowable("https://example.com/ok", out var plain));
        var detail = WidgetLinkPrompt.Detail(plain!);
        Assert.DoesNotContain(WidgetLinkPrompt.PunycodeWarning, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(WidgetLinkPrompt.CredentialsWarning, detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_label_names_the_server_once_the_widget_has_initialized()
    {
        Assert.Equal("Rendering widget", VisualizeWidgetStrings.Rendering);
        Assert.Equal("Widget from visualize", VisualizeWidgetStrings.WidgetFrom("visualize"));
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
