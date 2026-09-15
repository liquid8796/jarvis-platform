using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Cli;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Sessions;

namespace JarvisCode.Cli.Tests;

[Collection("repl")]
public sealed class ChromeIntegrationTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "jarvis-cli-browser-" + Guid.NewGuid().ToString("N"));
    private readonly string _profile = "test-cli-browser-" + Guid.NewGuid().ToString("N");
    private readonly string? _oldProfile = Environment.GetEnvironmentVariable("JARVISCODE_PROFILE");
    private readonly string _oldDirectory = Environment.CurrentDirectory;

    public ChromeIntegrationTests()
    {
        Directory.CreateDirectory(_workspace);
        Environment.CurrentDirectory = _workspace;
        Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", _profile);
    }

    private CliOptions Options(bool enabled) => new()
    {
        Prompt = "Read the controlled browser tab", ChromeEnabled = enabled, Print = true,
        NoSessionPersistence = true, SettingSources = "", MaxTurns = 3,
        Settings = """{"DefaultModelId":"fixture-model","CustomModels":[{"ProviderId":"fixture","ModelId":"fixture-model","DisplayName":"Fixture","MaxContextTokens":200000}]}""",
    };

    [Fact]
    public async Task Cli_turn_can_execute_a_readonly_browser_tool_over_the_existing_profile_host()
    {
        var pipe = "jarvis-cli-browser-native-" + Guid.NewGuid().ToString("N");
        using var desktop = new BrowserBridge(pipe);
        using var relay = new Relay(pipe);
        await Ready(desktop);
        using var host = new BrowserBridgeHost(ProfilePaths.Create(_profile).Root, desktop);
        var provider = new BrowserProvider();
        var options = Options(true) with { HasToolsFilter = true, Tools = [BrowserProvider.ToolName] };
        using var services = CliServices.Create(options, _ => new ProviderRegistry([provider]));
        await services.InitializeAsync(options, _workspace, default);
        var gate = new UiPermissionGate { WorkingDirectory = _workspace,
            PromptAsync = (_, _) => Task.FromResult(PermissionDecision.Allow) };
        var runner = new CliTurnRunner(services, options, gate);
        var session = Session.CreateNew(_workspace);
        var result = await runner.RunAsync(session, options.Prompt, new CliTurnEvents(), default);
        Assert.Equal(TurnEndReason.Completed, result.Reason);
        Assert.Equal(BrowserProvider.ToolName, Assert.Single(provider.Requests[0].Tools).Name);
        Assert.Equal(1, relay.TabCalls);
        Assert.Contains(provider.Requests[1].Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(),
            block => !block.IsError && block.Content.Contains("Controlled tab", StringComparison.Ordinal));
        Assert.Contains(services.ChromeTools(), tool => tool.Name == BrowserProvider.ToolName);
        Assert.Single(desktop.Connections);
    }

    [Fact]
    public async Task No_chrome_and_safe_mode_never_connect_or_advertise_browser_tools()
    {
        foreach (var options in new[] { Options(false), Options(true) with { SafeMode = true } })
        {
            using var services = CliServices.Create(options, _ => new ProviderRegistry([new BrowserProvider()]));
            await services.InitializeAsync(options, _workspace, default);
            Assert.False(services.ChromeEnabled);
            Assert.Empty(services.ChromeTools());
            Assert.False(services.App.Browser.IsConnected);
        }
    }

    private static async Task Ready(BrowserBridge bridge)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!bridge.ExtensionReady) await Task.Delay(20, timeout.Token);
    }

    private sealed class BrowserProvider : ILlmProvider
    {
        public const string ToolName = "mcp__claude-in-chrome__tabs_context_mcp";
        public string Id => "fixture";
        public string DisplayName => "Fixture";
        public List<LlmRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            Requests.Add(request);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            if (Requests.Count == 1)
            {
                yield return new ToolCallStartedEvent(0, "browser-fixture-call", ToolName);
                yield return new ToolCallArgumentsDeltaEvent(0, "{}");
                yield return new ResponseCompletedEvent(true, new Usage(3, 2), "tool_use");
            }
            else
            {
                yield return new TextDeltaEvent("Controlled browser result received.");
                yield return new ResponseCompletedEvent(false, new Usage(3, 2), "end_turn");
            }
        }
    }

    private sealed class Relay : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _run;
        private int _tabs;
        public int TabCalls => Volatile.Read(ref _tabs);
        public Relay(string pipe)
        {
            _pipe = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipe.Connect(5000);
            _run = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
                    using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync("{\"event\":\"ready\",\"browser\":\"Controlled Chrome\"}");
                    while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
                    {
                        var request = JsonNode.Parse(line)!;
                        if (request["cmd"]?.GetValue<string>() == "tabs") Interlocked.Increment(ref _tabs);
                        await writer.WriteLineAsync(new JsonObject
                        {
                            ["id"] = request["id"]?.DeepClone(), ["ok"] = true,
                            ["data"] = new JsonObject { ["result"] = new JsonArray(new JsonObject
                            { ["id"] = 7, ["title"] = "Controlled tab", ["url"] = "https://fixture.test" }) },
                        }.ToJsonString());
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            });
        }
        public void Dispose()
        {
            _lifetime.Cancel(); _pipe.Dispose(); _run.GetAwaiter().GetResult(); _lifetime.Dispose();
        }
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _oldDirectory;
        Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", _oldProfile);
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
        var root = ProfilePaths.Create(_profile).Root;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
