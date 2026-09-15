namespace JarvisCode.Core.Sessions;

/// <summary>Repository for persisted conversations.</summary>
public interface ISessionStore
{
    Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<Session?> LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SaveAsync(Session session, CancellationToken cancellationToken = default);

    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
}
