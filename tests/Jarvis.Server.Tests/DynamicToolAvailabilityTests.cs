using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Plugins;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Server.Tests;

public sealed partial class AgentTaskMcpTests
{
    [Fact]
    public async Task Stateful_mcp_session_receives_tool_list_changed_when_agent_catalog_changes()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var streamResponse = await client.GetAsync("/mcp", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        streamResponse.EnsureSuccessStatusCode();
        await using var stream = await streamResponse.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);

        var dynamicTool = new RuntimeDynamicTool();
        peer.Connection.ApplyPluginCatalog(new PluginCatalogSnapshot(
            [],
            new Dictionary<string, IAgentTool> { [dynamicTool.Descriptor.Id] = dynamicTool },
            new Dictionary<string, IReadOnlyList<string>>()));

        var received = false;
        while (!timeout.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) break;
            if (line.Contains("notifications/tools/list_changed", StringComparison.Ordinal))
            {
                received = true;
                break;
            }
        }

        Assert.True(received, "The stateful MCP notification stream did not receive tools/list_changed.");
    }

    [Fact]
    public async Task Existing_mcp_session_can_use_tools_added_after_the_session_started()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        using var client = await GrantAsync(app, admin, peer.DeviceId);

        var initial = await RpcAsync(client, "tools/list", new { });
        var initialNames = initial.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Contains(DynamicAgentToolNames.Search, initialNames);
        Assert.Contains(DynamicAgentToolNames.Call, initialNames);
        Assert.DoesNotContain("runtime_dynamic", initialNames);

        var dynamicTool = new RuntimeDynamicTool();
        peer.Connection.ApplyPluginCatalog(new PluginCatalogSnapshot(
            [],
            new Dictionary<string, IAgentTool> { [dynamicTool.Descriptor.Id] = dynamicTool },
            new Dictionary<string, IReadOnlyList<string>>()));

        await WaitForCapabilityAsync(app, peer.DeviceId, dynamicTool.Descriptor.Id, present: true);

        var mirrored = await ReadPolicyAsync(app, dynamicTool.Descriptor.Id);
        Assert.Equal(ToolPublicationMode.Auto, mirrored.PublicationMode);

        // Do not refresh tools/list here. The session began before this tool existed, so only the
        // permanent gateway tools from the original surface are available to the model.
        var search = await CallAsync(client, DynamicAgentToolNames.Search, new { query = dynamicTool.Descriptor.Id });
        var searchBody = search.GetProperty("structuredContent");
        var found = Assert.Single(searchBody.GetProperty("tools").EnumerateArray());
        Assert.Equal(dynamicTool.Descriptor.Id, found.GetProperty("id").GetString());
        Assert.Equal("Auto", found.GetProperty("publicationMode").GetString());

        var invoked = await CallAsync(client, DynamicAgentToolNames.Call, new
        {
            toolId = dynamicTool.Descriptor.Id,
            arguments = new { value = "after-start" }
        });
        Assert.False(invoked.GetProperty("isError").GetBoolean(), invoked.GetRawText());
        Assert.Equal("dynamic:after-start", invoked.GetProperty("content")[0].GetProperty("text").GetString());

        var refreshed = await RpcAsync(client, "tools/list", new { });
        Assert.Contains(refreshed.GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == dynamicTool.Descriptor.Name);

        var hide = await admin.PostAsJsonAsync("/api/admin/tools/bulk-availability", new
        {
            tools = new[] { new { id = mirrored.Id, revision = mirrored.Revision } },
            publicationMode = "Hidden"
        });
        hide.EnsureSuccessStatusCode();

        var hiddenSearch = await CallAsync(client, DynamicAgentToolNames.Search, new { query = dynamicTool.Descriptor.Id });
        Assert.Empty(hiddenSearch.GetProperty("structuredContent").GetProperty("tools").EnumerateArray());

        var hiddenCall = await CallAsync(client, DynamicAgentToolNames.Call, new
        {
            toolId = dynamicTool.Descriptor.Id,
            arguments = new { value = "blocked" }
        });
        Assert.True(hiddenCall.GetProperty("isError").GetBoolean());
        Assert.Contains("hidden", hiddenCall.GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase);

        var hiddenPolicy = await ReadPolicyAsync(app, dynamicTool.Descriptor.Id);
        var auto = await admin.PostAsJsonAsync("/api/admin/tools/bulk-availability", new
        {
            tools = new[] { new { id = hiddenPolicy.Id, revision = hiddenPolicy.Revision } },
            publicationMode = "Auto"
        });
        auto.EnsureSuccessStatusCode();

        peer.Connection.ApplyPluginCatalog(new PluginCatalogSnapshot(
            [],
            new Dictionary<string, IAgentTool>(),
            new Dictionary<string, IReadOnlyList<string>>()));
        await WaitForCapabilityAsync(app, peer.DeviceId, dynamicTool.Descriptor.Id, present: false);

        var removedCall = await CallAsync(client, DynamicAgentToolNames.Call, new
        {
            toolId = dynamicTool.Descriptor.Id,
            arguments = new { value = "gone" }
        });
        Assert.True(removedCall.GetProperty("isError").GetBoolean());
        Assert.Contains("not installed", removedCall.GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ToolEntry> ReadPolicyAsync(ServerFixture app, string toolId)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Tools.AsNoTracking().SingleAsync(tool => tool.AgentToolId == toolId);
    }

    private static async Task WaitForCapabilityAsync(ServerFixture app, string deviceId, string toolId, bool present)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var json = await db.Devices.AsNoTracking().Where(device => device.Id == deviceId)
                .Select(device => device.CapabilitiesJson).SingleAsync(timeout.Token);
            var ids = (JsonSerializer.Deserialize<ToolDescriptor[]>(json, WireJson.Options) ?? [])
                .Select(tool => tool.Id).ToHashSet(StringComparer.Ordinal);
            if (ids.Contains(toolId) == present) return;
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class RuntimeDynamicTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "runtime.dynamic",
            "runtime_dynamic",
            "runtime",
            "Tool added after the MCP session is already running.",
            WireJson.Element(new
            {
                type = "object",
                properties = new { value = new { type = "string" } },
                required = new[] { "value" },
                additionalProperties = false
            }),
            ReadOnly: true);

        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply("dynamic:" + arguments.GetProperty("value").GetString()));
    }
}
