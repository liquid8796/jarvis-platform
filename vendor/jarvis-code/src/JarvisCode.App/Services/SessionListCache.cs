using System.IO;
using System.Text.Json;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Services;

/// <summary>
/// Sidebar-list cache over a session store. The engine's ListAsync deserializes
/// every session file on every call, which is fine for dozens of sessions and
/// painful for hundreds of imported CLI transcripts — so the shell lists the
/// directory itself, re-reads only files whose size or write time changed, and
/// keeps the summaries on disk so a fresh launch lists instantly too. Purely
/// App-side: the engine store stays the single writer of session files.
/// </summary>
public sealed class SessionListCache
{
    private sealed record Entry(DateTime WriteTime, long Size, SessionSummary Summary);

    private static readonly JsonSerializerOptions CacheOptions = new() { WriteIndented = false };

    private readonly JsonSessionStore _store;
    private readonly string _directory;
    private readonly string _cacheFile;
    private readonly Dictionary<string, Entry> _cache;

    public SessionListCache(JsonSessionStore store, string directory)
    {
        _store = store;
        _directory = directory;
        _cacheFile = directory.TrimEnd('\\', '/') + ".list-cache.json";
        _cache = LoadCacheFile(_cacheFile);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var summaries = new List<SessionSummary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirty = false;
        foreach (var file in Directory.GetFiles(_directory, "*.json"))
        {
            seen.Add(file);
            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch (IOException)
            {
                continue;
            }

            if (_cache.TryGetValue(file, out var cached) &&
                cached.WriteTime == info.LastWriteTimeUtc && cached.Size == info.Length)
            {
                summaries.Add(cached.Summary);
                continue;
            }

            var session = await _store.LoadAsync(Path.GetFileNameWithoutExtension(file));
            if (session is null)
            {
                continue;
            }

            var summary = new SessionSummary(
                session.Id, session.Title, session.UpdatedAt, session.Messages.Count, session.WorkingDirectory,
                session.RoutineId);
            _cache[file] = new Entry(info.LastWriteTimeUtc, info.Length, summary);
            summaries.Add(summary);
            dirty = true;
        }

        foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _cache.Remove(stale);
            dirty = true;
        }

        if (dirty)
        {
            SaveCacheFile();
        }

        return [.. summaries.OrderByDescending(static s => s.UpdatedAt)];
    }

    private void SaveCacheFile()
    {
        try
        {
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache, CacheOptions));
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is a convenience; listing works without it.
        }
    }

    private static Dictionary<string, Entry> LoadCacheFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), CacheOptions)
                    ?? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    }
}
