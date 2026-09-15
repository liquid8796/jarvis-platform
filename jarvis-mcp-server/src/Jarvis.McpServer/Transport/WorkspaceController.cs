using Jarvis.McpServer.Application;
using Jarvis.McpServer.Infrastructure;
using Jarvis.McpServer.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Jarvis.McpServer.Transport;
[ApiController, Authorize, Route("api")]
public sealed class WorkspaceController(AppDbContext db, DeviceService devices) : ControllerBase
{
    private async Task<string> Owner(CancellationToken ct) => (await CurrentAccess.RequireAsync(User, db, false, ct)).Id;
    [HttpGet("devices")]
    public async Task<object> Devices(CancellationToken ct) => await devices.ListAsync(await Owner(ct), ct);
    [HttpPost("devices")]
    public async Task<object> Enroll(DeviceRequest request, CancellationToken ct) => await devices.EnrollAsync(await Owner(ct), request.Name, ct);
    [HttpPut("devices/{id}")]
    public async Task<IActionResult> Update(string id, DeviceRequest request, CancellationToken ct)
    { await devices.UpdateAsync(await Owner(ct), id, request.Name, request.Enabled, request.Revision ?? "", ct); return NoContent(); }
    [HttpPost("devices/{id}/rotate")]
    public async Task<object> Rotate(string id, CancellationToken ct) => await devices.RotateAsync(await Owner(ct), id, ct);
    [HttpDelete("devices/{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    { await devices.DeleteAsync(await Owner(ct), id, ct); return NoContent(); }
    [HttpGet("devices/{id}/tools")]
    public async Task<object> DeviceTools(string id, CancellationToken ct) => DeviceService.Capabilities(await devices.OwnedAsync(await Owner(ct), id, ct));
    [HttpGet("activity")]
    public async Task<object> Activity(CancellationToken ct)
    {
        var owner = await Owner(ct);
        return await db.Audit.AsNoTracking().Where(a => a.UserId == owner).OrderByDescending(a => a.Id).Take(100).ToListAsync(ct);
    }
    [HttpGet("overview")]
    public async Task<object> Overview(CancellationToken ct)
    {
        var owner = await Owner(ct); var list = await devices.ListAsync(owner, ct);
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).ToUnixTimeSeconds();
        return new { devices = list.Count, online = list.Count(d => d.Online),
            tools = await db.Tools.CountAsync(t => t.Enabled, ct),
            callsToday = await db.Audit.CountAsync(a => a.UserId == owner && a.Time >= today && a.Outcome == "started" && a.Action.StartsWith("tool."), ct),
            version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown", transport = "MCP Streamable HTTP / agent WebSocket TLS" };
    }
}
