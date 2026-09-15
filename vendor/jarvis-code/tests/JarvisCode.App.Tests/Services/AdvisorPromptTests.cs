using JarvisCode.App.Services;
using JarvisCode.Core.Models;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The advisor's gate and the conversation it forwards. The block itself is
/// pinned byte for byte by the ported-text check; these are the rules around it.
/// </summary>
public sealed class AdvisorPromptTests
{
    private static string? NoEnvironment(string name) => null;

    [Fact]
    public void The_block_is_the_references_own_length()
    {
        // 2,017 characters, measured off CLI 2.1.257's NQt.
        Assert.Equal(2017, AdvisorPrompt.Block.Length);
        Assert.StartsWith("# Advisor Tool", AdvisorPrompt.Block, StringComparison.Ordinal);
    }

    [Fact]
    public void No_advisor_model_means_no_block()
    {
        Assert.False(AdvisorPrompt.IsEnabled(null, NoEnvironment));
        Assert.False(AdvisorPrompt.IsEnabled("   ", NoEnvironment));
        Assert.True(AdvisorPrompt.IsEnabled("claude-opus-4-5", NoEnvironment));
    }

    [Fact]
    public void The_references_disable_variable_wins()
    {
        static string? Disabled(string name) =>
            name == AdvisorPrompt.DisableVariable ? "1" : null;

        Assert.False(AdvisorPrompt.IsEnabled("claude-opus-4-5", Disabled));
    }

    [Fact]
    public void The_whole_conversation_is_forwarded_as_one_user_turn()
    {
        List<ChatMessage> conversation =
        [
            ChatMessage.FromUserText("fix the parser"),
            new ChatMessage(Role.Assistant, [new TextBlock("I will start with the lexer.")]),
        ];

        var forwarded = AdvisorConversation.Forwardable(conversation);

        var only = Assert.Single(forwarded);
        Assert.Equal(Role.User, only.Role);
        var text = only.GetText();
        Assert.Contains("fix the parser", text, StringComparison.Ordinal);
        Assert.Contains("I will start with the lexer.", text, StringComparison.Ordinal);
        Assert.Contains("<conversation>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_with_two_text_blocks_is_rendered_once()
    {
        List<ChatMessage> conversation =
        [
            new ChatMessage(Role.User, [new TextBlock("first half"), new TextBlock("second half")]),
        ];

        var text = AdvisorConversation.Forwardable(conversation)[0].GetText();

        // VisibleText answers for the message, so asking it per block would
        // repeat the whole turn once per text block.
        var occurrences = text.Split("first half").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Tool_calls_and_their_results_are_forwarded_too()
    {
        // The block promises the advisor sees "every tool call you've made,
        // every result you've seen".
        List<ChatMessage> conversation =
        [
            ChatMessage.FromUserText("why is the build red?"),
            new ChatMessage(Role.Assistant, [new ToolCallBlock("t1", "Bash", "{\"command\":\"dotnet build\"}")]),
            ChatMessage.FromToolResults([new ToolResultBlock("t1", "Bash", "CS0103: name not found", true)]),
        ];

        var text = AdvisorConversation.Forwardable(conversation)[0].GetText();

        Assert.Contains("Bash", text, StringComparison.Ordinal);
        Assert.Contains("dotnet build", text, StringComparison.Ordinal);
        Assert.Contains("CS0103: name not found", text, StringComparison.Ordinal);
        Assert.Contains("error", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_very_long_tool_result_is_clipped_rather_than_forwarded_whole()
    {
        List<ChatMessage> conversation =
        [
            ChatMessage.FromToolResults(
                [new ToolResultBlock("t1", "Read", new string('z', 9000), false)]),
        ];

        var text = AdvisorConversation.Forwardable(conversation)[0].GetText();

        Assert.Contains("chars]", text, StringComparison.Ordinal);
        Assert.True(text.Length < 5000, $"result was not clipped: {text.Length} chars");
    }

    [Fact]
    public void An_oversized_conversation_keeps_the_newest_turns_and_says_so()
    {
        var conversation = new List<ChatMessage>();
        for (var i = 0; i < 400; i++)
        {
            conversation.Add(ChatMessage.FromUserText($"turn {i} " + new string('x', 800)));
        }

        var text = AdvisorConversation.Forwardable(conversation)[0].GetText();

        Assert.Contains("[earlier turns omitted for length]", text, StringComparison.Ordinal);
        // The advice is about where the session is now, so the tail survives.
        Assert.Contains("turn 399", text, StringComparison.Ordinal);
        Assert.DoesNotContain("turn 0 ", text, StringComparison.Ordinal);
    }
}
