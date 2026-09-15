using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Tests.Services;

public sealed class WidgetInputTests
{
    [Fact]
    public void EveryPartialJsonBoundaryPreservesTheDecodedWidgetPrefix()
    {
        const string code = "<svg><text>hello \"world\" \\ 😀\nnext</text></svg>";
        var json = JsonSerializer.Serialize(new { title = "figure", widget_code = code });
        for (var length = 0; length <= json.Length; length++)
        {
            var partial = VisualizeWidgetCalls.ParsePartial(json[..length]);
            Assert.StartsWith(partial.WidgetCode, code, StringComparison.Ordinal);
        }
        Assert.Equal(code, VisualizeWidgetCalls.ParsePartial(json).WidgetCode);
        Assert.Empty(VisualizeWidgetCalls.ParsePartial("{\"nested\":{\"widget_code\":\"wrong\"}}").WidgetCode);
    }

    [Fact]
    public void AWidgetStaysPartialUntilApprovedAndStoppingDoesNotFinalizeItsScripts()
    {
        var widget = new WidgetItem { CallId = "call", ServerName = "visualize", ToolName = "show_widget", WireName = "mcp__visualize__show_widget" };
        widget.AppendInput("{\"widget_code\":\"<h1>part");
        Assert.Equal("<h1>part", widget.WidgetCode);
        Assert.False(widget.IsInputComplete);
        widget.StopInput();
        Assert.False(widget.IsInputComplete);
        Assert.False(widget.IsInputStreaming);
        widget.CompleteInput("{\"title\":\"finished\",\"widget_code\":\"<h1>complete</h1>\"}");
        Assert.True(widget.IsInputComplete);
        Assert.Equal("<h1>complete</h1>", widget.WidgetCode);
    }

    [Fact]
    public void WidgetFilesKeepTheirBytesAndNamesWithoutOverwritingOrEscapingAttachments()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-widget-tests", Guid.NewGuid().ToString("N"));
        byte[] first = [0, 1, 2, 127, 128, 255];
        byte[] second = [42];
        var files = new JsonArray(
            new JsonObject { ["name"] = "../same.bin", ["data"] = Convert.ToBase64String(first) },
            new JsonObject { ["name"] = "C:\\outside\\same.bin", ["data"] = Convert.ToBase64String(second) });
        var paths = WidgetAttachments.Save(files, root);
        Assert.Equal(2, paths.Count);
        Assert.NotEqual(paths[0], paths[1]);
        Assert.All(paths, path => Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase));
        Assert.All(paths, path => Assert.Equal("same.bin", Path.GetFileName(path)));
        Assert.Equal(first, File.ReadAllBytes(paths[0]));
        Assert.Equal(second, File.ReadAllBytes(paths[1]));
    }

    [Fact]
    public void InvalidFileBatchCreatesNoPartialAttachments()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-widget-tests", Guid.NewGuid().ToString("N"));
        var files = new JsonArray(
            new JsonObject { ["name"] = "valid.txt", ["data"] = "aGk=" },
            new JsonObject { ["name"] = "invalid.txt", ["data"] = "not base64" });
        Assert.Throws<InvalidDataException>(() => WidgetAttachments.Save(files, root));
        Assert.False(Directory.Exists(root));
    }
}
