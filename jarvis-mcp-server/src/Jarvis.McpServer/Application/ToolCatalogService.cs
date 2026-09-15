using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
namespace Jarvis.McpServer.Application;
/// <summary>Admin metadata/aliases are separate from executable code. An admin cannot inject new agent handlers over the wire.</summary>
public sealed partial class ToolCatalogService(AppDbContext db, IAuditWriter audit)
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)] private static partial Regex NamePattern();
    public Task<List<ToolEntry>> ListAsync(CancellationToken ct) => db.Tools.AsNoTracking().OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
    public async Task<IReadOnlyList<ToolDescriptor>> InstalledAsync(CancellationToken ct) =>
        (await db.Devices.AsNoTracking().Select(d => d.CapabilitiesJson).ToListAsync(ct))
            .SelectMany(json => JsonSerializer.Deserialize<ToolDescriptor[]>(json, WireJson.Options) ?? [])
            .DistinctBy(t => t.Id).OrderBy(t => t.Category).ThenBy(t => t.Name).ToArray();
    public async Task<ToolEntry> SaveAsync(string actor, string? id, string name, string agentToolId,
        string description, bool enabled, string? revision, CancellationToken ct)
    {
        if (!NamePattern().IsMatch(name)) throw new ArgumentException("Tool name must be 1..64 letters, digits, underscores or hyphens.");
        var installed = (await InstalledAsync(ct)).SingleOrDefault(t => t.Id == agentToolId)
            ?? throw new ArgumentException("Connect an agent providing this capability before registering it.");
        if (description.Length is < 1 or > 8000) throw new ArgumentException("Description must be 1..8000 characters.");
        var entry = id is null ? new ToolEntry() : await db.Tools.SingleOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new KeyNotFoundException("Tool not found.");
        if (id is not null && entry.Revision != revision) throw new InvalidOperationException("Tool was changed. Refresh before saving.");
        if (await db.Tools.AnyAsync(t => t.Name == name && t.Id != entry.Id, ct)) throw new InvalidOperationException("Tool name already exists.");
        entry.Name = name; entry.AgentToolId = agentToolId; entry.Description = description;
        entry.Category = installed.Category; entry.Enabled = enabled; entry.Revision = Guid.NewGuid().ToString("N");
        if (id is null) db.Tools.Add(entry);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new() { UserId = actor, Action = "catalog.save", Outcome = entry.Name }, ct);
        return entry;
    }
    /// <summary>All selected entries and their audit events commit together, or none do.</summary>
    public async Task<BulkAvailabilityResult> SetAvailabilityAsync(string actor,
        IReadOnlyDictionary<string, string> selected, bool enabled, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var ids = selected.Keys.ToArray();
        var entries = await db.Tools.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        if (entries.Count != selected.Count)
            throw new KeyNotFoundException("A selected tool no longer exists. Refresh the catalog.");
        if (entries.Any(t => t.Revision != selected[t.Id]))
            throw new InvalidOperationException("A selected tool changed. Refresh the catalog before applying the selection again.");
        if (enabled)
        {
            var installed = (await InstalledAsync(ct)).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            if (entries.Any(t => !installed.Contains(t.AgentToolId)))
                throw new ArgumentException("A selected capability is no longer installed. Reconnect its agent and refresh.");
        }
        var changes = entries.Where(t => t.Enabled != enabled).ToArray();
        var correlation = Guid.NewGuid().ToString("N");
        foreach (var entry in changes)
        {
            entry.Enabled = enabled;
            entry.Revision = Guid.NewGuid().ToString("N");
            db.Audit.Add(new AuditEvent { UserId = actor,
                Action = enabled ? "catalog.bulk-publish" : "catalog.bulk-disable",
                Outcome = entry.Name, CorrelationId = correlation });
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new BulkAvailabilityResult(selected.Count, changes.Length, enabled);
    }
    public sealed record BulkAvailabilityResult(int Selected, int Updated, bool Enabled);

    public async Task<int> ImportAsync(string actor, CancellationToken ct)
    {
        var installed = await InstalledAsync(ct);
        var existing = (await db.Tools.Select(t => t.AgentToolId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var names = (await db.Tools.Select(t => t.Name).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var count = 0;
        foreach (var tool in installed.Where(t => !existing.Contains(t.Id)))
        {
            if (!NamePattern().IsMatch(tool.Name) || !names.Add(tool.Name)) continue;
            db.Tools.Add(new() { Name = tool.Name, AgentToolId = tool.Id, Description = tool.Description, Category = tool.Category, Enabled = false }); count++;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new() { UserId = actor, Action = "catalog.import", Outcome = count + " disabled tools imported" }, ct);
        return count;
    }
    public async Task DeleteAsync(string actor, string id, CancellationToken ct)
    {
        var tool = await db.Tools.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException("Tool not found.");
        db.Tools.Remove(tool); await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new() { UserId = actor, Action = "catalog.delete", Outcome = tool.Name }, ct);
    }
}
