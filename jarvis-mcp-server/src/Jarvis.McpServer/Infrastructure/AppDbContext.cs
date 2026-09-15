using Jarvis.McpServer.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
namespace Jarvis.McpServer.Infrastructure;
public sealed class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "user";
    public string Status { get; set; } = "pending";
}
public sealed class SchemaInfo { public int Id { get; set; } public int Version { get; set; } }
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<ToolEntry> Tools => Set<ToolEntry>();
    public DbSet<AuditEvent> Audit => Set<AuditEvent>();
    public DbSet<SchemaInfo> Schema => Set<SchemaInfo>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<Device>().HasKey(x => x.Id);
        model.Entity<Device>().HasIndex(x => x.OwnerId);
        model.Entity<Device>().HasIndex(x => x.TokenHash).IsUnique();
        model.Entity<Device>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<Device>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ToolEntry>().HasKey(x => x.Id);
        model.Entity<ToolEntry>().HasIndex(x => x.Name).IsUnique();
        model.Entity<ToolEntry>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<AuditEvent>().HasKey(x => x.Id);
        model.Entity<AuditEvent>().HasIndex(x => new { x.UserId, x.Time });
        model.Entity<SchemaInfo>().HasKey(x => x.Id);
    }
}
