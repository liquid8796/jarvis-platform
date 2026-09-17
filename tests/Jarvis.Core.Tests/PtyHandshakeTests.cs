using Jarvis.Agent.Core;

namespace Jarvis.Core.Tests;

public sealed class PtyHandshakeTests
{
    [Fact]
    public void Startup_query_can_cross_buffer_boundaries_and_is_answered_only_once()
    {
        var parser = new PtyStartupHandshake();
        Assert.False(parser.Observe("\u001b[1t\u001b["));
        Assert.True(parser.Observe("c"));
        Assert.False(parser.Observe("\u001b[c\u001b[0c"));
    }
    [Theory]
    [InlineData("text [c")]
    [InlineData("\u001b[?1;2c")]
    [InlineData("\u001b[>0c")]
    [InlineData("\u001b[5c")]
    [InlineData("\u001b]0;[c\a")]
    public void Does_not_answer_plain_text_or_feature_reports(string text) => Assert.False(new PtyStartupHandshake().Observe(text));
    [Fact]
    public void Zero_parameter_primary_query_is_supported() => Assert.True(new PtyStartupHandshake().Observe("\u001b[0c"));
}
