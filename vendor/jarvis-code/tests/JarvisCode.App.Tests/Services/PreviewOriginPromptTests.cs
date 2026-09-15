using JarvisCode.App.Services;
using Xunit;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The Browser pane's per-origin consent. What is pinned here is the reference's
/// own arithmetic - three refusals of one origin, nine across the session - and
/// that an allowed origin starts counting again from nothing.
/// </summary>
public class PreviewOriginPromptTests
{
    private static Func<string, string, CancellationToken, Task<int>> Answers(int button, List<string>? seen = null) =>
        (message, _, _) =>
        {
            seen?.Add(message);
            return Task.FromResult(button);
        };

    [Fact]
    public async Task AllowIsTheFirstButton()
    {
        var prompt = new PreviewOriginPrompt();

        Assert.Equal(
            PreviewOriginDecision.Allowed,
            await prompt.AskAsync("https://a.example", PreviewOriginCategory.Ordinary, Answers(0), default));
    }

    [Fact]
    public async Task CancelRefuses()
    {
        var prompt = new PreviewOriginPrompt();

        Assert.Equal(
            PreviewOriginDecision.DeniedByUser,
            await prompt.AskAsync("https://a.example", PreviewOriginCategory.Ordinary, Answers(1), default));
    }

    [Fact]
    public async Task AnOriginStopsAskingAfterThreeRefusals()
    {
        var prompt = new PreviewOriginPrompt();
        var asked = new List<string>();

        for (var i = 0; i < 4; i++)
        {
            await prompt.AskAsync("https://a.example", PreviewOriginCategory.Ordinary, Answers(1, asked), default);
        }

        Assert.Equal(3, asked.Count);
        Assert.True(prompt.IsSuppressed("https://a.example"));
        Assert.False(prompt.IsSuppressed("https://b.example"));
    }

    [Fact]
    public async Task TheSessionStopsAskingAfterNineRefusals()
    {
        var prompt = new PreviewOriginPrompt();
        var asked = new List<string>();

        // Three each across four origins would be twelve, but the session cap
        // stops it at nine - and then stops asking about a fresh origin too.
        foreach (var host in new[] { "a", "b", "c", "d" })
        {
            for (var i = 0; i < 3; i++)
            {
                await prompt.AskAsync($"https://{host}.example", PreviewOriginCategory.Ordinary, Answers(1, asked), default);
            }
        }

        Assert.Equal(9, asked.Count);
        Assert.True(prompt.IsSuppressed("https://never-asked.example"));
    }

    [Fact]
    public async Task AllowingClearsThatOriginsCount()
    {
        var prompt = new PreviewOriginPrompt();
        var asked = new List<string>();

        await prompt.AskAsync("https://a.example", PreviewOriginCategory.Ordinary, Answers(1, asked), default);
        await prompt.AskAsync("https://a.example", PreviewOriginCategory.Ordinary, Answers(1, asked), default);
        await prompt.AskAsync("https://a.example", PreviewOriginCategory.Ordinary, Answers(0, asked), default);

        Assert.False(prompt.IsSuppressed("https://a.example"));

        // Its own three are available again rather than one.
        for (var i = 0; i < 3; i++)
        {
            await prompt.AskAsync("https://a.example", PreviewOriginCategory.Ordinary, Answers(1, asked), default);
        }

        Assert.Equal(6, asked.Count);
        Assert.True(prompt.IsSuppressed("https://a.example"));
    }

    [Fact]
    public async Task ASuppressedOriginRefusesWithoutAsking()
    {
        var prompt = new PreviewOriginPrompt();
        var asked = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            await prompt.AskAsync("https://a.example", PreviewOriginCategory.Ordinary, Answers(1, asked), default);
        }

        var decision = await prompt.AskAsync(
            "https://a.example", PreviewOriginCategory.Ordinary, Answers(0, asked), default);

        Assert.Equal(PreviewOriginDecision.DeniedByUser, decision);
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public async Task NoOriginIsNotPrompted()
    {
        var prompt = new PreviewOriginPrompt();

        Assert.Equal(
            PreviewOriginDecision.NotPrompted,
            await prompt.AskAsync("", PreviewOriginCategory.Ordinary, Answers(0), default));
    }

    [Fact]
    public void TheMessageNamesTheOrigin()
    {
        Assert.Equal(
            "This page embeds content from https://a.example. Allow Jarvis to click inside it?",
            PreviewOriginPrompt.Message("https://a.example"));
    }

    [Fact]
    public void EveryCardCarriesTheEmbeddedLine()
    {
        Assert.Equal(PreviewOriginPrompt.EmbeddedDetail, PreviewOriginPrompt.Detail(PreviewOriginCategory.Ordinary));
    }

    [Fact]
    public void ACategoryAddsItsOwnLineBelowTheFirst()
    {
        var financial = PreviewOriginPrompt.Detail(PreviewOriginCategory.Financial);
        var risky = PreviewOriginPrompt.Detail(PreviewOriginCategory.HigherRisk);

        Assert.Equal(
            PreviewOriginPrompt.EmbeddedDetail + "\n\n" + PreviewOriginPrompt.FinancialDetail, financial);
        Assert.Equal(
            PreviewOriginPrompt.EmbeddedDetail + "\n\n" + PreviewOriginPrompt.HigherRiskDetail, risky);
    }

    [Fact]
    public void CancelIsBothTheDefaultAndTheEscape()
    {
        Assert.Equal(["Allow", "Cancel"], PreviewOriginPrompt.Buttons);
        Assert.Equal("Cancel", PreviewOriginPrompt.Buttons[PreviewOriginPrompt.DefaultButton]);
    }
}
