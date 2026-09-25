using Jarvis.McpServer.Application;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.McpServer.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Jarvis.McpServer.Transport;
[ApiController, Authorize, Route("api/admin")]
public sealed class AdminController(AppDbContext db, UserManager<AppUser> users, ToolCatalogService catalog,
    IAgentRouter router, IAuditWriter audit) : ControllerBase
{
    private async Task<string> Admin(CancellationToken ct) => (await CurrentAccess.RequireAsync(User, db, true, ct)).Id;
    [HttpGet("tools")]
    public async Task<object> Tools(CancellationToken ct) { await Admin(ct); return await catalog.ListAsync(ct); }
    [HttpGet("capabilities")]
    public async Task<object> Capabilities(CancellationToken ct) { await Admin(ct); return await catalog.InstalledAsync(ct); }
    [HttpGet("tools/{id}")]
    public async Task<object> Detail(string id, CancellationToken ct)
    {
        await Admin(ct); var tool = await db.Tools.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException();
        return new { tool, capability = (await catalog.InstalledAsync(ct)).FirstOrDefault(t => t.Id == tool.AgentToolId) };
    }
    [HttpPost("tools")]
    public async Task<object> CreateTool(ToolRequest request, CancellationToken ct) => await Save(null, request, ct);
    [HttpPut("tools/{id}")]
    public async Task<object> UpdateTool(string id, ToolRequest request, CancellationToken ct) => await Save(id, request, ct);
    private async Task<object> Save(string? id, ToolRequest request, CancellationToken ct) => await catalog.SaveAsync(await Admin(ct), id,
        request.Name, request.AgentToolId, request.Description, request.RequestedPublicationMode, request.Revision, ct);
    [HttpPost("tools/bulk-availability")]
    public async Task<object> SetToolAvailability(BulkToolAvailabilityRequest request, CancellationToken ct) =>
        await catalog.SetPublicationModeAsync(await Admin(ct),
            request.Tools.ToDictionary(t => t.Id, t => t.Revision, StringComparer.Ordinal),
            request.RequestedPublicationMode, ct);
    [HttpPost("tools/import")]
    public async Task<object> Import(CancellationToken ct) => new { imported = await catalog.ImportAsync(await Admin(ct), ct) };
    [HttpDelete("tools/{id}")]
    public async Task<IActionResult> DeleteTool(string id, CancellationToken ct) { await catalog.DeleteAsync(await Admin(ct), id, ct); return NoContent(); }
    [HttpGet("users")]
    public async Task<object> Users(CancellationToken ct)
    { await Admin(ct); return (await db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct)).Select(AccountController.View); }
    [HttpPost("users")]
    public async Task<object> CreateUser(AdminUserRequest request, CancellationToken ct)
    {
        var actor = await Admin(ct);
        if (string.IsNullOrEmpty(request.Password)) throw new ArgumentException("An initial password is required.");
        var user = new AppUser { Email = request.Email, UserName = request.Email, DisplayName = request.DisplayName, Role = request.Role, Status = request.Status };
        AccountController.Check(await users.CreateAsync(user, request.Password));
        await audit.WriteAsync(new() { UserId = actor, Action = "admin.user.create", Outcome = user.Id }, ct);
        return AccountController.View(user);
    }
    [HttpPut("users/{id}")]
    public async Task<object> UpdateUser(string id, AdminUserRequest request, CancellationToken ct)
    {
        var actor = await Admin(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = await users.FindByIdAsync(id) ?? throw new KeyNotFoundException("User not found.");
        await ProtectLastAdmin(user, request.Role, request.Status, ct);
        if (actor == id && (request.Role != "admin" || request.Status != "active")) throw new InvalidOperationException("You cannot disable or demote your current administrator account.");
        user.Email = request.Email; user.UserName = request.Email; user.DisplayName = request.DisplayName; user.Role = request.Role; user.Status = request.Status;
        AccountController.Check(await users.UpdateAsync(user));
        if (!string.IsNullOrEmpty(request.Password))
            AccountController.Check(await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), request.Password));
        AccountController.Check(await users.UpdateSecurityStampAsync(user));
        await transaction.CommitAsync(ct); router.DisconnectUser(id);
        await audit.WriteAsync(new() { UserId = actor, Action = "admin.user.update", Outcome = id }, ct);
        return AccountController.View(user);
    }
    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(string id, CancellationToken ct)
    {
        var actor = await Admin(ct);
        if (actor == id) throw new InvalidOperationException("Cannot delete your current administrator account.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = await users.FindByIdAsync(id) ?? throw new KeyNotFoundException("User not found.");
        await ProtectLastAdmin(user, "user", "disabled", ct);
        AccountController.Check(await users.DeleteAsync(user)); await transaction.CommitAsync(ct); router.DisconnectUser(id);
        await audit.WriteAsync(new() { UserId = actor, Action = "admin.user.delete", Outcome = id }, ct);
        return NoContent();
    }
    private async Task ProtectLastAdmin(AppUser user, string role, string status, CancellationToken ct)
    {
        if (user.Role == "admin" && user.Status == "active" && (role != "admin" || status != "active") &&
            !await db.Users.AnyAsync(u => u.Id != user.Id && u.Role == "admin" && u.Status == "active", ct))
            throw new InvalidOperationException("At least one active administrator must remain.");
    }
}
