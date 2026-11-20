using System.IO;
using System.IO.Pipes;
using System.Text;
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
    [Fact]
    public async Task DesktopSettingsExplainAutomaticReconnectWhenNoExtensionIsReady()
    {
        var vm = new ImageGenViewModel(_root, () => new Runtime());
        await vm.RefreshAsync();
        Assert.Empty(vm.Browsers);
        Assert.Contains("1.4.1+", vm.Status, StringComparison.Ordinal);
        Assert.Contains("automatic reconnect", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NativeBrowserPollDeadlineCanBeObservedAgainOnExactOriginalBinding()
    {
        // Real Windows service and extension pipes, unique to this test. The fake extension
        // responds only to the ordinary, already accepted original job (no real browser or account).
        var suffix = Guid.NewGuid().ToString("N");
        var servicePipe = "jarvis-imagegen-poll-service-" + suffix;
        var extensionPipe = "jarvis-imagegen-poll-extension-" + suffix;
        var bindingStore = new ImageGenBrowserBindingStore(_root);
        var binding = bindingStore.Save(Guid.NewGuid().ToString("D"), "chrome", null);
        var context = new AgentExecutionContext(_root, "test-call", AgentSessionRules.NewSessionId()) { OwnerId = "owner", AgentDeviceId = "device" };
        var job = new ImageGenerationJob { JobId = JobId, OwnerId = "owner", DeviceId = "device", SessionId = context.SessionId,
            RequestId = "original", RequestDigest = "original-digest", Prompt = "An already-submitted test image", Binding = binding };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var service = new BrowserServiceServer(servicePipe, extensionPipe, _root);
        var serviceTask = service.RunAsync(stop.Token);
        using var client = new BrowserRuntimeClient(servicePipe);
        await using var pipe = new NamedPipeClientStream(".", extensionPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000, stop.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            async Task Write(JsonObject value) => await writer.WriteLineAsync(value.ToJsonString().AsMemory(), stop.Token);
            async Task<JsonObject> Read() => JsonNode.Parse((await reader.ReadLineAsync(stop.Token))!)!.AsObject();
            await Write(new JsonObject
            {
                ["event"] = "ready", ["browser"] = "Chrome", ["browserFamily"] = "extension",
                ["browserProtocolVersion"] = 2, ["nativeHostProtocolVersion"] = 2, ["applicationSessions"] = true,
                ["extensionInstanceId"] = binding.ExtensionInstanceId,
                ["capabilities"] = new JsonArray("imagegen-v1", "application-sessions-v1")
            });
            var handshake = await Read();
            Assert.Equal("jarvis_native_ready", handshake["event"]!.GetValue<string>());
            Assert.True(handshake["accepted"]!.GetValue<bool>());

            var backend = new ChatGptExtensionImageBackend(client, bindingStore);
            void VerifyProbe(JsonObject request)
            {
                Assert.Equal("imagegen_poll", request["cmd"]!.GetValue<string>());
                Assert.Equal(context.SessionId, request["sessionId"]!.GetValue<string>());
                Assert.Equal(JobId, request["args"]!["jobId"]!.GetValue<string>());
                Assert.Null(request["args"]!["prompt"]);
            }
            var first = backend.CallAsync("poll", job, context, stop.Token);
            var slow = await Read(); VerifyProbe(slow);
            // Delay its real acknowledgement beyond the bridge's own request deadline.
            var timedOut = await first;
            Assert.Equal("BROWSER_POLL_TIMEOUT", timedOut["errorCode"]?.GetValue<string>());
            var second = backend.CallAsync("poll", job, context, stop.Token);
            var fresh = await Read(); VerifyProbe(fresh);
            Assert.NotEqual(slow["id"]!.GetValue<string>(), fresh["id"]!.GetValue<string>());
            await Write(new JsonObject { ["id"] = slow["id"]!.DeepClone(), ["ok"] = true,
                ["data"] = new JsonObject { ["ready"] = false } });
            await Write(new JsonObject { ["id"] = fresh["id"]!.DeepClone(), ["ok"] = true,
                ["data"] = new JsonObject { ["ready"] = true, ["count"] = 1, ["conversationId"] = "original-conversation" } });
            var current = await second;
            Assert.True(current["ready"]!.GetValue<bool>());
            Assert.Equal(1, current["count"]!.GetValue<int>());
            Assert.Equal("original-conversation", current["conversationId"]!.GetValue<string>());
            Assert.Equal(binding, bindingStore.Read());
        }
        finally
        {
            await pipe.DisposeAsync();
            stop.Cancel();
            try { await serviceTask; } catch (OperationCanceledException) { }
        }
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
