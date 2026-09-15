using System.Text.Json;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Tests.Agent;

public sealed class CitationTests
{
    private const string Source = """{"type":"web_search_result_location","url":"https://example.com/facts","title":"Source [one]","encrypted_index":"opaque","cited_text":"claim"}""";

    [Fact]
    public async Task Citation_stays_on_its_text_block_through_stream_accumulation_and_session_roundtrip()
    {
        var citation = new TextCitation(Source, 6);
        var provider = new ScriptedProvider(
        [
            new TextDeltaEvent("Claim.", 0),
            new CitationDeltaEvent(0, citation),
            new TextDeltaEvent(" More text.", 0),
            new TextDeltaEvent("Another block.", 2),
            new ResponseCompletedEvent(false, new Usage(1, 1), StopReasons.EndTurn),
        ]);
        var context = new AgentTurnContext
        {
            Provider = provider, ModelId = "scripted", SystemPrompt = "test",
            Messages = new List<ChatMessage> { ChatMessage.FromUserText("go") },
            Tools = new ToolRegistry([]), PermissionGate = new AutoApprovePermissionGate(),
            ToolContext = new ToolExecutionContext { WorkingDirectory = Path.GetTempPath() },
        };
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentOrchestrator().RunTurnAsync(context, default)) events.Add(evt);
        Assert.Single(events.OfType<AssistantCitationDelta>());
        var message = Assert.Single(events.OfType<AssistantMessageCompleted>()).Message;
        var blocks = message.Content.OfType<TextBlock>().ToArray();
        Assert.Equal(2, blocks.Length);
        Assert.Equal(citation, Assert.Single(blocks[0].Citations!));
        Assert.Null(blocks[1].Citations);
        Assert.Equal("Claim. More text.Another block.", message.GetText());
        Assert.Equal("Claim. [Source \\[one\\]](<https://example.com/facts>) More text.Another block.", message.GetDisplayText());
        var restored = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message))!;
        Assert.Equal(message.GetDisplayText(), restored.GetDisplayText());
        Assert.Equal(Source, Assert.Single(restored.Content.OfType<TextBlock>().First().Citations!).RawJson);
        Assert.Null(JsonSerializer.Deserialize<ChatMessage>("""{"Role":1,"Content":[{"type":"text","Text":"old session"}]}""")!
            .Content.OfType<TextBlock>().Single().Citations);
    }

    [Fact]
    public void Citation_display_rejects_active_or_credentialed_urls_and_keeps_document_page_locators()
    {
        Assert.DoesNotContain("](", CitationMarkdown.Link(new TextCitation("""{"url":"javascript:alert(1)","title":"bad"}""", 0)));
        Assert.DoesNotContain("](", CitationMarkdown.Link(new TextCitation("""{"url":"https://user:secret@example.com/","title":"bad"}""", 0)));
        Assert.Equal(" [Report, p. 3-4]", CitationMarkdown.Link(new TextCitation(
            """{"type":"page_location","document_title":"Report","start_page_number":3,"end_page_number":5}""", 0)));
    }

    [Fact]
    public void Citation_markdown_becomes_a_real_source_link_even_with_parentheses_and_brackets()
    {
        var block = new TextBlock("Claim.") { Citations = [new TextCitation(
            """{"url":"https://example.com/a)b","title":"Source [one]"}""", 6)] };
        var paragraph = Assert.IsType<JarvisCode.Core.Markdown.ParagraphBlock>(
            Assert.Single(JarvisCode.Core.Markdown.MarkdownParser.Parse(CitationMarkdown.Format(block))));
        var link = Assert.Single(paragraph.Inlines, run => run.LinkUrl is not null);
        Assert.Equal("Source [one]", link.Text);
        Assert.Equal("https://example.com/a%29b", link.LinkUrl);
    }
}
