using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
namespace Jarvis.McpServer.Application;
public sealed class DeviceService(AppDbContext db, JarvisOptions options, IAgentRouter router, IAuditWriter audit)
{
    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static IReadOnlyList<ToolDescriptor> Capabilities(Device device) =>
        JsonSerializer.Deserialize<ToolDescriptor[]>(device.CapabilitiesJson, WireJson.Options) ?? [];
    public async Task<IReadOnlyList<DeviceView>> ListAsync(string owner, CancellationToken ct) =>
        (await db.Devices.AsNoTracking().Where(d => d.OwnerId == owner).OrderBy(d => d.Name).ToListAsync(ct))
        .Select(d => new DeviceView(d.Id, d.Name, d.Enabled, router.IsOnline(owner, d.Id), d.Platform,
            d.AgentVersion, d.LastSeenAt, Capabilities(d).Count, d.Revision)).ToArray();
    public async Task<EnrollmentResult> EnrollAsync(string owner, string name, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.Devices.CountAsync(d => d.OwnerId == owner, ct) >= 10)
            throw new InvalidOperationException("At most ten devices per account.");
        var token = "jra_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var device = new Device { Name = name, OwnerId = owner, TokenHash = TokenHash(token),
            TokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds() };
        db.Devices.Add(device); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        await audit.WriteAsync(new() { UserId = owner, DeviceId = device.Id, Action = "device.enroll", Outcome = "created" }, ct);
        return new(device.Id, options.PublicOrigin, token);
    }
    public async Task UpdateAsync(string owner, string id, string name, bool enabled, string revision, CancellationToken ct)
    {
        var device = await OwnedAsync(owner, id, ct);
        if (device.Revision != revision) throw new InvalidOperationException("Device was changed. Refresh before saving.");
        device.Name = name; device.Enabled = enabled; device.Revision = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(ct);
        if (!enabled) router.DisconnectDevice(id);
        await audit.WriteAsync(new() { UserId = owner, DeviceId = id, Action = "device.update", Outcome = enabled ? "enabled" : "disabled" }, ct);
    }
    public async Task<EnrollmentResult> RotateAsync(string owner, string id, CancellationToken ct)
    {
        var device = await OwnedAsync(owner, id, ct);
        var token = "jra_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        device.TokenHash = TokenHash(token); device.TokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        device.Revision = Guid.NewGuid().ToString("N"); await db.SaveChangesAsync(ct); router.DisconnectDevice(id);
        await audit.WriteAsync(new() { UserId = owner, DeviceId = id, Action = "device.rotate", Outcome = "rotated" }, ct);
        return new(id, options.PublicOrigin, token);
    }
    public async Task DeleteAsync(string owner, string id, CancellationToken ct)
    {
        db.Devices.Remove(await OwnedAsync(owner, id, ct)); await db.SaveChangesAsync(ct); router.DisconnectDevice(id);
        await audit.WriteAsync(new() { UserId = owner, DeviceId = id, Action = "device.delete", Outcome = "deleted" }, ct);
    }
    public Task<Device> OwnedAsync(string owner, string id, CancellationToken ct) =>
        GetOwnedAsync(owner, id, ct);
    private async Task<Device> GetOwnedAsync(string owner, string id, CancellationToken ct) =>
        await db.Devices.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == owner, ct)
            ?? throw new KeyNotFoundException("Device not found.");
}
