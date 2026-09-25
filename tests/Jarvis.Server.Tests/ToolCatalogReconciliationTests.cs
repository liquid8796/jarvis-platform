using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.McpServer.Application;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Server.Tests;

public sealed class ToolCatalogReconciliationTests
{
    [Fact]
    public async Task Reconcile_removes_retired_and_uninstalled_rows_but_preserves_live_policy()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);

        const string activeId = "unified_exec.exec_command";
        const string retiredId = "computer.screenshot";
        const string staleId = "removed.fixture";

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.SingleAsync(item => item.Id == peer.DeviceId);
            var live = JsonSerializer.Deserialize<ToolDescriptor[]>(device.CapabilitiesJson, WireJson.Options) ?? [];
            var retired = new ToolDescriptor(retiredId, "computer__screenshot", "computer",
                "Retired computer screenshot fixture.", WireJson.Element(new { type = "object" }), true, true);
            device.CapabilitiesJson = JsonSerializer.Serialize(live.Append(retired), WireJson.Options);
            db.Tools.AddRange(
                new ToolEntry
                {
                    AgentToolId = retiredId,
                    Name = "computer__screenshot",
                    Category = "computer",
                    Description = "Retired fixture row",
                    PublicationMode = ToolPublicationMode.Hidden,
                    Enabled = false
                },
                new ToolEntry
                {
                    AgentToolId = staleId,
                    Name = "removed_fixture",
                    Category = "fixture",
                    Description = "Capability no device advertises",
                    PublicationMode = ToolPublicationMode.Published,
                    Enabled = true
                });
            await db.SaveChangesAsync();
        }

        var response = await admin.PostAsJsonAsync("/api/admin/tools/import", new { });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("removed").GetInt32());

        using var verify = app.Services.CreateScope();
        var verifiedDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verifiedDb.Tools.AnyAsync(tool => tool.AgentToolId == retiredId));
        Assert.False(await verifiedDb.Tools.AnyAsync(tool => tool.AgentToolId == staleId));
        var verifiedDevice = await verifiedDb.Devices.AsNoTracking().SingleAsync(item => item.Id == peer.DeviceId);
        Assert.DoesNotContain(DeviceService.Capabilities(verifiedDevice), tool => tool.Id == retiredId);
        var active = await verifiedDb.Tools.SingleAsync(tool => tool.AgentToolId == activeId);
        Assert.Equal(ToolPublicationMode.Published, active.PublicationMode);

        var catalog = verify.ServiceProvider.GetRequiredService<ToolCatalogService>();
        var installed = await catalog.InstalledAsync(CancellationToken.None);
        Assert.Contains(installed, tool => tool.Id == activeId);
        Assert.DoesNotContain(installed, tool => tool.Id == retiredId);
    }
}