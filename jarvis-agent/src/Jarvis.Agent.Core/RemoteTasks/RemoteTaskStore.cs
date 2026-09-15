using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

internal sealed record StoredRemoteTask(int Version, string OwnerId, string CreateDigest,
    string? PlanDigest, RemoteTaskPlan Plan, RemoteTaskSnapshot Snapshot, IReadOnlyList<RemoteTaskArtifact> Artifacts);

/// <summary>Atomic, bounded local snapshots. No task data is stored in the server database.</summary>
internal sealed class RemoteTaskStore : IDisposable
{
    private readonly string _root;
    private readonly FileStream _lease;
    public RemoteTaskStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _lease = new FileStream(Path.Combine(_root, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
            {
                var task = Read(path);
                if (task.Snapshot.Status is "QUEUED" or "RUNNING" or "CANCELLING")
                    Save(task with { Snapshot = task.Snapshot with { Status = "INTERRUPTED", UpdatedAt = DateTimeOffset.UtcNow,
                        Error = "Agent restarted; completion may be unknown. No actions were replayed." } });
            }
        }
        catch { _lease.Dispose(); throw; }
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Digest(RemoteTaskPlan plan) => Hash(JsonSerializer.Serialize(plan, WireJson.Options));
    public static string CreateDigest(RemoteTaskPlan plan, string? parentTaskId)
    {
        if (string.IsNullOrWhiteSpace(parentTaskId)) return Digest(plan);
        return Hash(JsonSerializer.Serialize(new { plan, parentTaskId = RemoteTaskRules.TaskId(parentTaskId) }, WireJson.Options));
    }
    private string FilePath(string owner, string id) => Path.Combine(_root, Hash(owner), RemoteTaskRules.TaskId(id) + ".json");
    public StoredRemoteTask? Load(string owner, string id)
    {
        var path = FilePath(owner, id);
        if (!File.Exists(path)) return null;
        var task = Read(path);
        if (task.OwnerId != owner || task.Snapshot.TaskId != RemoteTaskRules.TaskId(id))
            throw new InvalidDataException("Task snapshot identity mismatch.");
        return task;
    }
    public bool AtCapacity => Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories).Take(128).Count() >= 128;
    public int CountChildren(string owner, string parentTaskId)
    {
        var canonical = RemoteTaskRules.TaskId(parentTaskId);
        var directory = Path.Combine(_root, Hash(owner));
        if (!Directory.Exists(directory)) return 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var task = Read(path);
            if (StringComparer.Ordinal.Equals(task.Snapshot.ParentTaskId, canonical)) count++;
        }
        return count;
    }
    public void Save(StoredRemoteTask task)
    {
        var path = FilePath(task.OwnerId, task.Snapshot.TaskId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(task, WireJson.Options);
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Task snapshot exceeds storage limit.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static StoredRemoteTask Read(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Oversized task snapshot.");
        var task = JsonSerializer.Deserialize<StoredRemoteTask>(File.ReadAllBytes(path), WireJson.Options)
            ?? throw new InvalidDataException("Empty task snapshot.");
        if (task.Version != 1 || task.Snapshot is null || task.Artifacts is null || string.IsNullOrEmpty(task.OwnerId))
            throw new InvalidDataException("Unsupported task snapshot.");
        RemoteTaskRules.Validate(task.Plan);
        return task;
    }
    public void Dispose() => _lease.Dispose();
}
