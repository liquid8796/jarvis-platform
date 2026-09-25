using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
namespace Jarvis.McpServer.Infrastructure;
public static class DatabaseBootstrap
{
    public const int CurrentSchemaVersion = 2;

    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Schema upgrades are reviewed and additive; never delete/recreate a user's database.
        var created = await db.Database.EnsureCreatedAsync();
        if (created) { db.Schema.Add(new SchemaInfo { Id = 1, Version = CurrentSchemaVersion }); await db.SaveChangesAsync(); }
        await MigrateSchemaAsync(db);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (!await db.Users.AnyAsync(x => x.Role == "admin" && x.Status == "active"))
        {
            var email = configuration["Bootstrap:AdminEmail"];
            var password = configuration["Bootstrap:AdminPassword"];
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException("Set Bootstrap__AdminEmail and Bootstrap__AdminPassword for the first start. No default password exists.");
            if (await users.FindByEmailAsync(email) is not null)
                throw new InvalidOperationException("Bootstrap email already belongs to a non-admin account. Resolve locally; automatic promotion is disabled.");
            var admin = new AppUser { UserName = email, Email = email, DisplayName = "Administrator", Role = "admin", Status = "active" };
            var result = await users.CreateAsync(admin, password);
            if (!result.Succeeded) throw new InvalidOperationException("Bootstrap password did not meet the configured Identity policy.");
        }
    }
    public static async Task MigrateSchemaAsync(AppDbContext db)
    {
        var schema = await db.Schema.SingleOrDefaultAsync(x => x.Id == 1)
            ?? throw new InvalidOperationException("Jarvis database schema metadata is missing.");
        if (schema.Version == 1)
        {
            await using var migration = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Tools ADD COLUMN PublicationMode TEXT NOT NULL DEFAULT 'Auto';");
            await db.Database.ExecuteSqlRawAsync("UPDATE Tools SET PublicationMode='Published' WHERE Enabled<>0;");
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE Tools SET PublicationMode='Hidden'
                WHERE Enabled=0 AND EXISTS (
                    SELECT 1 FROM Audit
                    WHERE Audit.Outcome=Tools.Name
                      AND Audit.Action IN ('catalog.bulk-disable','catalog.save')
                );
                """);
            await db.Database.ExecuteSqlRawAsync("UPDATE Tools SET Enabled=CASE WHEN PublicationMode='Hidden' THEN 0 ELSE 1 END;");
            schema.Version = CurrentSchemaVersion;
            await db.SaveChangesAsync();
            await migration.CommitAsync();
        }
        else if (schema.Version != CurrentSchemaVersion)
        {
            throw new InvalidOperationException("Unsupported Jarvis database schema. No automatic destructive upgrade is allowed.");
        }
    }

    public static X509Certificate2 LoadCertificate(IConfiguration config, string name)
    {
        var path = config[$"Certificates:{name}Path"] ?? throw new InvalidOperationException($"Missing {name} certificate path.");
        var password = config[$"Certificates:{name}Password"] ?? throw new InvalidOperationException($"Missing {name} certificate password.");
        return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
    }
}
