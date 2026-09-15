using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.Providers.Tests;

public sealed class ChatGptAuxiliaryCapabilityTests
{
    [Fact]
    public void BrowserSessionDeclaresThatEmptyJarvisToolsCannotDisableTheWebsitesNativeTools()
    {
        var provider = new ChatGptWebProvider(new NoNetworkCookies(), new Options());

        Assert.False(provider.Capabilities.SupportsToolFreeInference);
        Assert.False(ProviderCapabilities.For(new Wrapper(new Wrapper(provider))).SupportsToolFreeInference);
        Assert.True(new ProviderCapabilities().SupportsToolFreeInference);
    }

    private sealed class NoNetworkCookies : IApiKeySource
    {
        public string? GetKey(string providerId) => throw new InvalidOperationException("Capability checks must not authenticate.");
    }

    private sealed class Options : IChatGptWebOptions
    {
        public string ChatGptProjectName => "";
        public string ChatGptProjectId => "";
        public int ChatGptRotateAfterMessages => 100;
        public IReadOnlyList<ChatGptChatEntry> ChatGptChats => [];
        public void SaveChatGptProjectId(string projectId) => throw new NotSupportedException();
        public void SaveChatGptChats(IReadOnlyList<ChatGptChatEntry> chats) => throw new NotSupportedException();
    }

    private sealed class Wrapper(ILlmProvider inner) : ILlmProvider, IDecoratedProvider
    {
        public ILlmProvider InnerProvider => inner;
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("Capability checks must not send a model request.");
    }
}
