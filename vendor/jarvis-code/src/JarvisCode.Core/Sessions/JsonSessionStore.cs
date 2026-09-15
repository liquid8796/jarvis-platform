using System.Text.Json;

namespace JarvisCode.Core.Sessions;

/// <summary>Stores each session as one JSON file under the app data directory.</summary>
public sealed class JsonSessionStore(string directory) : ISessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Where these sessions are kept, for anything stored alongside them.</summary>
    public string DirectoryPath => directory;

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
            return [];

        var summaries = new List<SessionSummary>();
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await TryReadAsync(file, cancellationToken);
            if (session is not null)
                summaries.Add(new SessionSummary(
                    session.Id, session.Title, session.UpdatedAt, session.Messages.Count, session.WorkingDirectory,
                    session.RoutineId));
        }
        return [.. summaries.OrderByDescending(s => s.UpdatedAt)];
    }

    public async Task<Session?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(sessionId);
        return File.Exists(path) ? await TryReadAsync(path, cancellationToken) : null;
    }

    public async Task SaveAsync(Session session, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(session, SerializerOptions);
        // Shell outputs leak env dumps; stored sessions must be safe to share. The
        // redaction patterns cannot match inside base64, so images survive intact.
        json = Security.SecretRedactor.Redact(json);
        var path = PathFor(session.Id);
        // Atomic write: a crash mid-write must never leave a truncated session file.
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(sessionId);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(string sessionId)
    {
        if (sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Invalid session id: {sessionId}", nameof(sessionId));
        return Path.Combine(directory, sessionId + ".json");
    }

    private static async Task<Session?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<Session>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt or locked session file should not take down the whole list.
            return null;
        }
    }
}
