using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using Xunit;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The pane asking before it opens a site a tool named. The card's own rules are
/// pinned in <see cref="PreviewOriginPromptTests"/>; what matters here is that
/// the navigate path consults it, honours the answer, and never asks twice about
/// a site the user already allowed.
/// </summary>
public class BrowserPaneOriginTests
{
    private static (BrowserPaneHandlers Handlers, StubPaneDriver Driver, List<string> Asked, HashSet<string> Allowed)
        Pane(int answer)
    {
        var driver = new StubPaneDriver();
        var asked = new List<string>();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var handlers = new BrowserPaneHandlers(driver, originPrompt: new PreviewOriginPrompt())
        {
            OriginCard = (message, _, _) =>
            {
                asked.Add(message);
                return Task.FromResult(answer);
            },
            IsOriginAllowed = allowed.Contains,
            OriginAllowed = o => allowed.Add(o),
        };
        return (handlers, driver, asked, allowed);
    }

    private static JsonObject Url(string url) => new() { ["url"] = url };

    [Fact]
    public async Task AllowingOpensTheSiteAndRemembersIt()
    {
        var (handlers, driver, asked, allowed) = Pane(answer: 0);

        var result = await handlers.NavigateAsync(Url("https://example.com/a"), null, default);

        Assert.False(result.IsError);
        Assert.Single(asked);
        Assert.Contains("https://example.com", allowed);
        Assert.Contains(driver.Calls, c => c.StartsWith("navigate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingRefusesAndDoesNotNavigate()
    {
        var (handlers, driver, asked, _) = Pane(answer: 1);

        var result = await handlers.NavigateAsync(Url("https://example.com/a"), null, default);

        Assert.True(result.IsError);
        Assert.Single(asked);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("navigate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAlreadyAllowedSiteIsNotAskedAbout()
    {
        var (handlers, _, asked, _) = Pane(answer: 0);

        await handlers.NavigateAsync(Url("https://example.com/a"), null, default);
        await handlers.NavigateAsync(Url("https://example.com/b"), null, default);

        Assert.Single(asked);
    }

    [Fact]
    public async Task EachSiteIsAskedAboutOnItsOwn()
    {
        var (handlers, _, asked, _) = Pane(answer: 0);

        await handlers.NavigateAsync(Url("https://one.example"), null, default);
        await handlers.NavigateAsync(Url("https://two.example"), null, default);

        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public async Task WithNoCardToShowThePaneIsUngated()
    {
        // A headless run and a subagent have nowhere to ask, so the tool-level
        // permission that let the call run at all is the only consent there is.
        var driver = new StubPaneDriver();
        var handlers = new BrowserPaneHandlers(driver, originPrompt: new PreviewOriginPrompt());

        var result = await handlers.NavigateAsync(Url("https://example.com/a"), null, default);

        Assert.False(result.IsError);
    }
}
