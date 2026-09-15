using JarvisCode.Core.Providers;
using JarvisCode.Providers.Anthropic;

namespace JarvisCode.Providers.Tests;

public sealed class AnthropicAuthPreviewTests
{
    [Theory]
    [InlineData("fixture-api-key", "x-api-key", "Authorization")]
    [InlineData("Bearer fixture-access-token", "Authorization", "x-api-key")]
    public async Task Redacted_preview_uses_the_same_authentication_header_as_the_wire(
        string credential, string expectedHeader, string absentHeader)
    {
        using var client = ProviderTestHelpers.ClientFor(
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n", out var handler);
        var provider = new AnthropicProvider(client, new FakeKeySource(credential));
        var request = ProviderTestHelpers.SampleRequest();
        var preview = provider.PreviewRequest(request);
        await ProviderTestHelpers.CollectAsync(provider, request);
        var authorization = Assert.Single(preview.Headers, header => header.Key == expectedHeader);
        Assert.Contains(RequestBodyOverride.RedactedValue, authorization.Value);
        Assert.DoesNotContain(credential, authorization.Value);
        Assert.DoesNotContain(preview.Headers, header => header.Key == absentHeader);
        Assert.True(handler.LastRequest!.Headers.Contains(expectedHeader));
        Assert.False(handler.LastRequest.Headers.Contains(absentHeader));
        Assert.Equal(credential, Assert.Single(handler.LastRequest.Headers.GetValues(expectedHeader)));
    }
}
