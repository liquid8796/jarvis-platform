using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.McpServer.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
namespace Jarvis.McpServer.Transport;
[ApiController, Route("api/auth")]
public sealed class AccountController(UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db,
    JarvisOptions options, IAntiforgery antiforgery, IAgentRouter router, IAuditWriter audit) : ControllerBase
{
    [HttpGet("csrf")] public object Csrf() => new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken };
    [HttpGet("session")]
    public async Task<object> Session(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true) return new { user = (object?)null, registrationEnabled = options.AllowRegistration };
        var user = await CurrentAccess.RequireAsync(User, db, false, ct);
        return new { user = View(user), registrationEnabled = options.AllowRegistration, mcpEndpoint = options.Resource };
    }
    [HttpPost("register"), EnableRateLimiting("registration")]
    public async Task<object> Register(RegisterRequest request, CancellationToken ct)
    {
        if (!options.AllowRegistration) throw new UnauthorizedAccessException("Registration is disabled.");
        var user = new AppUser { UserName = request.Email, Email = request.Email, DisplayName = request.DisplayName,
            Status = options.AutoApproveRegistration ? "active" : "pending", Role = "user" };
        Check(await users.CreateAsync(user, request.Password));
        await audit.WriteAsync(new() { UserId = user.Id, Action = "account.register", Outcome = user.Status }, ct);
        return new { message = user.Status == "active" ? "Account created. You can sign in." : "Account created. An administrator must approve it before you can sign in." };
    }
    [HttpPost("login"), EnableRateLimiting("login")]
    public async Task<object> Login(LoginRequest request, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email);
        var valid = user is null ? false : (await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true)).Succeeded;
        if (!valid || user?.Status != "active") throw new UnauthorizedAccessException("Invalid credentials, locked or unapproved account.");
        await signIn.SignInAsync(user, isPersistent: false);
        await audit.WriteAsync(new() { UserId = user.Id, Action = "account.login", Outcome = "completed" }, ct);
        return new { user = View(user) };
    }
    [HttpPost("logout")]
    public async Task<IActionResult> Logout() { await signIn.SignOutAsync(); return NoContent(); }
    [Authorize, HttpPost("revoke")]
    public async Task<IActionResult> Revoke(CancellationToken ct)
    {
        var user = await users.FindByIdAsync(CurrentAccess.UserId(User)) ?? throw new UnauthorizedAccessException();
        Check(await users.UpdateSecurityStampAsync(user)); router.DisconnectUser(user.Id);
        await signIn.SignOutAsync();
        await audit.WriteAsync(new() { UserId = user.Id, Action = "account.revoke-grants", Outcome = "completed" }, ct);
        return NoContent();
    }
    internal static UserView View(AppUser user) => new(user.Id, user.Email ?? "", user.DisplayName, user.Role, user.Status);
    internal static void Check(IdentityResult result)
    { if (!result.Succeeded) throw new ArgumentException(string.Join(" ", result.Errors.Select(e => e.Description))); }
}
