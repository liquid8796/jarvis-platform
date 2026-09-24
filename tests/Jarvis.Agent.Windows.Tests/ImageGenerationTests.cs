using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.ImageGeneration;
using Jarvis.Agent.Desktop.ViewModels;
using Jarvis.Agent.Windows;
using Jarvis.Agent.Windows.ImageGeneration;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class ImageGenerationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-imagegen-windows-" + Guid.NewGuid().ToString("N"));
    private const string JobId = "ig_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public ImageGenerationTests() => Directory.CreateDirectory(_root);
    [Fact]
    public void BrowserBindingIsLocalOptimisticAndStableForSameInstance()
    {
        var store = new ImageGenBrowserBindingStore(_root); var instance = Guid.NewGuid().ToString();
        Assert.Null(store.Read());
        var binding = store.Save(instance, "chrome", null);
        Assert.Equal(binding, store.Read());
        Assert.Equal(binding, store.Save(instance, "chrome", binding.Revision));
        Assert.Throws<IOException>(() => store.Save(Guid.NewGuid().ToString(), "edge", null));
        Assert.Throws<IOException>(() => store.Clear(null));
        store.Clear(binding.Revision); Assert.Null(store.Read());
    }
    [Theory]
    [InlineData("dev")] [InlineData("auto")] [InlineData("extension")]
    public void NewBrowserProfilesAndAmbiguousFamiliesCannotBeBound(string family) =>
        Assert.Throws<IOException>(() => new ImageGenBrowserBindingStore(_root).Save(Guid.NewGuid().ToString(), family, null));
    [Fact]
    public void InvalidBindingJsonIsPreservedInsteadOfReset()
    {
        var store = new ImageGenBrowserBindingStore(_root); File.WriteAllText(store.FilePath, "{invalid");
        Assert.ThrowsAny<Exception>(() => store.Read()); Assert.Equal("{invalid", File.ReadAllText(store.FilePath));
    }
    [Fact]
    public async Task OriginalsRemainByteExactAndPreviewsPreserveTransparency()
    {
        var store = new ImageArtifactStore(); var bytes = MakePng(1200, 800);
        var download = Path.Combine(_root, "Downloads", "JarvisImageGen", JobId, "result-0.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(download)!); await File.WriteAllBytesAsync(download, bytes);
        var input = await store.StageInputsAsync([download], [], Path.Combine(_root, "stage"), default);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(input.Single().LocalPath));
        var result = await store.ImportResultsAsync(Result(download, bytes.Length), input, Path.Combine(_root, "result"), JobId, default);
        var image = Assert.Single(result);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(image.LocalPath));
        Assert.Equal(ImageArtifactStore.Hash(bytes), image.Sha256);
        Assert.Equal(input[0].ArtifactId, Assert.Single(image.ParentArtifactIds));
        Assert.True(File.Exists(download));
        var preview = Assert.Single(await store.ReadPreviewsAsync(result, default));
        var previewBytes = Convert.FromBase64String(preview.Base64);
        Assert.True(previewBytes.Length <= ImageArtifactStore.MaxPreviewBytes);
        using var stream = new MemoryStream(previewBytes);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.True(frame.PixelWidth <= 768);
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4]; bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4 + 3] < 255);
    }
    [Fact]
    public async Task HtmlDisguisedAsPngAndUnrelatedDownloadAreRejected()
    {
        var file = Path.Combine(_root, "bad.png"); await File.WriteAllTextAsync(file, "<html>not an image</html>");
        var store = new ImageArtifactStore();
        await Assert.ThrowsAsync<ImageGenerationException>(() => store.StageInputsAsync([file], [], Path.Combine(_root, "job"), default));
        await Assert.ThrowsAsync<IOException>(() => store.ImportResultsAsync(Result(file, new FileInfo(file).Length), [], Path.Combine(_root, "job2"), JobId, default));
    }
    [Fact]
    public async Task DownloadMetadataMustMatchTheActualOriginal()
    {
        var path = Path.Combine(_root, "JarvisImageGen", JobId, "result-0.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, MakePng(8, 8));
        var store = new ImageArtifactStore();
        await Assert.ThrowsAsync<ImageGenerationException>(() => store.ImportResultsAsync(Result(path, 1), [], Path.Combine(_root, "job"), JobId, default));
        var foreign = "ig_" + new string('b', 32);
        await Assert.ThrowsAsync<IOException>(() => store.ImportResultsAsync(Result(path, new FileInfo(path).Length), [], Path.Combine(_root, "job"), foreign, default));
    }
    [Fact]
    public async Task OversizedInputsAreRejectedBeforeDecodingOrCopying()
    {
        var file = Path.Combine(_root, "large.png");
        using (var stream = File.Create(file)) stream.SetLength(ImageArtifactStore.MaxInputBytes + 1);
        await Assert.ThrowsAsync<ImageGenerationException>(() => new ImageArtifactStore().StageInputsAsync([file], [], Path.Combine(_root, "job"), default));
    }
    [Fact]
    public async Task BackendUsesOnlyPinnedExtensionRouteAndDoesNotAcquireCredentials()
    {
        var runtime = new Runtime(); var bindings = new ImageGenBrowserBindingStore(_root);
        var binding = bindings.Save(Guid.NewGuid().ToString(), "chrome", null);
        var backend = new ChatGptExtensionImageBackend(runtime, bindings);
        var context = new AgentExecutionContext(_root, "test-call", "js_" + new string('a', 32)) { OwnerId = "owner", AgentDeviceId = "device" };
        Assert.Equal(binding, await backend.GetBindingAsync(context, default));
        var job = new ImageGenerationJob { JobId = JobId, OwnerId = "owner", DeviceId = "device", SessionId = context.SessionId,
            RequestId = "r", RequestDigest = "h", Prompt = "A test image", Binding = binding };
        await backend.CallAsync("submit", job, context, default);
        Assert.Equal("imagegen.submit", runtime.Last!.ToolId);
        Assert.Equal("extension", runtime.Last.Context.BrowserFamily);
        Assert.Equal(binding.ExtensionInstanceId, runtime.Last.Arguments.GetProperty("instanceId").GetString());
        Assert.Equal("A test image", runtime.Last.Arguments.GetProperty("prompt").GetString());
        Assert.DoesNotContain("cookie", runtime.Last.Arguments.GetRawText(), StringComparison.OrdinalIgnoreCase);
        bindings.Clear(binding.Revision);
        await Assert.ThrowsAsync<ImageGenerationException>(() => backend.CallAsync("submit", job, context, default));
        await Assert.ThrowsAsync<ArgumentException>(() => backend.CallAsync("http", job, context, default));
    }
    [Fact]
    public async Task DesktopSettingsNeverGuessOrBindTheFirstConnectedBrowser()
    {
        var runtime = new Runtime();
        runtime.Connections = new JsonArray(new JsonObject { ["id"] = "b1", ["name"] = "Chrome", ["ready"] = true,
            ["active"] = true, ["extensionInstanceId"] = Guid.NewGuid().ToString() });
        var vm = new ImageGenViewModel(_root, () => runtime);
        await vm.RefreshAsync(); Assert.Single(vm.Browsers); Assert.Null(vm.SelectedBrowser);
        Assert.Null(new ImageGenBrowserBindingStore(_root).Read());
        vm.SelectedBrowser = vm.Browsers[0]; vm.BindCommand.Execute(null);
        Assert.Equal(vm.SelectedBrowser.InstanceId, new ImageGenBrowserBindingStore(_root).Read()!.ExtensionInstanceId);
        vm.ClearCommand.Execute(null); Assert.Null(new ImageGenBrowserBindingStore(_root).Read());
    }
    private static JsonObject Result(string path, long size) => new() { ["downloads"] = new JsonArray(new JsonObject
    { ["filename"] = path, ["state"] = "complete", ["downloadId"] = 7, ["fileSize"] = size }) };
    private static byte[] MakePng(int width, int height)
    {
        var bytes = new byte[width * height * 4];
        for (var i = 4; i < bytes.Length; i += 4) { bytes[i] = 200; bytes[i + 2] = 100; bytes[i + 3] = 128; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Runtime : IBrowserRuntimeClient
    {
        public event Action<string>? ApplicationStopRequested { add { } remove { } }
        public BrowserRuntimeHandshake? Handshake => null;
        public bool IsReady => true;
        public BrowserRuntimeRequest? Last { get; private set; }
        public JsonArray Connections { get; set; } = new();
        public Task<ToolReply> ExecuteAsync(BrowserRuntimeRequest request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new ToolReply(new JsonObject { ["connected"] = true, ["connections"] = Connections.DeepClone() }.ToJsonString()));
        }
        public Task EndApplicationSessionAsync(string sessionId, bool close, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }
}
