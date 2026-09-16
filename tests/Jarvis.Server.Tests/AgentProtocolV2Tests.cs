using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Server.Tests;

public sealed class AgentProtocolV2Tests
{
    [Fact]
    public async Task V2_agent_negotiates_protocol_capabilities_without_changing_wire_envelope_version()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var enrolled = await (await admin.PostAsJsonAsync("/api/devices", new { name = "Protocol v2 agent" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var deviceId = enrolled.GetProperty("deviceId").GetString()!;
        var token = enrolled.GetProperty("token").GetString()!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
        using var socket = await ws.ConnectAsync(new Uri("wss://localhost/agent/connect"), timeout.Token);
        var wire = new WireSocket(socket);
        var tool = new ToolDescriptor("test.echo", "test__echo", "test", "protocol fixture",
            WireJson.Element(new { type = "object" }), true);

        await wire.SendAsync(new WireMessage("hello")
        {
            Hello = new AgentHello(deviceId, "1.0.56", "test", "fake", [tool])
            {
                ProtocolVersion = AgentProtocolVersion.Current,
                Capabilities = AgentProtocolCapabilities.Agent
            }
        }, timeout.Token);

        var welcome = await wire.ReceiveAsync(timeout.Token);
        Assert.Equal(1, welcome!.Version);
        Assert.Equal(AgentProtocolVersion.Current, welcome.ProtocolVersion);
        Assert.Contains(AgentProtocolCapabilities.CatalogSync, welcome.Capabilities!);
        Assert.Contains(AgentProtocolCapabilities.CapabilityLeases, welcome.Capabilities!);
    }
}
