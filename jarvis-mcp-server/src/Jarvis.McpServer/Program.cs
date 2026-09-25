using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Jarvis.McpServer.Application;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.McpServer.Security;
using Jarvis.McpServer.Transport;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using OpenIddict.Validation.AspNetCore;

var releaseVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
var builder = WebApplication.CreateBuilder(args);
if (Environment.GetEnvironmentVariable("JARVIS_CONFIG_PATH") is { Length: > 0 } privateConfig)
    builder.Configuration.AddJsonFile(Path.GetFullPath(privateConfig), optional: false, reloadOnChange: false).AddEnvironmentVariables();
var settings = builder.Configuration.GetSection("Jarvis").Get<JarvisOptions>() ?? new();
settings.Validate(builder.Environment.IsDevelopment());
var data = Path.GetFullPath(settings.DataDirectory);
Directory.CreateDirectory(data);
builder.Services.AddSingleton(settings);
builder.Services.AddDbContextFactory<AppDbContext>(o => { o.UseSqlite("Data Source=" + Path.Combine(data, "jarvis.db")); o.UseOpenIddict(); });
builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
{
    o.Password.RequiredLength = 14; o.User.RequireUniqueEmail = true;
    o.Lockout.MaxFailedAccessAttempts = 5; o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    o.SignIn.RequireConfirmedEmail = false; // Registration is pending admin approval by default; no pretend email delivery.
}).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "jarvis.session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(2); o.SlidingExpiration = false;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddDataProtection().SetApplicationName("jarvis-mcp-server")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "data-protection")));
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "jarvis.csrf";
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always; });
builder.Services.AddOpenIddict()
    .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
    .AddServer(o =>
    {
        o.SetIssuer(settings.Issuer);
        o.AddEventHandler<HandleConfigurationRequestContext>(handler => handler
            .UseSingletonHandler<OAuthDiscoveryHandler>()
            .SetOrder(Math.Max(
                OpenIddictServerHandlers.Discovery.AttachScopes.Descriptor.Order,
                OpenIddictServerHandlers.Discovery.AttachClientAuthenticationMethods.Descriptor.Order) + 1_000));
        o.SetAuthorizationEndpointUris("connect/authorize").SetTokenEndpointUris("connect/token")
            .SetRevocationEndpointUris("connect/revoke");
        o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
        o.Configure(configuration => { configuration.CodeChallengeMethods.Clear(); configuration.CodeChallengeMethods.Add(OpenIddictConstants.CodeChallengeMethods.Sha256); });
        o.RegisterScopes(CurrentAccess.Scope);
        o.Configure(configuration => configuration.Resources.Add(new Uri(settings.Resource)));
        o.UseReferenceAccessTokens().UseReferenceRefreshTokens();
        o.SetAccessTokenLifetime(TimeSpan.FromMinutes(10)); o.SetRefreshTokenLifetime(TimeSpan.FromDays(7));
        if (builder.Environment.IsDevelopment()) o.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
        else
        {
            o.AddEncryptionCertificate(DatabaseBootstrap.LoadCertificate(builder.Configuration, "Encryption"));
            o.AddSigningCertificate(DatabaseBootstrap.LoadCertificate(builder.Configuration, "Signing"));
        }
        var integration = o.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
        if (builder.Environment.IsDevelopment()) integration.DisableTransportSecurityRequirement();
    })
    .AddValidation(o => { o.UseLocalServer(); o.UseAspNetCore(); o.AddAudiences(settings.Resource); o.EnableTokenEntryValidation(); });
builder.Services.AddAuthorization(o => o.AddPolicy("mcp", p =>
    p.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).RequireAuthenticatedUser()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddControllersWithViews().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddSingleton<McpToolCatalogChangeHub>();
builder.Services.AddSingleton<ToolCatalogReconciler>();
builder.Services.AddScoped<DeviceToolCatalog>();
builder.Services.AddSingleton<WsAgentRouter>();
builder.Services.AddSingleton<IAgentRouter>(s => s.GetRequiredService<WsAgentRouter>());
builder.Services.AddSingleton<IAgentTaskRouter>(s => s.GetRequiredService<WsAgentRouter>());
builder.Services.AddScoped<AgentTaskService>();
builder.Services.AddSingleton<McpSessionContext>();
builder.Services.AddSingleton<IAuditWriter, AuditWriter>();
builder.Services.AddScoped<DeviceService>(); builder.Services.AddScoped<ToolCatalogService>(); builder.Services.AddScoped<McpGateway>();
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "jarvis-mcp-server", Version = releaseVersion };
        o.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } };
    })
    .WithHttpTransport(o =>
    {
        o.SessionMode = ModelContextProtocol.AspNetCore.HttpServerSessionMode.StatefulForInitializeClients;
#pragma warning disable MCPEXP002 // Required to retain initialize-client sessions for tools/list_changed notifications.
        o.RunSessionHandler = async (http, server, ct) =>
            await http.RequestServices.GetRequiredService<McpToolCatalogChangeHub>().RunSessionAsync(http, server, ct);
#pragma warning restore MCPEXP002
    })
    .WithListToolsHandler(async (context, ct) => await context.Services!.GetRequiredService<McpGateway>().ListAsync(ct))
    .WithCallToolHandler(async (context, ct) => await context.Services!.GetRequiredService<McpGateway>().CallAsync(context.Params!, ct));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1; // Keep default trusted loopback proxies; never clear the trust lists.
});
builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 1024 * 1024; o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15); });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(
        c.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("registration", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
await DatabaseBootstrap.InitializeAsync(app.Services, app.Configuration);
await app.Services.GetRequiredService<ToolCatalogReconciler>().ReconcileAsync(CancellationToken.None);
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.Use(async (http, next) =>
{
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    http.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    http.Response.Headers.ContentSecurityPolicy = BrowserContentSecurityPolicy.Default;
    if (http.Request.Path.StartsWithSegments("/mcp"))
        http.Response.OnStarting(() =>
        {
            if (http.Response.StatusCode == 401) http.Response.Headers.WWWAuthenticate = "Bearer resource_metadata=\"" + settings.PublicOrigin + "/.well-known/oauth-protected-resource\"";
            return Task.CompletedTask;
        });
    if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/connect")) http.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (Exception ex) when (!http.Response.HasStarted && ex is ArgumentException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException or DbUpdateException)
    {
        http.Response.StatusCode = ex switch { UnauthorizedAccessException => 403, KeyNotFoundException => 404,
            DbUpdateConcurrencyException => 409, DbUpdateException => 409, InvalidOperationException => 409, _ => 400 };
        await http.Response.WriteAsJsonAsync(new { error = ex is DbUpdateException ? "Conflict. Refresh and retry." : ex.Message });
    }
});
app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.Use(async (http, next) =>
{
    if (http.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
    {
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { http.Response.StatusCode = 400; await http.Response.WriteAsJsonAsync(new { error = "Invalid CSRF token. Reload the page." }); return; }
    }
    if (http.Request.Path.StartsWithSegments("/mcp") && http.Request.Headers.TryGetValue("Origin", out var origin) && origin != settings.PublicOrigin)
    { http.Response.StatusCode = 403; return; }
    await next();
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = releaseVersion }));
object Metadata() => new { resource = settings.Resource, authorization_servers = new[] { settings.PublicOrigin }, scopes_supported = new[] { CurrentAccess.Scope }, bearer_methods_supported = new[] { "header" } };
app.MapGet("/.well-known/oauth-protected-resource", Metadata);
app.MapGet("/.well-known/oauth-protected-resource/mcp", Metadata);
app.Map("/agent/connect", (HttpContext http, WsAgentRouter router) => router.AcceptAsync(http));
app.MapControllers();
app.MapMcp("/mcp").RequireAuthorization("mcp");
app.Run();
public partial class Program { }

