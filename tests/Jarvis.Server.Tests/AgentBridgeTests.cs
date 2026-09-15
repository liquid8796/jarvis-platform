using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace Jarvis.Server.Tests;
public sealed class AgentBridgeTests
{
    [Fact] public async Task Authorized_websocket_round_trips_tool_call_and_rejects_other_owner()
    {
        using var app=new ServerFixture();using var admin=await app.Admin();
        var result=await (await admin.PostAsJsonAsync("/api/devices",new{name="Fake agent fixture"})).Content.ReadFromJsonAsync<JsonElement>();
        var deviceId=result.GetProperty("deviceId").GetString()!;var token=result.GetProperty("token").GetString()!;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ws=app.Server.CreateWebSocketClient();ws.ConfigureRequest=request=>request.Headers.Authorization="Bearer "+token;
        using var socket=await ws.ConnectAsync(new Uri("wss://localhost/agent/connect"),timeout.Token);
        var wire=new WireSocket(socket);
        var descriptor=new ToolDescriptor("test.echo","test__echo","test","Fixture tool",WireJson.Element(new{type="object"}),true);
        await wire.SendAsync(new("hello"){Hello=new(deviceId,"1.0.16","test","fake",[descriptor])},timeout.Token);
        Assert.Equal("welcome",(await wire.ReceiveAsync(timeout.Token))!.Type);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner=(await db.Devices.AsNoTracking().SingleAsync(d=>d.Id==deviceId)).OwnerId;
        var router=app.Services.GetRequiredService<IAgentRouter>();Assert.True(router.IsOnline(owner,deviceId));Assert.False(router.IsOnline("other-owner",deviceId));
        var pending=router.CallAsync(owner,deviceId,"test.echo",WireJson.Element(new{text="hello"}),"test",timeout.Token);
        var call=await wire.ReceiveAsync(timeout.Token);Assert.Equal("call",call!.Type);Assert.Equal("test.echo",call.ToolId);
        await wire.SendAsync(new("result"){Id=call.Id,Result=new("echo: hello")},timeout.Token);
        Assert.Equal("echo: hello",(await pending).Text);
        router.DisconnectDevice(deviceId);
    }
}
