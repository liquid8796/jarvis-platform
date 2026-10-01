using Jarvis.McpServer.Domain;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.McpServer.Infrastructure;

/// <summary>
/// Server-side revocation index for stateless protected application-session handles. Deletions are
/// monotonic and authenticated by the enrolled Agent WebSocket; owner/device identity is never read
/// from the deletion payload. The Agent replays all local tombstones after each reconnect.
/// </summary>
public sealed class ApplicationSessionDeletionStore(IDbContextFactory<AppDbContext> contexts)
{
    public async Task ApplyAsync(string ownerId, string deviceId,
        IReadOnlyList<AgentSessionDeletion>? deletions, CancellationToken ct)
    {
        ValidateIdentity(ownerId, deviceId);
        if (deletions is null || deletions.Count is < 1 or > 200 ||
            deletions.Any(d => d is null || !AgentSessionRules.IsSessionId(d.SessionId) || d.DeletedAtUnixMilliseconds <= 0) ||
            deletions.Select(d => d.SessionId).Distinct(StringComparer.Ordinal).Count() != deletions.Count)
            throw new InvalidDataException("Invalid application-session deletion batch.");

        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var deletion in deletions)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ApplicationSessionTombstones (OwnerId, DeviceId, SessionId, DeletedAt)
                VALUES ({ownerId}, {deviceId}, {deletion.SessionId}, {deletion.DeletedAtUnixMilliseconds})
                ON CONFLICT(OwnerId, DeviceId, SessionId)
                DO UPDATE SET DeletedAt=MAX(DeletedAt, excluded.DeletedAt);
                """, ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> IsDeletedAsync(string ownerId, string deviceId, string sessionId, CancellationToken ct)
    {
        ValidateIdentity(ownerId, deviceId);
        if (!AgentSessionRules.IsSessionId(sessionId))
            throw new AgentRequestException("SESSION_INVALID", "A valid application session is required.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ApplicationSessionTombstones.AsNoTracking().AnyAsync(row =>
            row.OwnerId == ownerId && row.DeviceId == deviceId && row.SessionId == sessionId, ct);
    }

    public async Task ThrowIfDeletedAsync(string ownerId, string deviceId, string sessionId, CancellationToken ct)
    {
        if (await IsDeletedAsync(ownerId, deviceId, sessionId, ct))
            throw new AgentRequestException("SESSION_DELETED",
                "This session was permanently deleted in Jarvis Agent and its server handle has been revoked.");
    }

    private static void ValidateIdentity(string ownerId, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 256 ||
            string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 100)
            throw new AgentRequestException("SESSION_INVALID", "Authenticated owner and enrolled device are required.");
    }
}
