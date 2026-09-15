using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Anthropic;

namespace JarvisCode.Providers.Tests;

public sealed class AnthropicCitationTests
{
    private const string Citation = """{"type":"web_search_result_location","url":"https://example.com/source","title":"The source","encrypted_index":"opaque-index","cited_text":"supporting text"}""";

    [Fact]
    public async Task Stream_emits_initial_text_and_citation_deltas_with_their_block_offsets()
    {
        var sse = "data: {\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"text\",\"text\":\"One.\",\"citations\":[" + Citation + "]}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"text_delta\",\"text\":\" Two.\"}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"citations_delta\",\"citation\":" + Citation + "}}\n\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":5}}\n\n";
        var provider = new AnthropicProvider(ProviderTestHelpers.ClientFor(sse, out _), new FakeKeySource());
        var events = await ProviderTestHelpers.CollectAsync(provider, ProviderTestHelpers.SampleRequest());
        Assert.Equal(new[] { "One.", " Two." }, events.OfType<TextDeltaEvent>().Select(e => e.Delta));
        Assert.All(events.OfType<TextDeltaEvent>(), e => Assert.Equal(2, e.Index));
        Assert.Equal(new[] { 4, 9 }, events.OfType<CitationDeltaEvent>().Select(e => e.Citation.TextOffset));
        Assert.All(events.OfType<CitationDeltaEvent>(), e =>
        {
            Assert.Equal(2, e.Index);
            Assert.Equal("anthropic", e.Citation.ProviderId);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Citation), JsonNode.Parse(e.Citation.RawJson)));
        });
    }

    [Fact]
    public void Switching_provider_keeps_visible_text_but_does_not_replay_another_providers_opaque_tokens()
    {
        var answer = new TextBlock("A claim.")
        { Citations = [new TextCitation(Citation, 8) { ProviderId = "anthropic" }] };
        var body = AnthropicProvider.BuildRequestBody(ProviderTestHelpers.SampleRequest() with
        {
            Messages = [ChatMessage.FromUserText("ask"), new ChatMessage(Role.Assistant, [answer])],
        }, "bedrock");
        var block = body["messages"]![1]!["content"]![0]!;
        Assert.Equal("A claim.", block["text"]!.GetValue<string>());
        Assert.Null(block["citations"]);
    }

    [Fact]
    public void Replay_preserves_opaque_citation_fields_without_putting_display_links_on_the_wire()
    {
        var answer = new TextBlock("A claim.") { Citations = [new TextCitation(Citation, 8)] };
        var body = AnthropicProvider.BuildRequestBody(ProviderTestHelpers.SampleRequest() with
        {
            Messages = [ChatMessage.FromUserText("ask"), new ChatMessage(Role.Assistant, [answer]), ChatMessage.FromUserText("continue")],
        });
        var block = body["messages"]![1]!["content"]![0]!;
        Assert.Equal("A claim.", block["text"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Citation), block["citations"]![0]));
        Assert.Null(block["citations"]![0]!["TextOffset"]);
    }
}
