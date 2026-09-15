using System.Security.Claims;
using Jarvis.McpServer.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
namespace Jarvis.McpServer.Security;

public static class CurrentAccess
{
    public const string DeviceClaim = "jarvis_device";
    public const string StampClaim = "jarvis_stamp";
    public const string Scope = "mcp:tools";
    public static string UserId(ClaimsPrincipal user) => user.FindFirstValue(OpenIddictConstants.Claims.Subject)
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
    public static async Task<AppUser> RequireAsync(ClaimsPrincipal principal, AppDbContext db, bool admin, CancellationToken ct)
    {
        var id = UserId(principal);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user?.Status != "active" || (admin && user.Role != "admin")) throw new UnauthorizedAccessException();
        var cookieStamp = principal.FindFirstValue(new ClaimsIdentityOptions().SecurityStampClaimType);
        if (cookieStamp is not null && cookieStamp != user.SecurityStamp) throw new UnauthorizedAccessException("Web session revoked.");
        return user;
    }
    public static async Task<(AppUser User, DeviceIdentity Device)> RequireMcpAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await RequireAsync(http.User, db, false, ct);
        if (!http.User.HasScope(Scope) || http.User.FindFirstValue(StampClaim) != user.SecurityStamp)
            throw new UnauthorizedAccessException("MCP grant revoked.");
        var id = http.User.FindFirstValue(DeviceClaim);
        if (id is null || !await db.Devices.AnyAsync(d => d.Id == id && d.OwnerId == user.Id && d.Enabled, ct))
            throw new UnauthorizedAccessException("Selected device is not available to this user.");
        return (user, new DeviceIdentity(id));
    }
    public sealed record DeviceIdentity(string Id);
}
