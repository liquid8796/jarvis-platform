using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace Jarvis.Agent.Windows.Tests;

public sealed class BrowserDocumentReferenceTests
{
    [Theory]
    [InlineData("ref_daaa_12", 12, 0, "aaa")]
    [InlineData("ref_dbbb_f3r12", 12, 3, "bbb")]
    [InlineData("ref_f3r12", 12, 3, null)]
    public void References_preserve_document_and_frame_identity(string text, int expected, int frame, string? document)
    {
        var parsed = ElementRef.Parse(JsonValue.Create(text));
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.Value.Value);
        Assert.Equal(frame, parsed.Value.FrameId);
        Assert.Equal(document, parsed.Value.DocumentId);
        Assert.Equal(text, parsed.Value.ToString());
        var arguments = new JsonObject();
        parsed.Value.ApplyTo(arguments);
        Assert.Equal(expected, arguments["ref"]!.GetValue<int>());
        Assert.Equal(document, arguments["documentId"]?.GetValue<string>());
    }
}
