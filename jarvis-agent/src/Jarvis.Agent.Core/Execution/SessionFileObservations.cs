using System.Security.Cryptography;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Execution;

/// <summary>Bounded optimistic file observations. Stores hashes, never source text or credentials.</summary>
public sealed class SessionFileObservations
{
    private const int Capacity = 8192;
    private readonly object _sync = new();
    private readonly Dictionary<(AgentSessionIdentity Identity, string Path), FileObservation> _observed = new();
    public sealed record FileObservation(bool Exists, string Hash);

    private static string Key(string path)
    {
        var canonical = ToolExecutionResources.CanonicalPath(path);
        return OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical;
    }
    public static async Task<FileObservation> CaptureAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // Other Jarvis writes are held by the resource coordinator. This denies new Windows writers
            // while hashing; external programs are not claimed to participate in our transaction.
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new(true, Convert.ToHexString(await SHA256.HashDataAsync(input, ct)));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return new(false, ""); }
    }
    public async Task RememberAsync(AgentSessionIdentity identity, string path, CancellationToken ct) =>
        Remember(identity, path, await CaptureAsync(path, ct));
    public void Remember(AgentSessionIdentity identity, string path, FileObservation observation)
    {
        var key = (identity, Key(path));
        lock (_sync)
        {
            _observed.Remove(key);
            if (_observed.Count >= Capacity) _observed.Remove(_observed.Keys.First());
            _observed[key] = observation;
        }
    }
    public async Task ValidateWriteAsync(AgentSessionIdentity identity, string path, CancellationToken ct)
    {
        var key = (identity, Key(path));
        FileObservation? before;
        lock (_sync) _observed.TryGetValue(key, out before);
        var current = await CaptureAsync(path, ct);
        if (before is not null && before != current)
            throw new AgentRequestException("FILE_CHANGED", "This file changed after this session read it. Read its current contents and reconcile before writing; no file tool ran.");
        if (before is null && current.Exists)
            throw new AgentRequestException("FILE_READ_REQUIRED", "Read the existing file in this session before changing it. Another session's read does not authorize a stale overwrite.");
    }
    public void Forget(AgentSessionIdentity identity)
    {
        lock (_sync)
            foreach (var key in _observed.Keys.Where(key => key.Identity == identity).ToArray()) _observed.Remove(key);
    }
    public void Forget(AgentSessionIdentity identity, string path)
    {
        var key = (identity, Key(path));
        lock (_sync) _observed.Remove(key);
    }
}
