using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ChatPlaceholdersTests
{
    [Fact]
    public void A_conversation_that_has_started_says_write_a_message()
        => Assert.Equal(
            ChatPlaceholders.WriteAMessage,
            ChatPlaceholders.Resolve(new ChatPlaceholderState()));

    [Fact]
    public void A_new_conversation_says_how_can_I_help()
        => Assert.Equal(
            ChatPlaceholders.NewConversation,
            ChatPlaceholders.Resolve(new ChatPlaceholderState(NewConversation: true)));

    [Theory]
    [InlineData(ChatDictation.Connecting, ChatPlaceholders.Connecting)]
    [InlineData(ChatDictation.Listening, ChatPlaceholders.Listening)]
    [InlineData(ChatDictation.Processing, ChatPlaceholders.Processing)]
    [InlineData(ChatDictation.Speaking, ChatPlaceholders.Speaking)]
    [InlineData(ChatDictation.Reconnecting, ChatPlaceholders.Reconnecting)]
    public void Dictation_outranks_every_other_rung(ChatDictation dictation, string expected)
        => Assert.Equal(expected, ChatPlaceholders.Resolve(new ChatPlaceholderState(
            Dictation: dictation,
            Placeholder: "supplied",
            RolePickerActive: true,
            ReviewChips: true,
            NewConversation: true)));

    [Fact]
    public void A_supplied_placeholder_beats_every_conversation_rung()
        => Assert.Equal("supplied", ChatPlaceholders.Resolve(new ChatPlaceholderState(
            Placeholder: "supplied", RolePickerActive: true, NewConversation: true)));

    [Fact]
    public void The_role_picker_asks_for_something_else()
        => Assert.Equal(ChatPlaceholders.SomethingElse, ChatPlaceholders.Resolve(
            new ChatPlaceholderState(RolePickerActive: true, AnsweringQuestion: true, NewConversation: true)));

    [Fact]
    public void A_hidden_question_becomes_the_placeholder_itself()
        => Assert.Equal("Which environment?", ChatPlaceholders.Resolve(new ChatPlaceholderState(
            AnsweringQuestion: true, HiddenQuestion: "Which environment?")));

    [Fact]
    public void A_question_with_no_text_falls_to_or_reply_directly()
        => Assert.Equal(ChatPlaceholders.OrReplyDirectly, ChatPlaceholders.Resolve(
            new ChatPlaceholderState(AnsweringQuestion: true)));

    [Fact]
    public void A_model_fallback_notice_also_says_or_reply_directly()
        => Assert.Equal(ChatPlaceholders.OrReplyDirectly, ChatPlaceholders.Resolve(
            new ChatPlaceholderState(ModelFallbackNotice: true, ReviewChips: true)));

    [Fact]
    public void Review_chips_outrank_a_new_conversation()
        => Assert.Equal(ChatPlaceholders.AddressReviewComments, ChatPlaceholders.Resolve(
            new ChatPlaceholderState(ReviewChips: true, NewConversation: true)));

    [Fact]
    public void A_new_conversation_outranks_agent_and_external()
        => Assert.Equal(ChatPlaceholders.NewConversation, ChatPlaceholders.Resolve(
            new ChatPlaceholderState(NewConversation: true, AgentMode: true, ExternalConversation: true)));

    [Fact]
    public void Agent_mode_outranks_an_external_conversation()
        => Assert.Equal(ChatPlaceholders.AgentMode, ChatPlaceholders.Resolve(
            new ChatPlaceholderState(AgentMode: true, ExternalConversation: true)));

    [Fact]
    public void An_external_conversation_says_reply()
        => Assert.Equal(ChatPlaceholders.ExternalConversation, ChatPlaceholders.Resolve(
            new ChatPlaceholderState(ExternalConversation: true)));

    [Fact]
    public void An_empty_supplied_placeholder_is_not_a_placeholder()
        => Assert.Equal(ChatPlaceholders.WriteAMessage, ChatPlaceholders.Resolve(
            new ChatPlaceholderState(Placeholder: "")));
}
