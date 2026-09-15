using System.Collections.Concurrent;
using System.Text.Json;

namespace JarvisCode.Core.Routines;

/// <summary>Persists routines as one JSON file in app data (atomic temp+move writes).</summary>
public sealed class RoutineStore
{
    private sealed class State { public object Gate { get; } = new(); public List<Routine> Ephemeral { get; set; } = []; }
    private static readonly ConcurrentDictionary<string, State> States = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string filePath;
    private readonly State state;
    public RoutineStore(string path)
    {
        filePath = Path.GetFullPath(path);
        state = States.GetOrAdd(filePath, _ => new State());
    }
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public List<Routine> Load()
    {
        lock (state.Gate) return LoadLocked();
    }

    private List<Routine> LoadLocked()
    {
        List<Routine> durable;
        try
        {
            durable = File.Exists(filePath)
                ? JsonSerializer.Deserialize<List<Routine>>(File.ReadAllText(filePath), SerializerOptions) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt file must not take routine management down with it.
            durable = [];
        }
        durable.RemoveAll(r => r.OwnerSessionId is not null);
        durable.AddRange(state.Ephemeral);
        return durable;
    }

    public void Save(IReadOnlyList<Routine> routines)
    {
        lock (state.Gate) SaveLocked(routines);
    }

    public void Update(Action<List<Routine>> change)
    {
        lock (state.Gate)
        {
            var rows = LoadLocked();
            change(rows);
            SaveLocked(rows);
        }
    }

    public void RemoveOwnedBy(string sessionId) => Update(rows => rows.RemoveAll(r => r.OwnerSessionId == sessionId));

    private void SaveLocked(IReadOnlyList<Routine> routines)
    {
        state.Ephemeral = routines.Where(r => r.OwnerSessionId is not null).ToList();
        var durable = routines.Where(r => r.OwnerSessionId is null).ToList();
        if (durable.Count == 0 && !File.Exists(filePath)) return;
        var json = JsonSerializer.Serialize(durable, SerializerOptions);
        if (File.Exists(filePath) && File.ReadAllText(filePath) == json) return;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, filePath, overwrite: true);
    }
}
