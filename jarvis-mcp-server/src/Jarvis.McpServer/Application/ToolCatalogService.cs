using System.Text.RegularExpressions;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.McpServer.Application;

/// <summary>
/// Administrator metadata/policy for agent tools. Live selected-device capabilities remain the executable source of truth.
/// </summary>
public sealed partial class ToolCatalogService(
    AppDbContext db,
    IAuditWriter audit,
    McpToolCatalogChangeHub changeHub,
    ToolCatalogReconciler reconciler)
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public async Task<List<ToolEntry>> ListAsync(CancellationToken ct)
    {
        await ReconcileAsync(ct);
        return await db.Tools.AsNoTracking().OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ToolDescriptor>> InstalledAsync(CancellationToken ct) =>
        AgentToolCatalogRules.Installed(
            await db.Devices.AsNoTracking().Select(d => d.CapabilitiesJson).ToListAsync(ct));

    public async Task<ToolCatalogReconcileResult> ReconcileAsync(CancellationToken ct)
    {
        var result = await reconciler.ReconcileAsync(ct);
        if (result.Changed) await changeHub.NotifyAllAsync(ct);
        return result;
    }

    public async Task<ToolEntry> SaveAsync(
        string actor,
        string? id,
        string name,
        string agentToolId,
        string description,
        ToolPublicationMode publicationMode,
        string? revision,
        CancellationToken ct)
    {
        if (ToolPublicationRules.IsReservedPublicName(name))
            throw new ArgumentException("This MCP tool name is reserved by the Jarvis gateway.");
        if (!NamePattern().IsMatch(name))
            throw new ArgumentException("Tool name must be 1..64 letters, digits, underscores or hyphens.");

        var installed = (await InstalledAsync(ct)).SingleOrDefault(t => t.Id == agentToolId)
            ?? throw new ArgumentException("Connect an agent providing this capability before registering it.");
        if (description.Length is < 1 or > 8000)
            throw new ArgumentException("Description must be 1..8000 characters.");

        var entry = id is null
            ? new ToolEntry()
            : await db.Tools.SingleOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new KeyNotFoundException("Tool not found.");
        if (id is not null && entry.Revision != revision)
            throw new InvalidOperationException("Tool was changed. Refresh before saving.");
        if (await db.Tools.AnyAsync(t => t.Name == name && t.Id != entry.Id, ct))
            throw new InvalidOperationException("Tool name already exists.");
        if (await db.Tools.AnyAsync(t => t.AgentToolId == agentToolId && t.Id != entry.Id, ct))
            throw new InvalidOperationException("This installed capability already has a publication policy.");

        entry.Name = name;
        entry.AgentToolId = agentToolId;
        entry.Description = description;
        entry.Category = installed.Category;
        entry.PublicationMode = publicationMode;
        entry.Enabled = ToolPublicationRules.IsVisible(publicationMode);
        entry.Revision = Guid.NewGuid().ToString("N");
        if (id is null) db.Tools.Add(entry);

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new()
        {
            UserId = actor,
            Action = "catalog.save",
            Outcome = entry.Name + ":" + publicationMode
        }, ct);
        await changeHub.NotifyAllAsync(ct);
        return entry;
    }

    /// <summary>All selected policy changes and their audit events commit together, or none do.</summary>
    public async Task<BulkPublicationResult> SetPublicationModeAsync(
        string actor,
        IReadOnlyDictionary<string, string> selected,
        ToolPublicationMode publicationMode,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var ids = selected.Keys.ToArray();
        var entries = await db.Tools.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        if (entries.Count != selected.Count)
            throw new KeyNotFoundException("A selected tool no longer exists. Refresh the catalog.");
        if (entries.Any(t => t.Revision != selected[t.Id]))
            throw new InvalidOperationException("A selected tool changed. Refresh the catalog before applying the selection again.");

        if (publicationMode != ToolPublicationMode.Hidden)
        {
            var installed = (await InstalledAsync(ct)).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            if (entries.Any(t => !installed.Contains(t.AgentToolId)))
                throw new ArgumentException("A selected capability is no longer installed. Reconnect its agent and refresh.");
        }

        var changes = entries.Where(t => t.PublicationMode != publicationMode).ToArray();
        var correlation = Guid.NewGuid().ToString("N");
        var action = publicationMode switch
        {
            ToolPublicationMode.Auto => "catalog.bulk-auto",
            ToolPublicationMode.Published => "catalog.bulk-publish",
            ToolPublicationMode.Hidden => "catalog.bulk-hide",
            _ => throw new ArgumentOutOfRangeException(nameof(publicationMode))
        };
        foreach (var entry in changes)
        {
            entry.PublicationMode = publicationMode;
            entry.Enabled = ToolPublicationRules.IsVisible(publicationMode);
            entry.Revision = Guid.NewGuid().ToString("N");
            db.Audit.Add(new AuditEvent
            {
                UserId = actor,
                Action = action,
                Outcome = entry.Name,
                CorrelationId = correlation
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (changes.Length > 0) await changeHub.NotifyAllAsync(ct);
        return new BulkPublicationResult(selected.Count, changes.Length, publicationMode);
    }

    public sealed record BulkPublicationResult(
        int Selected,
        int Updated,
        ToolPublicationMode PublicationMode);

    /// <summary>
    /// Synchronizes the persisted policy mirror with all enrolled manifests. New rows use Auto;
    /// duplicate, retired and no-longer-advertised rows are removed.
    /// </summary>
    public async Task<ToolCatalogReconcileResult> ImportAsync(string actor, CancellationToken ct)
    {
        var result = await ReconcileAsync(ct);
        await audit.WriteAsync(new()
        {
            UserId = actor,
            Action = "catalog.import",
            Outcome = $"{result.Added} auto tools imported; {result.Removed} stale tools removed"
        }, ct);
        return result;
    }

    public async Task DeleteAsync(string actor, string id, CancellationToken ct)
    {
        var tool = await db.Tools.SingleOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new KeyNotFoundException("Tool not found.");
        db.Tools.Remove(tool);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new()
        {
            UserId = actor,
            Action = "catalog.delete",
            Outcome = tool.Name
        }, ct);
        await changeHub.NotifyAllAsync(ct);
    }
}
