using System.IO;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ChatConversationSettingsTests
{
    [Fact]
    public void An_untouched_conversation_takes_the_reference_defaults()
    {
        var state = ChatConversationSettings.Resolve(null, webSearchDefault: false);
        Assert.False(state.WebSearch);
        Assert.True(state.ExtendedThinking);
        // The reference reads "off" === (tool_search_mode ?? "auto") ? "off" : "on".
        Assert.Equal(ChatToolAccess.LoadWhenNeeded, state.ToolAccess);
        Assert.Empty(state.Connectors);
    }

    [Fact]
    public void Web_search_follows_the_app_default_until_the_conversation_says_otherwise()
    {
        Assert.True(ChatConversationSettings.Resolve(null, webSearchDefault: true).WebSearch);
        Assert.False(ChatConversationSettings
            .Resolve(new ChatConversationSetting { WebSearch = false }, webSearchDefault: true).WebSearch);
    }

    [Fact]
    public void Only_the_reference_off_value_means_already_loaded()
    {
        Assert.Equal(
            ChatToolAccess.AlreadyLoaded,
            ChatConversationSettings.Resolve(new ChatConversationSetting { ToolAccess = "off" }, false).ToolAccess);
        Assert.Equal(
            ChatToolAccess.LoadWhenNeeded,
            ChatConversationSettings.Resolve(new ChatConversationSetting { ToolAccess = "auto" }, false).ToolAccess);
        Assert.Equal(
            ChatToolAccess.LoadWhenNeeded,
            ChatConversationSettings.Resolve(new ChatConversationSetting { ToolAccess = "on" }, false).ToolAccess);
    }

    [Fact]
    public void Extended_thinking_is_on_unless_the_conversation_turned_it_off()
    {
        Assert.False(ChatConversationSettings
            .Resolve(new ChatConversationSetting { ExtendedThinking = false }, false).ExtendedThinking);
        Assert.True(ChatConversationSettings
            .Resolve(new ChatConversationSetting { ExtendedThinking = true }, false).ExtendedThinking);
    }

    [Fact]
    public void Switches_round_trip_through_the_store()
    {
        var file = Path.Combine(Path.GetTempPath(), "jarvis-chatconv-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var settings = new ChatConversationSettings(new UiSettingsStore(file));
            settings.SetWebSearch("s1", true);
            settings.SetExtendedThinking("s1", false);
            settings.SetToolAccess("s1", ChatToolAccess.AlreadyLoaded);
            settings.SetConnector("s1", "linear", true);
            settings.SetConnector("s1", "sentry", true);
            settings.SetConnector("s1", "sentry", false);

            var reopened = new ChatConversationSettings(new UiSettingsStore(file)).Get("s1", webSearchDefault: false);
            Assert.True(reopened.WebSearch);
            Assert.False(reopened.ExtendedThinking);
            Assert.Equal(ChatToolAccess.AlreadyLoaded, reopened.ToolAccess);
            Assert.Equal(["linear"], reopened.Connectors);

            // A conversation nobody touched is unaffected by another's switches.
            Assert.True(new ChatConversationSettings(new UiSettingsStore(file)).Get("s2", false).ExtendedThinking);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Forgetting_a_conversation_drops_its_switches()
    {
        var file = Path.Combine(Path.GetTempPath(), "jarvis-chatconv-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var store = new UiSettingsStore(file);
            var settings = new ChatConversationSettings(store);
            settings.SetWebSearch("s1", true);
            settings.Forget("s1");
            Assert.Empty(store.Current.ChatConversations);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
