using System.Text.Json;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.McpServer.Application;

public sealed record ResolvedDeviceTool(
    ToolDescriptor Descriptor,
    string PublicName,
    string Description,
    ToolPublicationMode PublicationMode)
{
    public bool Visible => ToolPublicationRules.IsVisible(PublicationMode);
    public ToolDescriptor PublicDescriptor => Descriptor with { Name = PublicName, Description = Description };
}

/// <summary>
/// Resolves the selected device's live capabilities against optional administrator publication policy.
/// Device capabilities are the source of truth; absence of a policy row means Auto, never disabled.
/// </summary>
public sealed class DeviceToolCatalog(AppDbContext db)
{
    public async Task<IReadOnlyList<ResolvedDeviceTool>> ResolveAsync(
        string ownerId, string deviceId, CancellationToken ct)
    {
        var json = await db.Devices.AsNoTracking()
            .Where(d => d.OwnerId == ownerId && d.Id == deviceId)
            .Select(d => d.CapabilitiesJson)
            .SingleAsync(ct);
        var descriptors = (JsonSerializer.Deserialize<ToolDescriptor[]>(json, WireJson.Options) ?? [])
            .DistinctBy(t => t.Id, StringComparer.Ordinal)
            .ToArray();
        var entries = await db.Tools.AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct);
        var policies = entries
            .GroupBy(t => t.AgentToolId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return descriptors.Select(descriptor =>
        {
            policies.TryGetValue(descriptor.Id, out var policy);
            var mode = policy?.PublicationMode ?? ToolPublicationMode.Auto;
            var useLiveMetadata = policy is null || mode == ToolPublicationMode.Auto;
            return new ResolvedDeviceTool(
                descriptor,
                useLiveMetadata ? descriptor.Name : policy!.Name,
                useLiveMetadata ? descriptor.Description : policy!.Description,
                mode);
        }).ToArray();
    }
}
