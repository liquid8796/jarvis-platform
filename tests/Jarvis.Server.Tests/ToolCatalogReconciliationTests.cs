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

    [Fact]
    public async Task Reconcile_prefixes_only_legacy_default_unity_names_and_preserves_custom_aliases()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);

        var schema = WireJson.Element(new { type = "object", additionalProperties = false });
        // Simulate a pre-1.0.97 Agent manifest. The server must normalize it even before the
        // desktop Agent is upgraded and must migrate only the exact old default policy name.
        var listTools = new ToolDescriptor("unity.list_tools", "list_tools", "unity",
            "Discover Unity tools.", schema, false, true);
        var callTool = new ToolDescriptor("unity.call_tool", "call_tool", "unity",
            "Call a Unity tool.", schema, false, true);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.SingleAsync(item => item.Id == peer.DeviceId);
            var live = JsonSerializer.Deserialize<ToolDescriptor[]>(device.CapabilitiesJson, WireJson.Options) ?? [];
            device.CapabilitiesJson = JsonSerializer.Serialize(live.Concat([listTools, callTool]), WireJson.Options);
            db.Tools.AddRange(
                new ToolEntry
                {
                    AgentToolId = listTools.Id,
                    Name = "list_tools",
                    Category = "unity",
                    Description = "Legacy Unity default",
                    PublicationMode = ToolPublicationMode.Published,
                    Enabled = true
                },
                new ToolEntry
                {
                    AgentToolId = callTool.Id,
                    Name = "my_unity_action",
                    Category = "unity",
                    Description = "Administrator alias",
                    PublicationMode = ToolPublicationMode.Published,
                    Enabled = true
                });
            await db.SaveChangesAsync();
        }

        var response = await admin.PostAsJsonAsync("/api/admin/tools/import", new { });
        response.EnsureSuccessStatusCode();

        using var verify = app.Services.CreateScope();
        var verifiedDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrated = await verifiedDb.Tools.SingleAsync(tool => tool.AgentToolId == listTools.Id);
        var customized = await verifiedDb.Tools.SingleAsync(tool => tool.AgentToolId == callTool.Id);
        Assert.Equal("unity_list_tools", migrated.Name);
        Assert.Equal(ToolPublicationMode.Published, migrated.PublicationMode);
        Assert.Equal("my_unity_action", customized.Name);
        Assert.Equal(ToolPublicationMode.Published, customized.PublicationMode);

        var installed = AgentToolCatalogRules.Installed([
            JsonSerializer.Serialize(new[] { listTools, callTool }, WireJson.Options)
        ]);
        Assert.Contains(installed, tool => tool.Id == listTools.Id && tool.Name == "unity_list_tools");
        Assert.Contains(installed, tool => tool.Id == callTool.Id && tool.Name == "unity_call_tool");
    }

    [Theory]
    [InlineData("list_tools", "unity.list_tools")]
    [InlineData("call_tool", "unity.call_tool")]
    [InlineData("list_resources", "unity.list_resources")]
    [InlineData("read_resource", "unity.read_resource")]
    [InlineData("list_prompts", "unity.list_prompts")]
    [InlineData("get_prompt", "unity.get_prompt")]
    public void Legacy_unity_public_names_resolve_only_to_their_canonical_bridge_tool(
        string legacyName, string expectedToolId)
    {
        Assert.True(AgentToolCatalogRules.TryResolveLegacyPublicName(legacyName, out var toolId));
        Assert.Equal(expectedToolId, toolId);
        Assert.True(AgentToolCatalogRules.TryGetPreferredPublicName(toolId, out var preferredName));
        Assert.Equal("unity_" + legacyName, preferredName);
        Assert.True(AgentToolCatalogRules.IsLegacyDefaultPublicName(toolId, legacyName));
        Assert.False(AgentToolCatalogRules.IsLegacyDefaultPublicName(toolId, "custom_alias"));
    }
}