using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;

// Starts the production server with test-owned pipes and artifact root. No install,
// global mutex, registry/native-host registration, or production profile mutation.
if (args.Length != 4 || !OperatingSystem.IsWindows()) return 2;
var servicePipe = args[0];
var extensionPipe = args[1];
var root = Path.GetFullPath(args[2]);
var parentId = int.Parse(args[3]);
using var stop = new CancellationTokenSource();
using var server = new BrowserServiceServer(servicePipe, extensionPipe, root);
_ = Task.Run(async () =>
{
    try
    {
        using var parent = Process.GetProcessById(parentId);
        await parent.WaitForExitAsync(stop.Token);
        stop.Cancel();
    }
    catch (ArgumentException) { stop.Cancel(); }
    catch (OperationCanceledException) { }
});
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine(JsonSerializer.Serialize(new
{
    harness = "production-browser-service-private-pipes",
    serviceAssembly = typeof(BrowserServiceServer).Assembly.FullName,
    servicePipe,
    extensionPipe,
    root
}));
var serverRun = server.RunAsync(stop.Token);
using var client = new BrowserRuntimeClient(servicePipe);
var suite = new SessionBrowserToolSet(client, new ComputerStateTracker());
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
try
{
    // This outer test-owned facade adds framing only. Actual tool routing,
    // observation freshness, client RPC and server execution are production code.
    await using var pipe = new NamedPipeServerStream(servicePipe + "-front", PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    await pipe.WaitForConnectionAsync(stop.Token);
    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    while (await reader.ReadLineAsync(stop.Token) is { } line)
    {
        var message = JsonNode.Parse(line)!.AsObject();
        var response = new JsonObject { ["id"] = message["id"]!.DeepClone(), ["ok"] = true };
        try
        {
            switch (message["kind"]!.GetValue<string>())
            {
                case "hello":
                    response["facade"] = "production-session-browser-toolset";
                    break;
                case "endSession":
                    await suite.StopSessionAsync(new AgentSessionIdentity("live-test", "live-device", message["sessionId"]!.GetValue<string>()),
                        message["close"]?.GetValue<bool>() == true, stop.Token);
                    break;
                case "execute":
                    var request = message["request"]!.Deserialize<BrowserRuntimeRequest>(json)!;
                    var arguments = JsonNode.Parse(request.Arguments.GetRawText())!.AsObject();
                    arguments["browserFamily"] = request.Context.BrowserFamily;
                    var context = new AgentExecutionContext(root, request.Context.CallId, request.Context.ApplicationSessionId!)
                    {
                        OwnerId = "live-test", AgentDeviceId = "live-device", FullPermission = true,
                        SessionCancellation = stop.Token
                    };
                    var tool = suite.Tools.Single(item => item.Descriptor.Id == request.ToolId);
                    var reply = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(arguments), context, stop.Token);
                    response["reply"] = JsonSerializer.SerializeToNode(reply, json);
                    response["handshake"] = JsonSerializer.SerializeToNode(client.Handshake, json);
                    break;
                default: throw new ArgumentException("Unsupported test facade request.");
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or TimeoutException)
        {
            response["ok"] = false;
            response["error"] = error.Message;
        }
        await writer.WriteLineAsync(response.ToJsonString());
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
finally
{
    stop.Cancel();
    try { await serverRun; } catch (OperationCanceledException) { }
}
return 0;
