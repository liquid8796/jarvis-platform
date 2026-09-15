using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// A stand-in for the relay + extension: connects to the bridge's pipe and
/// answers each {id, cmd, args} line through the handler, echoing the cmd and
/// args back so tests can assert the exact protocol the tools speak.
/// </summary>
internal sealed class FakeBrowserRelay : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly CancellationTokenSource _cts = new();

    public FakeBrowserRelay(string pipeName, Func<string, JsonObject, JsonNode?> handler, string? announceName = null)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _pipe.Connect(5000);
        _ = Task.Run(async () =>
        {
            using var reader = new StreamReader(_pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            if (announceName is not null)
            {
                // What the extension sends right after connecting its relay.
                await writer.WriteLineAsync(
                    new JsonObject { ["event"] = "ready", ["browser"] = announceName }.ToJsonString());
            }

            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null)
                {
                    break;
                }

                var request = (JsonObject)JsonNode.Parse(line)!;
                var cmd = request["cmd"]!.GetValue<string>();
                var args = request["args"] as JsonObject ?? [];
                JsonObject response;
                try
                {
                    response = new JsonObject
                    {
                        ["id"] = request["id"]!.DeepClone(),
                        ["ok"] = true,
                        ["data"] = handler(cmd, args),
                    };
                }
                catch (Exception ex)
                {
                    response = new JsonObject
                    {
                        ["id"] = request["id"]!.DeepClone(),
                        ["ok"] = false,
                        ["error"] = ex.Message,
                    };
                }

                await writer.WriteLineAsync(response.ToJsonString());
            }
        });
    }

    private bool _disposed;

    public async Task SendEventAsync(string eventName) => await _pipe.WriteAsync(Encoding.UTF8.GetBytes(
        new JsonObject { ["event"] = eventName }.ToJsonString() + "\n"));

    public void Dispose()
    {
        if (_disposed)
        {
            return; // tests may drop a relay mid-test and again via using
        }

        _disposed = true;
        _cts.Cancel();
        _pipe.Dispose();
        _cts.Dispose();
    }
}

public class JarvisBrowserToolsTests
{
    private static readonly ToolExecutionContext Context = new() { WorkingDirectory = @"C:\" };

    private static JsonObject Args(params (string Key, JsonNode? Value)[] pairs)
    {
        var args = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            args[key] = value;
        }

        return args;
    }

    /// <summary>Bridge + fake relay on a unique pipe; waits for the connection.</summary>
    private static (BrowserBridge Bridge, FakeBrowserRelay Relay) Connect(Func<string, JsonObject, JsonNode?> handler)
    {
        var pipeName = $"JarvisCode-browser-test-{Guid.NewGuid():N}";
        var bridge = new BrowserBridge(pipeName);
        var relay = new FakeBrowserRelay(pipeName, handler);
        for (var i = 0; i < 100 && !bridge.IsConnected; i++)
        {
            Thread.Sleep(20);
        }

        Assert.True(bridge.IsConnected, "the fake relay should connect to the bridge");
        return (bridge, relay);
    }

    private static ITool Tool(BrowserBridge bridge, string name)
        => JarvisBrowserTools.Create(bridge).Single(t => t.Name == name);

    /// <summary>Wraps frame objects the way the extension's a11y reply does.</summary>
    private static JsonObject Tree(params JsonObject[] frames)
        => new() { ["frames"] = new JsonArray([.. frames]) };

    // ---- protocol mapping over the real pipe ----

    [Fact]
    public async Task ReadPageRequestsTheA11yTreeAndRendersIt()
    {
        JsonObject? seen = null;
        var (bridge, relay) = Connect((cmd, args) =>
        {
            Assert.Equal("a11y", cmd);
            seen = args;
            return new JsonObject
            {
                ["frames"] = new JsonArray(new JsonObject
                {
                    ["frameId"] = 0,
                    ["title"] = "Shop",
                    ["url"] = "https://shop.test/",
                    ["nodes"] = new JsonArray(
                        new JsonObject { ["role"] = "navigation", ["depth"] = 0 },
                        new JsonObject { ["role"] = "link", ["name"] = "Home", ["ref"] = 1, ["depth"] = 1 },
                        new JsonObject { ["role"] = "checkbox", ["name"] = "Agree", ["ref"] = 2, ["depth"] = 1, ["checked"] = true },
                        new JsonObject { ["role"] = "text", ["name"] = "Welcome!", ["depth"] = 1 }),
                }),
            };
        });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "read_page")
                .ExecuteAsync(Args(("filter", "all"), ("ref_id", "ref_7")), Context, default);

            Assert.False(result.IsError);
            Assert.Equal("all", seen!["filter"]?.GetValue<string>());
            Assert.Equal(7, seen["rootRef"]?.GetValue<int>());
            Assert.Contains("Page: Shop", result.Content);
            Assert.Contains("- navigation", result.Content);
            Assert.Contains("  - link \"Home\" [ref_1]", result.Content);
            Assert.Contains("[ref_2] (checked)", result.Content);
            Assert.Contains("text: \"Welcome!\"", result.Content);
        }
    }

    [Fact]
    public async Task ComputerScreenshotReturnsTheImage()
    {
        var pixel = Convert.ToBase64String(new byte[] { 137, 80, 78, 71 });
        var (bridge, relay) = Connect((cmd, args) =>
        {
            Assert.Equal("computer", cmd);
            Assert.Equal("screenshot", args["action"]?.GetValue<string>());
            return new JsonObject { ["image"] = pixel, ["width"] = 1280, ["height"] = 720 };
        });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "computer")
                .ExecuteAsync(Args(("action", "screenshot")), Context, default);

            Assert.False(result.IsError);
            Assert.Contains("1280x720", result.Content);
            Assert.Equal(pixel, Assert.Single(result.Images!).Base64Data);
        }
    }

    [Fact]
    public async Task ComputerClickSendsCoordinateOrRef()
    {
        var calls = new List<JsonObject>();
        var (bridge, relay) = Connect((_, args) =>
        {
            calls.Add(args);
            return new JsonObject { ["clicked"] = true, ["x"] = 10, ["y"] = 20 };
        });
        using (bridge)
        using (relay)
        {
            var tool = Tool(bridge, "computer");
            await tool.ExecuteAsync(
                Args(("action", "left_click"), ("coordinate", new JsonArray(10, 20))), Context, default);
            await tool.ExecuteAsync(
                Args(("action", "left_click"), ("ref", "ref_12"), ("modifiers", "ctrl")), Context, default);

            Assert.Equal(10, calls[0]["x"]?.GetValue<double>());
            Assert.Equal(20, calls[0]["y"]?.GetValue<double>());
            Assert.Equal(12, calls[1]["ref"]?.GetValue<int>());
            Assert.Equal("ctrl", calls[1]["modifiers"]?.GetValue<string>());
        }
    }

    [Fact]
    public async Task ResizePresetsMapToViewportArgs()
    {
        var calls = new List<JsonObject>();
        var (bridge, relay) = Connect((_, args) =>
        {
            calls.Add(args);
            return new JsonObject { ["applied"] = new JsonArray("ok") };
        });
        using (bridge)
        using (relay)
        {
            var tool = Tool(bridge, "resize_window");
            await tool.ExecuteAsync(Args(("preset", "mobile"), ("color_scheme", "dark")), Context, default);
            await tool.ExecuteAsync(Args(("preset", "desktop")), Context, default);
            await tool.ExecuteAsync(Args(("width", 500), ("height", 800)), Context, default);

            Assert.Equal(375, calls[0]["width"]?.GetValue<int>());
            Assert.True(calls[0]["mobile"]?.GetValue<bool>());
            Assert.Equal("dark", calls[0]["colorScheme"]?.GetValue<string>());
            Assert.True(calls[1]["reset"]?.GetValue<bool>());
            Assert.True(calls[2]["mobile"]?.GetValue<bool>(), "width < 768 implies mobile emulation");
        }
    }

    [Fact]
    public async Task ResizeWithoutPresetOrSizeFailsWithoutCallingTheBrowser()
    {
        using var bridge = new BrowserBridge($"JarvisCode-browser-test-{Guid.NewGuid():N}");
        var result = await Tool(bridge, "resize_window").ExecuteAsync(Args(), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("preset", result.Content);
    }

    [Fact]
    public async Task NavigateSupportsHistoryMoves()
    {
        JsonObject? seen = null;
        var (bridge, relay) = Connect((cmd, args) =>
        {
            seen = args;
            return new JsonObject { ["tabId"] = 3, ["went"] = "back" };
        });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "navigate")
                .ExecuteAsync(Args(("url", "back"), ("tab_id", 3)), Context, default);

            Assert.False(result.IsError);
            Assert.Equal("back", seen!["url"]?.GetValue<string>());
            Assert.Null(seen["newTab"]);
        }
    }

    [Fact]
    public async Task FormInputSendsTheParsedRefAndRawValue()
    {
        JsonObject? seen = null;
        var (bridge, relay) = Connect((cmd, args) =>
        {
            Assert.Equal("form_input", cmd);
            seen = args;
            return new JsonObject { ["set"] = true, ["checked"] = true };
        });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "form_input")
                .ExecuteAsync(Args(("ref", "ref_4"), ("value", true)), Context, default);

            Assert.False(result.IsError);
            Assert.Equal(4, seen!["ref"]?.GetValue<int>());
            Assert.True(seen["value"]?.GetValue<bool>());
            Assert.Contains("checked", result.Content);
        }
    }

    [Fact]
    public async Task NetworkBodyFetchGoesThroughRequestId()
    {
        var (bridge, relay) = Connect((cmd, args) =>
        {
            Assert.Equal("network_read", cmd);
            Assert.Equal("99.1", args["requestId"]?.GetValue<string>());
            return new JsonObject
            {
                ["request"] = new JsonObject { ["method"] = "GET", ["url"] = "https://api.test/x", ["status"] = 200 },
                ["body"] = "{\"ok\":true}",
                ["base64Encoded"] = false,
            };
        });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "read_network_requests")
                .ExecuteAsync(Args(("request_id", "99.1")), Context, default);

            Assert.False(result.IsError);
            Assert.Contains("GET https://api.test/x → 200", result.Content);
            Assert.Contains("{\"ok\":true}", result.Content);
        }
    }

    [Fact]
    public async Task JavaScriptReturnsStringValuesRawAndObjectsAsJson()
    {
        var (bridge, relay) = Connect((cmd, args) => cmd == "js_exec" && args["code"]!.GetValue<string>() == "1+1"
            ? new JsonObject { ["value"] = 2, ["type"] = "number" }
            : new JsonObject { ["value"] = "hello", ["type"] = "string" });
        using (bridge)
        using (relay)
        {
            var tool = Tool(bridge, "javascript_tool");
            var number = await tool.ExecuteAsync(Args(("code", "1+1")), Context, default);
            var text = await tool.ExecuteAsync(Args(("code", "'x'")), Context, default);

            Assert.Equal("2", number.Content);
            Assert.Equal("hello", text.Content);
        }
    }

    // ---- file upload ----

    private static string TempFile(string name, int bytes = 16)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jarvis-upload-{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task FileUploadSendsAbsolutePathsForARefTarget()
    {
        var file = TempFile("shot.png");
        JsonObject? seen = null;
        var (bridge, relay) = Connect((cmd, args) =>
        {
            Assert.Equal("file_upload", cmd);
            seen = args;
            return new JsonObject { ["uploaded"] = 1 };
        });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "file_upload")
                .ExecuteAsync(Args(("paths", new JsonArray(file)), ("ref", "ref_f2r9")), Context, default);

            Assert.False(result.IsError);
            Assert.Equal(9, seen!["ref"]?.GetValue<int>());
            Assert.Equal(2, seen["frameId"]?.GetValue<int>());
            Assert.Equal(file, (seen["paths"] as JsonArray)![0]?.GetValue<string>());
        }

        File.Delete(file);
    }

    [Fact]
    public async Task FileUploadWithACoordinateDropsTheFileContents()
    {
        var file = TempFile("note.txt");
        await File.WriteAllTextAsync(file, "hello");
        JsonObject? seen = null;
        var (bridge, relay) = Connect((cmd, args) =>
        {
            Assert.Equal("drop_image", cmd);
            seen = args;
            return new JsonObject { ["dropped"] = true, ["target"] = "div" };
        });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "file_upload")
                .ExecuteAsync(Args(("paths", new JsonArray(file)), ("coordinate", new JsonArray(30, 40))), Context, default);

            Assert.False(result.IsError);
            Assert.Equal(30, seen!["x"]?.GetValue<double>());
            Assert.Equal("text/plain", seen["mime"]?.GetValue<string>());
            Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(seen["data"]!.GetValue<string>())));
        }

        File.Delete(file);
    }

    [Fact]
    public async Task FileUploadRefusesMissingFilesOversizedBatchesAndNoTarget()
    {
        using var bridge = new BrowserBridge($"JarvisCode-browser-test-{Guid.NewGuid():N}");
        var tool = Tool(bridge, "file_upload");
        var file = TempFile("ok.png");

        var missing = await tool.ExecuteAsync(
            Args(("paths", new JsonArray(@"C:\nope\missing.png")), ("ref", "ref_1")), Context, default);
        var noTarget = await tool.ExecuteAsync(Args(("paths", new JsonArray(file))), Context, default);
        var empty = await tool.ExecuteAsync(Args(("ref", "ref_1")), Context, default);

        Assert.True(missing.IsError);
        Assert.Contains("No such file", missing.Content);
        Assert.True(noTarget.IsError);
        Assert.Contains("coordinate", noTarget.Content);
        Assert.True(empty.IsError);

        var big = TempFile("big.bin", (int)BrowserFileUploadTool.MaxTotalBytes + 1);
        var oversized = await tool.ExecuteAsync(
            Args(("paths", new JsonArray(big)), ("ref", "ref_1")), Context, default);
        Assert.True(oversized.IsError);
        Assert.Contains("10MB", oversized.Content);

        File.Delete(file);
        File.Delete(big);
    }

    /// <summary>
    /// A page the model is driving is a place the user's files leave the
    /// machine, so an upload may only read what the session may read — the
    /// reference runs the same check before handing paths to Chrome.
    /// </summary>
    [Fact]
    public async Task FileUploadRefusesAPathTheSessionMayNotRead()
    {
        using var bridge = new BrowserBridge($"JarvisCode-browser-test-{Guid.NewGuid():N}");
        var tool = Tool(bridge, "file_upload");

        var root = Directory.CreateTempSubdirectory("jarvis-upload-");
        var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace"));
        var elsewhere = Directory.CreateDirectory(Path.Combine(root.FullName, "elsewhere"));
        var outside = Path.Combine(elsewhere.FullName, "secret.png");
        var inside = Path.Combine(workspace.FullName, "ok.png");
        await File.WriteAllBytesAsync(outside, new byte[8]);
        await File.WriteAllBytesAsync(inside, new byte[8]);

        var scoped = new ToolExecutionContext { WorkingDirectory = workspace.FullName };
        try
        {
            var refused = await tool.ExecuteAsync(
                Args(("paths", new JsonArray(outside)), ("ref", "ref_1")), scoped, default);
            Assert.True(refused.IsError);
            Assert.Equal(BrowserFileUploadTool.OutOfScope(outside), refused.Content);

            // The same call for a file inside the workspace gets past the guard
            // and fails later, on the bridge that is not connected.
            var allowed = await tool.ExecuteAsync(
                Args(("paths", new JsonArray(inside)), ("ref", "ref_1")), scoped, default);
            Assert.DoesNotContain("Cannot upload", allowed.Content, StringComparison.Ordinal);

            // An added directory is part of what the session may read.
            var widened = scoped with { AdditionalDirectories = [elsewhere.FullName] };
            var nowAllowed = await tool.ExecuteAsync(
                Args(("paths", new JsonArray(outside)), ("ref", "ref_1")), widened, default);
            Assert.DoesNotContain("Cannot upload", nowAllowed.Content, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ACoordinateDropTakesExactlyOneFile()
    {
        using var bridge = new BrowserBridge($"JarvisCode-browser-test-{Guid.NewGuid():N}");
        var first = TempFile("a.png");
        var second = TempFile("b.png");

        var result = await Tool(bridge, "file_upload").ExecuteAsync(
            Args(("paths", new JsonArray(first, second)), ("coordinate", new JsonArray(1, 2))), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("exactly one file", result.Content);
        File.Delete(first);
        File.Delete(second);
    }

    // ---- gif recording ----

    [Fact]
    public async Task GifLifecycleMapsToTheExtensionOps()
    {
        var ops = new List<string>();
        var (bridge, relay) = Connect((cmd, args) =>
        {
            Assert.Equal("gif", cmd);
            ops.Add(args["op"]!.GetValue<string>());
            return new JsonObject { ["recording"] = true, ["frames"] = 12, ["cleared"] = true };
        });
        using (bridge)
        using (relay)
        {
            var tool = Tool(bridge, "gif_creator");
            foreach (var action in new[] { "start_recording", "stop_recording", "clear" })
            {
                Assert.False((await tool.ExecuteAsync(Args(("action", action)), Context, default)).IsError);
            }

            Assert.Equal(["start", "stop", "clear"], ops);
        }
    }

    [Fact]
    public async Task GifExportComposesTheCapturedFramesIntoAFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jarvis-gif-{Guid.NewGuid():N}");
        var context = new ToolExecutionContext { WorkingDirectory = directory };
        Directory.CreateDirectory(directory);

        var (bridge, relay) = Connect((cmd, args) => new JsonObject
        {
            ["frames"] = new JsonArray(
                new JsonObject { ["ts"] = 10.0, ["data"] = SampleJpeg(Color.Navy) },
                new JsonObject { ["ts"] = 10.5, ["data"] = SampleJpeg(Color.Maroon) }),
            ["events"] = new JsonArray(new JsonObject
            {
                ["ts"] = 10.1, ["action"] = "left_click", ["x"] = 20, ["y"] = 15, ["label"] = "left click",
            }),
            ["dropped"] = 0,
        });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "gif_creator")
                .ExecuteAsync(Args(("action", "export"), ("path", "run.gif")), context, default);

            Assert.False(result.IsError);
            var written = Path.Combine(directory, "run.gif");
            Assert.True(File.Exists(written));
            var bytes = await File.ReadAllBytesAsync(written);
            Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
            Assert.Contains("2 frame(s)", result.Content);
        }

        Directory.Delete(directory, recursive: true);
    }

    /// <summary>
    /// A long recording ships every captured frame over the pipe in one reply —
    /// megabytes of base64. The transport has to carry it, and the composer has
    /// to thin it down rather than emit a 200-frame GIF.
    /// </summary>
    [Fact]
    public async Task ALongRecordingSurvivesThePipeAndIsThinnedDown()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jarvis-gif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var context = new ToolExecutionContext { WorkingDirectory = directory };

        var frame = SampleJpeg(Color.DarkSlateBlue, 640, 400);
        var payload = new JsonArray();
        for (var i = 0; i < 200; i++)
        {
            payload.Add(new JsonObject { ["ts"] = 100 + i * 0.2, ["data"] = frame });
        }

        var (bridge, relay) = Connect((cmd, args) => new JsonObject
        {
            ["frames"] = payload.DeepClone(),
            ["events"] = new JsonArray(),
            ["dropped"] = 12,
        });
        using (bridge)
        using (relay)
        {
            Assert.True(frame.Length * 200 > 1_000_000, "the fixture must be big enough to matter");

            var result = await Tool(bridge, "gif_creator")
                .ExecuteAsync(Args(("action", "export"), ("path", "long.gif")), context, default);

            Assert.False(result.IsError, result.Content);
            Assert.Contains("200 frame(s)", result.Content);
            Assert.Contains("12 early frame(s)", result.Content);

            using var stream = new MemoryStream(await File.ReadAllBytesAsync(Path.Combine(directory, "long.gif")));
            using var image = Image.FromStream(stream);
            Assert.Equal(GifComposer.MaxFrames, image.GetFrameCount(FrameDimension.Time));
        }

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task GifExportWithoutFramesExplainsWhatToDo()
    {
        var (bridge, relay) = Connect((cmd, args) => new JsonObject { ["frames"] = new JsonArray() });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "gif_creator")
                .ExecuteAsync(Args(("action", "export")), Context, default);

            Assert.True(result.IsError);
            Assert.Contains("start_recording", result.Content);
        }
    }

    [Fact]
    public async Task GifRejectsAnUnknownAction()
    {
        using var bridge = new BrowserBridge($"JarvisCode-browser-test-{Guid.NewGuid():N}");
        var result = await Tool(bridge, "gif_creator")
            .ExecuteAsync(Args(("action", "rewind")), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("start_recording", result.Content);
    }

    [Fact]
    public void GifOptionsDefaultOnAndClampQuality()
    {
        var defaults = BrowserGifTool.ReadOptions(new JsonObject());
        Assert.True(defaults.ShowClickIndicators && defaults.ShowWatermark && defaults.ShowProgressBar);
        Assert.Equal(7, defaults.Quality);

        var custom = BrowserGifTool.ReadOptions(new JsonObject
        {
            ["options"] = new JsonObject
            {
                ["show_watermark"] = false,
                ["quality"] = 99,
            },
        });
        Assert.False(custom.ShowWatermark);
        Assert.True(custom.ShowClickIndicators);
        Assert.Equal(10, custom.Quality);
    }

    private static string SampleJpeg(Color color, int width = 80, int height = 60)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(color);
            // Noise keeps the JPEG from compressing to almost nothing.
            for (var x = 0; x < width; x += 3)
            {
                graphics.DrawLine(Pens.White, x, 0, x + 40, height);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Jpeg);
        return Convert.ToBase64String(stream.ToArray());
    }

    // ---- browser_batch ----

    [Fact]
    public async Task BatchRunsSequentiallyAndAggregatesOutput()
    {
        var commands = new List<string>();
        var (bridge, relay) = Connect((cmd, args) =>
        {
            commands.Add(cmd);
            return cmd switch
            {
                "navigate" => new JsonObject { ["tabId"] = 1 },
                "computer" => new JsonObject { ["typed"] = true },
                _ => new JsonObject(),
            };
        });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "browser_batch").ExecuteAsync(Args(("actions", new JsonArray(
                new JsonObject
                {
                    ["name"] = "navigate",
                    ["input"] = new JsonObject { ["url"] = "https://a.test/", ["tabId"] = 1 },
                },
                new JsonObject { ["name"] = "computer", ["input"] = new JsonObject { ["action"] = "type", ["text"] = "hi" } }))),
                Context, default);

            Assert.False(result.IsError);
            Assert.Equal(["navigate", "computer"], commands);
            Assert.Contains("→ navigate:", result.Content);
            Assert.Contains("→ computer:", result.Content);
        }
    }

    [Fact]
    public async Task BatchStopsOnTheFirstErrorAndKeepsEarlierOutput()
    {
        var commands = new List<string>();
        var (bridge, relay) = Connect((cmd, args) =>
        {
            commands.Add(cmd);
            if (cmd == "a11y")
            {
                throw new InvalidOperationException("boom");
            }

            return new JsonObject { ["tabId"] = 1 };
        });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "browser_batch").ExecuteAsync(Args(("actions", new JsonArray(
                new JsonObject
                {
                    ["name"] = "navigate",
                    ["input"] = new JsonObject { ["url"] = "https://a.test/", ["tabId"] = 1 },
                },
                new JsonObject { ["name"] = "read_page", ["input"] = new JsonObject { ["tabId"] = 1 } },
                new JsonObject
                {
                    ["name"] = "navigate",
                    ["input"] = new JsonObject { ["url"] = "https://b.test/", ["tabId"] = 1 },
                }))),
                Context, default);

            Assert.True(result.IsError);
            Assert.Equal(["navigate", "a11y"], commands);
            Assert.Contains("→ navigate:", result.Content);
            Assert.Contains("stopped at step 2", result.Content);
        }
    }

    [Fact]
    public async Task BatchRejectsNestingAndUnknownTools()
    {
        using var bridge = new BrowserBridge($"JarvisCode-browser-test-{Guid.NewGuid():N}");
        var tool = Tool(bridge, "browser_batch");

        var nested = await tool.ExecuteAsync(Args(("actions", new JsonArray(
            new JsonObject { ["name"] = "browser_batch" }))), Context, default);
        var unknown = await tool.ExecuteAsync(Args(("actions", new JsonArray(
            new JsonObject { ["name"] = "PowerShell" }))), Context, default);

        Assert.True(nested.IsError);
        Assert.Contains("nested", nested.Content);
        Assert.True(unknown.IsError);
        Assert.Contains("not a Jarvis Browser tool", unknown.Content);
    }

    // ---- formatting ----

    [Fact]
    public void FindMatchesFiltersToRefCarryingNodesCaseInsensitively()
    {
        var data = Tree(new JsonObject
        {
            ["frameId"] = 0,
            ["nodes"] = new JsonArray(
                new JsonObject { ["role"] = "text", ["name"] = "Sign in to continue", ["depth"] = 0 },
                new JsonObject { ["role"] = "button", ["name"] = "Sign In", ["ref"] = 5, ["depth"] = 1 },
                new JsonObject { ["role"] = "link", ["name"] = "Register", ["ref"] = 6, ["depth"] = 1 }),
        });

        var matches = JarvisBrowserFormat.FindMatches(data, "sign in");

        Assert.Contains("1 match(es)", matches);
        Assert.Contains("button \"Sign In\" [ref_5]", matches);
        Assert.DoesNotContain("Register", matches);
        Assert.Contains("No elements match", JarvisBrowserFormat.FindMatches(data, "checkout"));
    }

    [Fact]
    public void ConsoleFormatFiltersLevelsAndPatternsAndKeepsTheTail()
    {
        var entries = new JsonArray();
        for (var i = 0; i < 5; i++)
        {
            entries.Add(new JsonObject { ["level"] = "log", ["text"] = $"tick {i}" });
        }

        entries.Add(new JsonObject { ["level"] = "error", ["text"] = "TypeError: x is not a function (app.js:3)", ["url"] = "https://a.test/app.js" });
        var data = new JsonObject { ["entries"] = entries };

        var errors = JarvisBrowserFormat.FormatConsole(data, onlyErrors: true, pattern: null, limit: 100);
        Assert.Equal("[error] TypeError: x is not a function (app.js:3) (https://a.test/app.js)", errors);

        var tail = JarvisBrowserFormat.FormatConsole(data, onlyErrors: false, pattern: null, limit: 2);
        Assert.Contains("[4 older message(s) not shown]", tail);
        Assert.Contains("tick 4", tail);

        var pattern = JarvisBrowserFormat.FormatConsole(data, onlyErrors: false, pattern: "tick [12]", limit: 100);
        Assert.Equal("[log] tick 1\n[log] tick 2", pattern);

        // An invalid regex degrades to a substring match instead of throwing.
        Assert.Contains("TypeError", JarvisBrowserFormat.FormatConsole(data, false, "function (app", 100));
    }

    [Fact]
    public void ConsoleAndNetworkExplainAFreshAttachment()
    {
        var justAttached = new JsonObject { ["justAttached"] = true };

        Assert.Contains("just started", JarvisBrowserFormat.FormatConsole(justAttached, false, null, 100));
        Assert.Contains("just started", JarvisBrowserFormat.FormatNetwork(justAttached, null, 50));
    }

    [Fact]
    public void NetworkFormatShowsStatusFailureAndSize()
    {
        var data = new JsonObject
        {
            ["requests"] = new JsonArray(
                new JsonObject
                {
                    ["requestId"] = "1.1", ["url"] = "https://a.test/api", ["method"] = "POST",
                    ["status"] = 500, ["finished"] = true, ["type"] = "fetch", ["size"] = 2048,
                },
                new JsonObject
                {
                    ["requestId"] = "1.2", ["url"] = "https://a.test/x.js", ["method"] = "GET",
                    ["failed"] = true, ["finished"] = true, ["errorText"] = "net::ERR_CONNECTION_REFUSED",
                },
                new JsonObject { ["requestId"] = "1.3", ["url"] = "https://a.test/slow", ["method"] = "GET" }),
        };

        var text = JarvisBrowserFormat.FormatNetwork(data, null, 50);

        Assert.Contains("[500] POST https://a.test/api (fetch, 2kB) id=1.1", text);
        Assert.Contains("[failed: net::ERR_CONNECTION_REFUSED] GET https://a.test/x.js id=1.2", text);
        Assert.Contains("[pending] GET https://a.test/slow id=1.3", text);
        Assert.Contains("request_id", text);

        var filtered = JarvisBrowserFormat.FormatNetwork(data, "api", 50);
        Assert.DoesNotContain("x.js", filtered);
    }

    [Theory]
    [InlineData("ref_12", 12, 0)]
    [InlineData("12", 12, 0)]
    [InlineData("REF_3", 3, 0)]
    [InlineData("ref_f4r7", 7, 4)]
    [InlineData("f4r7", 7, 4)]
    public void RefsParseFromStrings(string raw, int expected, int frameId)
        => Assert.Equal(new ElementRef(expected, frameId), JarvisBrowserFormat.ParseRef(JsonValue.Create(raw)));

    [Fact]
    public void RefsParseFromNumbersAndRejectGarbage()
    {
        Assert.Equal(new ElementRef(12), JarvisBrowserFormat.ParseRef(JsonValue.Create(12)));
        Assert.Null(JarvisBrowserFormat.ParseRef(JsonValue.Create("banner")));
        Assert.Null(JarvisBrowserFormat.ParseRef(JsonValue.Create("ref_f2")));
        Assert.Null(JarvisBrowserFormat.ParseRef(null));
    }

    [Fact]
    public void RefsRoundTripThroughTheirRenderedForm()
    {
        foreach (var reference in new[] { new ElementRef(5), new ElementRef(5, 3) })
        {
            Assert.Equal(reference, JarvisBrowserFormat.ParseRef(JsonValue.Create(reference.ToString())));
        }

        Assert.Equal("ref_5", new ElementRef(5).ToString());
        Assert.Equal("ref_f3r5", new ElementRef(5, 3).ToString());
    }

    /// <summary>
    /// Refs are numbered per document, so a page and its iframe both hand out
    /// ref_1 — only the frame id keeps the two apart on the way back.
    /// </summary>
    [Fact]
    public void AFrameRefCarriesItsFrameIdBackToTheExtension()
    {
        var args = new JsonObject();
        new ElementRef(1, 7).ApplyTo(args);
        Assert.Equal(1, args["ref"]?.GetValue<int>());
        Assert.Equal(7, args["frameId"]?.GetValue<int>());

        var mainFrame = new JsonObject();
        new ElementRef(1).ApplyTo(mainFrame);
        Assert.Equal(1, mainFrame["ref"]?.GetValue<int>());
        Assert.Null(mainFrame["frameId"]);
    }

    [Fact]
    public void TreeRenderingCapsAtMaxChars()
    {
        var nodes = new JsonArray();
        for (var i = 0; i < 200; i++)
        {
            nodes.Add(new JsonObject { ["role"] = "link", ["name"] = $"Item {i}", ["ref"] = i + 1, ["depth"] = 0 });
        }

        var rendered = JarvisBrowserFormat.RenderTree(
            Tree(new JsonObject { ["frameId"] = 0, ["title"] = "T", ["url"] = "https://t/", ["nodes"] = nodes }),
            maxChars: 400);

        Assert.Contains("[tree truncated", rendered);
        Assert.True(rendered.Length < 700, "output should stay near the cap");
    }

    /// <summary>A page extensions cannot read (chrome://, the web store) yields no frames at all.</summary>
    [Fact]
    public void AnUnreadablePageSaysSoInsteadOfRenderingAnEmptyTree()
    {
        var rendered = JarvisBrowserFormat.RenderTree(new JsonObject { ["frames"] = new JsonArray() }, 50000);

        Assert.Contains("no readable elements", rendered);
        Assert.Contains("get_page_text", rendered);
    }

    /// <summary>
    /// The extension is updated by hand in the user's browser, so an app running
    /// ahead of it still has to render the older single-frame reply.
    /// </summary>
    [Fact]
    public void ThePreIframeReplyShapeStillRenders()
    {
        var rendered = JarvisBrowserFormat.RenderTree(new JsonObject
        {
            ["title"] = "Old",
            ["url"] = "https://old.test/",
            ["nodes"] = new JsonArray(
                new JsonObject { ["role"] = "button", ["name"] = "Go", ["ref"] = 2, ["depth"] = 0 }),
        }, 50000);

        Assert.Contains("Page: Old", rendered);
        Assert.Contains("button \"Go\" [ref_2]", rendered);
    }

    // ---- iframes ----

    [Fact]
    public void IframeNodesRenderUnderTheirOwnHeaderWithFrameScopedRefs()
    {
        var rendered = JarvisBrowserFormat.RenderTree(Tree(
            new JsonObject
            {
                ["frameId"] = 0,
                ["title"] = "Checkout",
                ["url"] = "https://shop.test/pay",
                ["nodes"] = new JsonArray(
                    new JsonObject { ["role"] = "button", ["name"] = "Continue", ["ref"] = 1, ["depth"] = 0 }),
            },
            new JsonObject
            {
                ["frameId"] = 4,
                ["title"] = "Card",
                ["url"] = "https://pay.test/card",
                ["nodes"] = new JsonArray(
                    // The iframe restarts numbering: this is also ref 1.
                    new JsonObject { ["role"] = "textbox", ["name"] = "Card number", ["ref"] = 1, ["depth"] = 0 }),
            }), maxChars: 50000);

        Assert.Contains("Page: Checkout", rendered);
        Assert.Contains("button \"Continue\" [ref_1]", rendered);
        Assert.Contains("## iframe [frameId 4] https://pay.test/card", rendered);
        Assert.Contains("textbox \"Card number\" [ref_f4r1]", rendered);
    }

    [Fact]
    public void FindReachesIntoIframesAndKeepsRefsDistinct()
    {
        var matches = JarvisBrowserFormat.FindMatches(Tree(
            new JsonObject
            {
                ["frameId"] = 0,
                ["nodes"] = new JsonArray(
                    new JsonObject { ["role"] = "textbox", ["name"] = "Card holder", ["ref"] = 1, ["depth"] = 0 }),
            },
            new JsonObject
            {
                ["frameId"] = 4,
                ["nodes"] = new JsonArray(
                    new JsonObject { ["role"] = "textbox", ["name"] = "Card number", ["ref"] = 1, ["depth"] = 0 }),
            }), "card");

        Assert.Contains("2 match(es)", matches);
        Assert.Contains("[ref_1]", matches);
        Assert.Contains("[ref_f4r1]", matches);
    }

    [Fact]
    public async Task ReadPageFocusedOnAnIframeRefTargetsThatFrame()
    {
        JsonObject? seen = null;
        var (bridge, relay) = Connect((cmd, args) =>
        {
            seen = args;
            return new JsonObject
            {
                ["frames"] = new JsonArray(new JsonObject
                {
                    ["frameId"] = 4,
                    ["url"] = "https://pay.test/card",
                    ["nodes"] = new JsonArray(
                        new JsonObject { ["role"] = "button", ["name"] = "Pay", ["ref"] = 3, ["depth"] = 0 }),
                }),
            };
        });
        using (bridge)
        using (relay)
        {
            var result = await Tool(bridge, "read_page")
                .ExecuteAsync(Args(("ref_id", "ref_f4r2")), Context, default);

            Assert.Equal(2, seen!["rootRef"]?.GetValue<int>());
            Assert.Equal(4, seen["frameId"]?.GetValue<int>());
            Assert.Contains("[ref_f4r3]", result.Content);
        }
    }

    [Fact]
    public async Task ComputerAndFormInputSendTheFrameOfAnIframeRef()
    {
        var calls = new List<JsonObject>();
        var (bridge, relay) = Connect((_, args) =>
        {
            calls.Add(args);
            return new JsonObject { ["clicked"] = true, ["x"] = 1, ["y"] = 2, ["set"] = true };
        });
        using (bridge)
        using (relay)
        {
            await Tool(bridge, "computer")
                .ExecuteAsync(Args(("action", "left_click"), ("ref", "ref_f4r9")), Context, default);
            await Tool(bridge, "form_input")
                .ExecuteAsync(Args(("ref", "ref_f4r9"), ("value", "4242")), Context, default);

            Assert.All(calls, call =>
            {
                Assert.Equal(9, call["ref"]?.GetValue<int>());
                Assert.Equal(4, call["frameId"]?.GetValue<int>());
            });
        }
    }

    [Fact]
    public void EveryBrowserToolCarriesAServializableSchemaAndADescription()
    {
        using var bridge = new BrowserBridge($"JarvisCode-browser-test-{Guid.NewGuid():N}");
        foreach (var tool in JarvisBrowserTools.Create(bridge))
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name} needs a description");
            var json = tool.InputSchema.ToJsonString();
            Assert.Equal("object", (JsonNode.Parse(json) as JsonObject)?["type"]?.GetValue<string>());
        }
    }

    [Fact]
    public void ComputerDescribeCallNamesTheActionAndTarget()
    {
        using var bridge = new BrowserBridge($"JarvisCode-browser-test-{Guid.NewGuid():N}");
        var tool = Tool(bridge, "computer");

        Assert.Equal("BrowserComputer(screenshot)", tool.DescribeCall(Args(("action", "screenshot"))));
        Assert.Equal("BrowserComputer(left_click ref_3)", tool.DescribeCall(Args(("action", "left_click"), ("ref", "ref_3"))));
        Assert.Equal("BrowserComputer(key ctrl+a)", tool.DescribeCall(Args(("action", "key"), ("text", "ctrl+a"))));
    }
}
