using System.Text.RegularExpressions;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.McpServer.Application;

/// <summary>
/// Reconciles the admin catalog against the union of all persisted Agent manifests.
/// New capabilities receive Auto metadata; duplicate, retired and no-longer-advertised rows are removed.
/// Runtime visibility never depends on this mirror succeeding.
/// </summary>
public sealed class ToolCatalogReconciler(
    IDbContextFactory<AppDbContext> contexts,
    ILogger<ToolCatalogReconciler> logger)
{
    private static readonly Regex PublicName = new("^[A-Za-z0-9_-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly SemaphoreSlim _serial = new(1, 1);

    public async Task<ToolCatalogReconcileResult> ReconcileAsync(CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var manifests = await db.Devices.AsNoTracking()
                .OrderByDescending(device => device.LastSeenAt)
                .ThenBy(device => device.Id)
                .Select(device => device.CapabilitiesJson)
                .ToListAsync(ct);
            var descriptors = AgentToolCatalogRules.Installed(manifests);
            var installedIds = descriptors.Select(tool => tool.Id).ToHashSet(StringComparer.Ordinal);
            var entries = await db.Tools.OrderBy(entry => entry.Id).ToListAsync(ct);

            var duplicateRows = entries.GroupBy(entry => entry.AgentToolId, StringComparer.Ordinal)
                .SelectMany(group => group
                    .OrderByDescending(entry => entry.PublicationMode switch
                    {
                        ToolPublicationMode.Hidden => 2,
                        ToolPublicationMode.Published => 1,
                        _ => 0
                    })
                    .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                    .Skip(1));
            var staleRows = entries.Where(entry =>
                    AgentToolCatalogRules.IsRetired(entry.AgentToolId) || !installedIds.Contains(entry.AgentToolId))
                .Concat(duplicateRows)
                .DistinctBy(entry => entry.Id, StringComparer.Ordinal)
                .ToArray();
            if (staleRows.Length > 0)
            {
                db.Tools.RemoveRange(staleRows);
                var staleIds = staleRows.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
                entries.RemoveAll(entry => staleIds.Contains(entry.Id));
            }

            var firstByTool = entries.GroupBy(entry => entry.AgentToolId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var names = entries.ToDictionary(entry => entry.Name, entry => entry.Id, StringComparer.Ordinal);
            var added = 0;
            var updated = 0;

            foreach (var descriptor in descriptors)
            {
                if (firstByTool.TryGetValue(descriptor.Id, out var existing))
                {
                    var entryChanged = false;
                    if (existing.PublicationMode == ToolPublicationMode.Auto)
                    {
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
                    }

                    // Published/Hidden rows normally preserve administrator metadata. The six Unity bridge
                    // rows predate category-prefixed public names, though, so migrate only their exact former
                    // defaults. A custom administrator alias is deliberately left untouched.
                    var adoptsLiveName = existing.PublicationMode == ToolPublicationMode.Auto ||
                        IsLegacyUnityDefault(existing, descriptor);
                    if (adoptsLiveName &&
                        !StringComparer.Ordinal.Equals(existing.Name, descriptor.Name) &&
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
                        existing.Enabled = existing.PublicationMode != ToolPublicationMode.Hidden;
                        updated++;
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
                added++;
            }

            if (added > 0 || updated > 0 || staleRows.Length > 0)
                await db.SaveChangesAsync(ct);

            if (added > 0 || updated > 0 || staleRows.Length > 0)
                logger.LogInformation(
                    "Tool catalog reconciled added={Added} updated={Updated} removed={Removed}",
                    added, updated, staleRows.Length);
            return new(added, updated, staleRows.Length);
        }
        catch (DbUpdateException ex)
        {
            // Live manifests remain authoritative if an administrator edits a policy concurrently.
            logger.LogWarning("Tool catalog metadata reconciliation deferred: {Reason}", ex.GetType().Name);
            return new(0, 0, 0);
        }
        finally { _serial.Release(); }
    }

    private static bool IsLegacyUnityDefault(ToolEntry entry, ToolDescriptor descriptor) =>
        AgentToolCatalogRules.IsLegacyDefaultPublicName(entry.AgentToolId, entry.Name) &&
        AgentToolCatalogRules.TryGetPreferredPublicName(entry.AgentToolId, out var preferredName) &&
        StringComparer.Ordinal.Equals(descriptor.Name, preferredName);
}

public sealed record ToolCatalogReconcileResult(int Added, int Updated, int Removed)
{
    public bool Changed => Added > 0 || Updated > 0 || Removed > 0;
}
