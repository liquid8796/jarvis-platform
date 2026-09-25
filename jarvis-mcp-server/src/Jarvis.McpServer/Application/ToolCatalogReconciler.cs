using System.Text.RegularExpressions;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.McpServer.Application;

/// <summary>
/// Mirrors newly advertised agent capabilities into the admin catalog as Auto policy metadata.
/// Runtime visibility never depends on this mirror succeeding.
/// </summary>
public sealed class ToolCatalogReconciler(
    IDbContextFactory<AppDbContext> contexts,
    ILogger<ToolCatalogReconciler> logger)
{
    private static readonly Regex PublicName = new("^[A-Za-z0-9_-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task ReconcileAsync(IReadOnlyList<ToolDescriptor> descriptors, CancellationToken ct)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var entries = await db.Tools.OrderBy(t => t.Id).ToListAsync(ct);
            var firstByTool = entries.GroupBy(t => t.AgentToolId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var names = entries.ToDictionary(t => t.Name, t => t.Id, StringComparer.Ordinal);
            var changed = false;

            foreach (var descriptor in descriptors)
            {
                if (firstByTool.TryGetValue(descriptor.Id, out var existing))
                {
                    if (existing.PublicationMode == ToolPublicationMode.Auto)
                    {
                        var entryChanged = false;
                        if (!existing.Enabled)
                        {
                            existing.Enabled = true;
                            entryChanged = true;
                        }
                        if (!StringComparer.Ordinal.Equals(existing.Description, descriptor.Description))
                        {
                            existing.Description = descriptor.Description;
                            entryChanged = true;
                        }
                        if (!StringComparer.Ordinal.Equals(existing.Category, descriptor.Category))
                        {
                            existing.Category = descriptor.Category;
                            entryChanged = true;
                        }
                        if (!StringComparer.Ordinal.Equals(existing.Name, descriptor.Name) &&
                            PublicName.IsMatch(descriptor.Name) &&
                            !ToolPublicationRules.IsReservedPublicName(descriptor.Name) &&
                            (!names.TryGetValue(descriptor.Name, out var holder) || holder == existing.Id))
                        {
                            names.Remove(existing.Name);
                            names[descriptor.Name] = existing.Id;
                            existing.Name = descriptor.Name;
                            entryChanged = true;
                        }
                        if (entryChanged)
                        {
                            existing.Revision = Guid.NewGuid().ToString("N");
                            changed = true;
                        }
                    }
                    continue;
                }

                if (!PublicName.IsMatch(descriptor.Name) ||
                    ToolPublicationRules.IsReservedPublicName(descriptor.Name) ||
                    names.ContainsKey(descriptor.Name))
                    continue;

                var entry = new ToolEntry
                {
                    Name = descriptor.Name,
                    AgentToolId = descriptor.Id,
                    Description = descriptor.Description,
                    Category = descriptor.Category,
                    PublicationMode = ToolPublicationMode.Auto,
                    Enabled = true
                };
                db.Tools.Add(entry);
                entries.Add(entry);
                firstByTool[descriptor.Id] = entry;
                names[entry.Name] = entry.Id;
                changed = true;
            }

            if (changed) await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // The live device manifest remains authoritative even if an admin edited the mirror concurrently.
            logger.LogInformation("Tool catalog metadata reconciliation deferred: {Reason}", ex.GetType().Name);
        }
    }
}
