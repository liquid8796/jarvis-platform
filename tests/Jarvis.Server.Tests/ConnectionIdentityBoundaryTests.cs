using System.Text.Json;
using Jarvis.McpServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Server.Tests;

public sealed partial class OAuthFlowTests
{
    [Fact]
    public async Task Identity_reads_do_not_depend_on_valid_agent_catalog_metadata()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var grant = await IdentityGrantAsync(app, admin);
        var listed = await IdentityRpcAsync(grant.Client, "tools/list", new { });
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.SingleAsync(d => d.Id == grant.DeviceId);
            device.CapabilitiesJson = "invalid catalog JSON";
            device.Platform = "Windows";
            device.AgentVersion = "1.0.97";
            device.LastSeenAt = 1700000000;
            await db.SaveChangesAsync();
        }
        Assert.Equal(grant.DeviceId, await IdentityProfileIdAsync(grant.Client));
        var identity = AssertIdentityOutput(listed, IdentityTool, await IdentityCallAsync(grant.Client, IdentityTool, new { }));
        Assert.Equal("Windows", identity.GetProperty("device").GetProperty("platform").GetString());
        Assert.Equal("1.0.97", identity.GetProperty("device").GetProperty("agentVersion").GetString());
        Assert.Equal(1700000000, identity.GetProperty("device").GetProperty("lastSeenAt").GetInt64());
        Assert.False(identity.GetProperty("device").GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task Identity_rejected_selectors_are_audited_without_their_values()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var grant = await IdentityGrantAsync(app, admin);
        const string selector = "do-not-reflect-this-selector";
        foreach (var name in new[] { ProfileTool, IdentityTool })
        {
            var result = await IdentityCallAsync(grant.Client, name, new { email = selector });
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.DoesNotContain(selector, result.GetRawText());
        }
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entries = await db.Audit.Where(a => a.Outcome == "rejected:INVALID_ARGUMENTS").ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(grant.DeviceId, entry.DeviceId));
        var auditJson = JsonSerializer.Serialize(entries);
        Assert.DoesNotContain(selector, auditJson);
        Assert.DoesNotContain(ServerFixture.AdminEmail, auditJson);
    }

    [Fact]
    public async Task Identity_paused_agent_keeps_read_only_profile_available()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        using var grant = await IdentityGrantAsync(app, admin, peer.DeviceId);
        peer.Connection.Pause();
        Assert.Equal(peer.DeviceId, await IdentityProfileIdAsync(grant.Client));
        var identity = (await IdentityCallAsync(grant.Client, IdentityTool, new { })).GetProperty("structuredContent");
        Assert.True(identity.GetProperty("device").GetProperty("online").GetBoolean());
        Assert.Empty(peer.Connection.GetLocalSessionOverview());
    }

    [Fact]
    public async Task Identity_deleted_account_never_leaves_a_fallback_profile()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        using var grant = await IdentityGrantAsync(app, admin);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.SingleAsync(d => d.Id == grant.DeviceId);
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == device.OwnerId));
            await db.SaveChangesAsync();
        }
        foreach (var name in new[] { ProfileTool, IdentityTool })
            await AssertIdentityDeniedAsync(grant.Client, name);
    }
}
