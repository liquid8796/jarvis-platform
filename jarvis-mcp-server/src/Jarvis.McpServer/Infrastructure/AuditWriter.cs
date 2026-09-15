using Jarvis.McpServer.Domain;
using Microsoft.EntityFrameworkCore;
namespace Jarvis.McpServer.Infrastructure;
/// <summary>Audit metadata only. Never persist arguments, bearer tokens, clipboard contents or screenshots.</summary>
public sealed class AuditWriter(IDbContextFactory<AppDbContext> contexts, ILogger<AuditWriter> logger) : IAuditWriter
{
    public async Task WriteAsync(AuditEvent entry, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.Audit.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Jarvis action={Action} user={UserId} device={DeviceId} outcome={Outcome} call={CallId} durationMs={Duration}",
            entry.Action, entry.UserId, entry.DeviceId, entry.Outcome, entry.CorrelationId, entry.DurationMs);
    }
}
