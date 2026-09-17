using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Server.Tests;

public sealed class ExecutionSettingsBridgeTests
{
    [Fact]
    public async Task Settings_are_acknowledged_and_five_calls_do_not_hit_the_old_four_slot_cap()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var enrollment = await (await admin.PostAsJsonAsync("/api/devices", new { name = "Execution limits fixture" })).Content.ReadFromJsonAsync<JsonElement>();
        var device = enrollment.GetProperty("deviceId").GetString()!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + enrollment.GetProperty("token").GetString();
        using var socket = await ws.ConnectAsync(new Uri("wss://localhost/agent/connect"), timeout.Token);
        var wire = new WireSocket(socket);
        var read = new ToolDescriptor("test.read", "test__read", "test", "Read fixture", WireJson.Element(new { type = "object" }), true);
        await wire.SendAsync(new("hello") { Hello = new(device, "1.0.64", "test", "test", [read])
        { ProtocolVersion = AgentProtocolVersion.Current, Capabilities = AgentProtocolCapabilities.Agent,
          ExecutionSettings = new() { MaxConcurrentCalls = 5, MaxQueuedCalls = 0 } } }, timeout.Token);
        var welcome = await wire.ReceiveAsync(timeout.Token);
        Assert.Equal("welcome", welcome!.Type);
        Assert.Equal(1, welcome.ExecutionSettingsRevision);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = (await db.Devices.AsNoTracking().SingleAsync(d => d.Id == device)).OwnerId;
        var router = app.Services.GetRequiredService<IAgentRouter>();
        var pending = Enumerable.Range(0, 5).Select(_ => router.CallAsync(owner, device, "test.read", WireJson.Element(new { }), AgentSessionRules.NewSessionId(), timeout.Token)).ToArray();
        var calls = new List<WireMessage>();
        for (var i = 0; i < 5; i++) calls.Add((await wire.ReceiveAsync(timeout.Token))!);
        Assert.All(calls, call => Assert.Equal(owner, call.OwnerId));
        var full = await Assert.ThrowsAsync<AgentRequestException>(() => router.CallAsync(owner, device, "test.read", WireJson.Element(new { }), AgentSessionRules.NewSessionId(), timeout.Token));
        Assert.Equal("QUEUE_FULL", full.Code);
        var control = router.CallAsync(owner, device, "process.read", WireJson.Element(new { }), AgentSessionRules.NewSessionId(), timeout.Token);
        var controlCall = await wire.ReceiveAsync(timeout.Token);
        Assert.Equal("process.read", controlCall!.ToolId);
        await wire.SendAsync(new("result") { Id = controlCall.Id, Result = new("status-ready") }, timeout.Token);
        Assert.Equal("status-ready", (await control).Text);
        foreach (var call in calls) await wire.SendAsync(new("result") { Id = call.Id, Result = new("done") }, timeout.Token);
        await Task.WhenAll(pending);
        await wire.SendAsync(new("execution.settings.changed")
        { ExecutionSettings = new() { Revision = 2, MaxConcurrentCalls = 2, MaxQueuedCalls = 0 }, ExecutionSettingsRevision = 2 }, timeout.Token);
        var ack = await wire.ReceiveAsync(timeout.Token);
        Assert.Equal("execution.settings.ack", ack!.Type);
        Assert.Equal(2, ack.ExecutionSettingsRevision);
        // A stale revision cannot replace the user's newer settings.
        await wire.SendAsync(new("execution.settings.changed")
        { ExecutionSettings = new() { Revision = 1, MaxConcurrentCalls = 100 }, ExecutionSettingsRevision = 1 }, timeout.Token);
        Assert.Equal(2, (await wire.ReceiveAsync(timeout.Token))!.ExecutionSettingsRevision);
        router.DisconnectDevice(device);
    }
}
