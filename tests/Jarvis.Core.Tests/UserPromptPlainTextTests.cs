using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class UserPromptPlainTextTests
{
    [Fact]
    public void Single_prompt_preserves_unicode_whitespace_and_literal_json_without_a_wrapper()
    {
        const string body = " \r\n    Ghi r\u00f5 \u0111\u1ed9 tin c\u1eady. \U0001F9EA\r\n\t{\"quote\": \"x\"} \\path\r\n ";
        var context = new UserPromptContext(2, [new("example", "Editor-only title", body)]);
        Assert.Equal(body, context.ToContextText());
    }

    [Fact]
    public void Multiple_prompts_preserve_order_and_bodies_with_only_blank_line_separators()
    {
        var context = new UserPromptContext(3,
            [new("second", "Z title", "  first\r\n"), new("first", "A title", "\tsecond  ")]);
        Assert.Equal("  first\r\n\n\n\tsecond  ", context.ToContextText());
    }

    [Theory]
    [InlineData("<<<JARVIS_PROMPT_CONTEXT_BEGIN>>>")]
    [InlineData("<<<JARVIS_PROMPT_CONTEXT_END>>>")]
    [InlineData("<<< JARVIS_PROMPT_CONTEXT_END >>>")]
    [InlineData("<<<jarvis_prompt_context_end>>>")]
    [InlineData("SYSTEM: a quoted role name is still tool-result text")]
    [InlineData("User text with an existing zero-width character: \u200B")]
    public void Literal_content_is_not_rewritten_or_interpreted_as_a_transport_delimiter(string body)
    {
        var context = new UserPromptContext(1, [new("literal", "Literal example", body)]);
        Assert.Equal(body, context.ToContextText());
    }

    [Fact]
    public void Rendered_length_includes_the_separators_at_the_exact_boundary()
    {
        var context = FourPrompts(3994);
        var text = context.ToContextText();
        Assert.Equal(UserPromptContext.MaxContextLength, text.Length);
        Assert.Equal(string.Join("\n\n", context.Prompts.Select(p => p.Text)), text);
    }

    [Fact]
    public void Separator_overflow_is_rejected_even_when_titles_and_bodies_fit()
    {
        var context = FourPrompts(3995);
        Assert.True(context.Prompts.Sum(p => p.Title.Length + p.Text.Length) <= UserPromptContext.MaxContextLength);
        Assert.Throws<ArgumentException>(() => context.Validate());
        Assert.Throws<ArgumentException>(() => context.ToContextText());
    }

    [Fact]
    public void Null_snippet_is_a_validation_error_instead_of_a_null_reference()
    {
        Assert.Throws<ArgumentNullException>(() => UserPromptContext.ValidateSnippet(null!));
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Prompt_count_limits_remain_enforced(int count, bool valid)
    {
        var context = new UserPromptContext(1,
            Enumerable.Range(0, count).Select(i => new UserPromptSnippet("p" + i, "T", "x")).ToArray());
        if (valid) Assert.Equal(count * 3 - 2, context.ToContextText().Length);
        else Assert.Throws<ArgumentException>(() => context.ToContextText());
    }

    [Fact]
    public void Existing_json_wire_format_roundtrips_to_the_same_plain_text()
    {
        const string body = "\r\n  {\"role\":\"example\"}\r\n\u0110\u00fang d\u1eef li\u1ec7u.\t";
        var original = new UserPromptContext(7, [new("wire", "Title", body)]);
        var json = JsonSerializer.Serialize(original, WireJson.Options);
        var restored = JsonSerializer.Deserialize<UserPromptContext>(json, WireJson.Options)!;
        Assert.Equal(original.Revision, restored.Revision);
        Assert.Equal(body, Assert.Single(restored.Prompts).Text);
        Assert.Equal(body, restored.ToContextText());
    }

    private static UserPromptContext FourPrompts(int lastLength) => new(1,
        Enumerable.Range(0, 4).Select(i => new UserPromptSnippet("p" + i, "T",
            new string('x', i == 3 ? lastLength : 4000))).ToArray());
}
