using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class CodexViewImageToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-view-image-" + Guid.NewGuid().ToString("N"));
    private readonly string _private = Path.Combine(Path.GetTempPath(), "jarvis-view-private-" + Guid.NewGuid().ToString("N"));

    public CodexViewImageToolTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_private);
    }

    [Fact]
    public async Task High_detail_returns_bounded_png_preview_and_wire_image()
    {
        var path = Path.Combine(_root, "sample.png");
        using (var bitmap = new Bitmap(320, 180, PixelFormat.Format32bppArgb))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.CornflowerBlue);
            graphics.FillRectangle(Brushes.Gold, 25, 25, 100, 75);
            bitmap.Save(path, ImageFormat.Png);
        }

        var tool = new CodexViewImageTool(_private);
        Assert.Equal("image.view_image", tool.Descriptor.Id);
        Assert.Equal("view_image", tool.Descriptor.Name);
        var reply = await tool.ExecuteAsync(WireJson.Element(new { path = "sample.png", detail = "high" }),
            new AgentExecutionContext(_root, "call", "session"), CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        var image = Assert.Single(reply.Images!);
        Assert.Equal("image/png", image.MimeType);
        Assert.NotEmpty(Convert.FromBase64String(image.Base64));
        using var json = JsonDocument.Parse(reply.Text);
        Assert.Equal(320, json.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(180, json.RootElement.GetProperty("height").GetInt32());
        Assert.StartsWith("data:image/png;base64,", json.RootElement.GetProperty("image_url").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Original_detail_preserves_original_encoded_bytes()
    {
        var path = Path.Combine(_root, "sample.jpg");
        using (var bitmap = new Bitmap(40, 30)) bitmap.Save(path, ImageFormat.Jpeg);
        var expected = await File.ReadAllBytesAsync(path);
        var reply = await new CodexViewImageTool(_private).ExecuteAsync(
            WireJson.Element(new { path, detail = "original" }),
            new AgentExecutionContext(_root, "call", "session"), CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        var image = Assert.Single(reply.Images!);
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal(expected, Convert.FromBase64String(image.Base64));
    }

    [Fact]
    public async Task Rejects_private_profile_image()
    {
        var path = Path.Combine(_private, "secret.png");
        using (var bitmap = new Bitmap(1, 1)) bitmap.Save(path, ImageFormat.Png);
        var reply = await new CodexViewImageTool(_private).ExecuteAsync(
            WireJson.Element(new { path, detail = "high" }),
            new AgentExecutionContext(_root, "call", "session"), CancellationToken.None);
        Assert.True(reply.IsError);
        Assert.Contains("private profile", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_private)) Directory.Delete(_private, true);
    }
}
