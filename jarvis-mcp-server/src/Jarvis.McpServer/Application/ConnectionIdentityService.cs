using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.McpServer.Application;

/// <summary>Read-only projection of an already authenticated OAuth owner/device binding.</summary>
public sealed class ConnectionIdentityService(AppDbContext db, IAgentRouter router, JarvisOptions options)
{
    // These IDs come only from CurrentAccess, never from tool arguments. The join rechecks ownership
    // and authorization while reading display fields; no Agent call or session is needed.
    public async Task<ConnectionIdentity> ReadAsync(string ownerId, string deviceId, CancellationToken ct)
    {
        var row = await (from device in db.Devices.AsNoTracking()
                         join user in db.Users.AsNoTracking() on device.OwnerId equals user.Id
                         where device.Id == deviceId && user.Id == ownerId && device.Enabled && user.Status == "active"
                         select new
                         {
                             DeviceId = device.Id, DeviceName = device.Name, device.Enabled, device.Platform,
                             device.AgentVersion, device.LastSeenAt, AccountId = user.Id,
                             user.DisplayName, user.Email, user.Role, user.Status
                         }).SingleOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException("Connection identity is not available to this OAuth account.");

        if (string.IsNullOrWhiteSpace(row.DeviceId) || string.IsNullOrWhiteSpace(row.AccountId))
            throw new InvalidOperationException("Connection identity is unavailable.");

        var name = Display(row.DisplayName);
        var email = Display(row.Email);
        var deviceName = Display(row.DeviceName);
        var label = string.Join(" — ", new[] { name ?? email, deviceName }.Where(value => value is not null));

        // Device.Id is an existing persisted random UUID with immutable ownership. Enrollment creates
        // a fresh ID; rename/refresh/reconnect/token rotation never change or reassign it. Thus it
        // identifies the FULL connectable profile, including two devices owned by the same account.
        var profile = new ConnectionProfile(row.DeviceId, name, email, Display(label));
        return new(profile,
            new(row.AccountId, name, email, row.Role, row.Status),
            new(row.DeviceId, deviceName, row.Enabled, router.IsOnline(row.AccountId, row.DeviceId),
                Display(row.Platform), Display(row.AgentVersion), row.LastSeenAt > 0 ? row.LastSeenAt : null),
            new(options.PublicOrigin, typeof(ConnectionIdentityService).Assembly.GetName().Version?.ToString(3) ?? "unknown"));
    }

    private static string? Display(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
