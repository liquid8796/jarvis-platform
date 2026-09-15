using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ChatWelcomeTests
{
    [Fact]
    public void A_later_chat_gets_the_greeting_and_no_onboarding()
    {
        var view = ChatWelcome.Describe(firstChatEver: false, "Liquid");
        Assert.False(view.IsOnboarding);
        Assert.Equal(ChatWelcome.Greeting, view.Greeting);
        Assert.Null(view.OnboardingIntro);
    }

    [Fact]
    public void The_first_chat_of_all_gets_the_onboarding_instead_of_the_greeting()
    {
        var view = ChatWelcome.Describe(firstChatEver: true, "Liquid");
        Assert.True(view.IsOnboarding);
        Assert.Null(view.Greeting);
        Assert.Equal("Welcome, Liquid! I’m Jarvis.", view.OnboardingGreeting);
        Assert.Equal(ChatWelcome.OnboardingIntro, view.OnboardingIntro);
        Assert.Equal(ChatWelcome.OnboardingStart, view.OnboardingStart);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_name_falls_back_to_the_anonymous_greeting(string? name)
        => Assert.Equal(
            ChatWelcome.OnboardingGreetingAnonymous,
            ChatWelcome.Describe(firstChatEver: true, name).OnboardingGreeting);

    [Fact]
    public void Incognito_replaces_both_screens()
    {
        var view = ChatWelcome.Describe(firstChatEver: true, "Liquid", incognito: true);
        Assert.False(view.IsOnboarding);
        Assert.Equal(ChatWelcome.Incognito, view.Greeting);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(99)]
    public void The_poke_ladder_falls_back_to_the_greeting_outside_its_arms(int pokes)
        => Assert.StartsWith("Hi, I’m Jarvis.", ChatWelcome.PokeReply(pokes));

    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    public void Six_to_twelve_pokes_are_answered_patiently(int pokes)
        => Assert.Equal("Yes, yes. What can I do for you?", ChatWelcome.PokeReply(pokes));

    [Theory]
    [InlineData(13)]
    [InlineData(18)]
    public void Thirteen_to_eighteen_pokes_ask_whether_you_are_still_at_it(int pokes)
        => Assert.Equal("Are you still doing that?", ChatWelcome.PokeReply(pokes));

    [Theory]
    [InlineData(19)]
    [InlineData(24)]
    public void Nineteen_to_twenty_four_pokes_concede_attention(int pokes)
        => Assert.Equal("Alright, alright, you have my attention!", ChatWelcome.PokeReply(pokes));

    [Theory]
    [InlineData(25)]
    [InlineData(31)]
    public void Twenty_five_to_thirty_one_pokes_give_up(int pokes)
        => Assert.Equal("Ugh, well you can’t do that forever", ChatWelcome.PokeReply(pokes));

    [Fact]
    public void The_onboarding_mascot_writes_until_the_last_paragraph_lands()
    {
        Assert.Equal(SparkState.Writing, ChatWelcome.OnboardingMascot(revealed: false));
        Assert.Equal(SparkState.Idle, ChatWelcome.OnboardingMascot(revealed: true));
    }

    [Fact]
    public void The_greeting_metrics_are_the_measured_ones()
    {
        Assert.Equal(28, ChatWelcome.GreetingFontSize);
        Assert.Equal(36.4, ChatWelcome.GreetingLineHeight, 6);
        Assert.Equal(8, ChatWelcome.GreetingGap);
        Assert.Equal(640, ChatWelcome.GreetingRowBreakpoint);
    }
}
