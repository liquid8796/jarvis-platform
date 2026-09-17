using System.Collections.Concurrent;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Execution;

/// <summary>Atomic local settings, separate from enrollment credentials. Corrupt state fails closed.</summary>
public sealed class ExecutionSettingsStore
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _path;
    private readonly object _sync;
    public ExecutionSettingsStore(string path)
    {
        _path = Path.GetFullPath(path);
        _sync = Locks.GetOrAdd(_path, _ => new object());
    }

    public AgentExecutionSettings Load()
    {
        lock (_sync) return LoadCore();
    }

    public AgentExecutionSettings Save(AgentExecutionSettings draft, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.Validate();
        if (expectedRevision < 1) throw new ArgumentException("Expected settings revision must be positive.");
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var fileLock = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = LoadCore();
            if (current.Revision != expectedRevision)
                throw new AgentRequestException("SETTINGS_REVISION_CONFLICT", "Settings changed elsewhere. Reload before saving your draft.");
            var saved = draft with { Revision = checked(current.Revision + 1) };
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, saved, WireJson.Options);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temp, _path, overwrite: true);
                return saved;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private AgentExecutionSettings LoadCore()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            if (new FileInfo(_path).Length > 16384) throw new InvalidDataException("Execution settings exceed 16 KiB.");
            var settings = JsonSerializer.Deserialize<AgentExecutionSettings>(File.ReadAllBytes(_path), WireJson.Options)
                ?? throw new JsonException("Execution settings are empty.");
            settings.Validate();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Execution settings are invalid. The existing file was not overwritten; restore or repair it before saving.", ex);
        }
    }
}
