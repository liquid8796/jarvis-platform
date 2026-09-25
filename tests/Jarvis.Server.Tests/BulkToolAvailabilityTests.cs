using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace Jarvis.Server.Tests;

public sealed class BulkToolAvailabilityTests
{
    private const string Route = "/api/admin/tools/bulk-availability";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Selected_tools_and_audits_commit_together(bool enabled)
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var tools = await SeedAsync(app, admin, !enabled);
        var response = await admin.PostAsJsonAsync(Route, Request(tools.Take(2), enabled));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("updated").GetInt32());
        Assert.Equal(2, result.GetProperty("selected").GetInt32());
        var current = await admin.GetFromJsonAsync<ToolEntry[]>("/api/admin/tools");
        foreach (var original in tools.Take(2))
        {
            var changed = Assert.Single(current!, t => t.Id == original.Id);
            Assert.Equal(enabled ? ToolPublicationMode.Published : ToolPublicationMode.Hidden, changed.PublicationMode); Assert.NotEqual(original.Revision, changed.Revision);
        }
        var untouched = Assert.Single(current!, t => t.Id == tools[2].Id);
        Assert.Equal(!enabled ? ToolPublicationMode.Published : ToolPublicationMode.Hidden, untouched.PublicationMode); Assert.Equal(tools[2].Revision, untouched.Revision);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audits = await db.Audit.Where(a => a.Action.StartsWith("catalog.bulk-")).ToListAsync();
        Assert.Equal(2, audits.Count); Assert.Single(audits.Select(a => a.CorrelationId).Distinct());
        Assert.All(audits, a => Assert.Equal(enabled ? "catalog.bulk-publish" : "catalog.bulk-hide", a.Action));
    }

    [Theory]
    [InlineData("stale", HttpStatusCode.Conflict)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("uninstalled", HttpStatusCode.BadRequest)]
    public async Task Invalid_batch_has_no_partial_updates_or_audits(string failure, HttpStatusCode expected)
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var tools = await SeedAsync(app, admin);
        if (failure == "stale") tools[1].Revision = "stale-revision";
        if (failure == "missing") tools[1].Id = Guid.NewGuid().ToString();
        if (failure == "uninstalled")
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.SingleAsync(); device.CapabilitiesJson = "[]"; await db.SaveChangesAsync();
        }
        var response = await admin.PostAsJsonAsync(Route, Request(tools.Take(2), true));
        Assert.Equal(expected, response.StatusCode);
        using var verify = app.Services.CreateScope(); var verifiedDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verifiedDb.Tools.AnyAsync(t => t.PublicationMode != ToolPublicationMode.Hidden));
        Assert.False(await verifiedDb.Audit.AnyAsync(a => a.Action.StartsWith("catalog.bulk-")));
    }

    [Fact]
    public async Task Noop_keeps_revisions_and_does_not_create_false_audits()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var tools = await SeedAsync(app, admin, true);
        var response = await admin.PostAsJsonAsync(Route, Request(tools, true)); response.EnsureSuccessStatusCode();
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updated").GetInt32());
        var current = await admin.GetFromJsonAsync<ToolEntry[]>("/api/admin/tools");
        Assert.All(current!, t => Assert.Equal(tools.Single(o => o.Id == t.Id).Revision, t.Revision));
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Audit.AnyAsync(a => a.Action.StartsWith("catalog.bulk-")));
    }

    [Fact]
    public async Task Disabling_remains_possible_when_the_original_capability_is_gone()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var tools = await SeedAsync(app, admin, true);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.SingleAsync(); device.CapabilitiesJson = "[]"; await db.SaveChangesAsync();
        }
        (await admin.PostAsJsonAsync(Route, Request(tools, false))).EnsureSuccessStatusCode();
        Assert.All((await admin.GetFromJsonAsync<ToolEntry[]>("/api/admin/tools"))!, t => Assert.Equal(ToolPublicationMode.Hidden, t.PublicationMode));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing-enabled")]
    [InlineData("null-items")]
    [InlineData("null-entry")]
    [InlineData("duplicate")]
    [InlineData("missing-revision")]
    [InlineData("too-many")]
    public async Task Request_validation_rejects_invalid_selection(string variant)
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var tools = await SeedAsync(app, admin);
        var selected = new { id = tools[0].Id, revision = tools[0].Revision };
        object body = variant switch
        {
            "empty" => new { tools = Array.Empty<object>(), enabled = true },
            "missing-enabled" => new { tools = new[] { selected } },
            "null-items" => new { tools = (object?)null, enabled = true },
            "null-entry" => new { tools = new object?[] { null }, enabled = true },
            "duplicate" => new { tools = new[] { selected, selected }, enabled = true },
            "missing-revision" => new { tools = new[] { new { id = tools[0].Id } }, enabled = true },
            _ => new { tools = Enumerable.Range(0, 501).Select(i => new { id = "id" + i, revision = "r" }).ToArray(), enabled = true }
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Route, body)).StatusCode);
        Assert.All((await admin.GetFromJsonAsync<ToolEntry[]>("/api/admin/tools"))!, t => Assert.Equal(ToolPublicationMode.Hidden, t.PublicationMode));
    }

    [Fact]
    public async Task Bulk_changes_require_CSRF_and_active_admin()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var tools = await SeedAsync(app, admin);
        using var anonymous = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Route, Request(tools, true))).StatusCode);
        (await admin.PostAsJsonAsync("/api/admin/users", new { email = "member@example.test", displayName = "Member",
            password = ServerFixture.Password, role = "user", status = "active" })).EnsureSuccessStatusCode();
        using var member = app.Client(); await ServerFixture.Csrf(member);
        (await member.PostAsJsonAsync("/api/auth/login", new { email = "member@example.test", password = ServerFixture.Password })).EnsureSuccessStatusCode();
        await ServerFixture.Csrf(member);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(Route, Request(tools, true))).StatusCode);
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Route, Request(tools, true))).StatusCode);
    }

    private static object Request(IEnumerable<ToolEntry> tools, bool enabled) =>
        new { tools = tools.Select(t => new { id = t.Id, revision = t.Revision }).ToArray(), enabled };

    private static async Task<ToolEntry[]> SeedAsync(ServerFixture app, HttpClient admin, bool enabled = false)
    {
        var response = await admin.PostAsJsonAsync("/api/devices", new { name = "Bulk catalog fixture" });
        response.EnsureSuccessStatusCode();
        var enrollment = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.Devices.SingleAsync(d => d.Id == enrollment.GetProperty("deviceId").GetString());
        var descriptors = Enumerable.Range(1, 3).Select(i => new ToolDescriptor("fixture.tool" + i,
            "fixture__tool" + i, "fixture", "Fixture tool " + i, WireJson.Element(new { type = "object" }), i == 1)).ToArray();
        device.CapabilitiesJson = JsonSerializer.Serialize(descriptors, WireJson.Options);
        var entries = descriptors.Select(t => new ToolEntry { Name = t.Name, AgentToolId = t.Id,
            Description = t.Description, Category = t.Category,
            PublicationMode = enabled ? ToolPublicationMode.Published : ToolPublicationMode.Hidden }).ToArray();
        db.Tools.AddRange(entries); await db.SaveChangesAsync(); return entries;
    }
}
