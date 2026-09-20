using System.Collections.Concurrent;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Prompts;

/// <summary>Atomic versioned local storage. Missing files seed disabled examples; corrupt files are never reset.</summary>
public sealed class PromptInjectionStore
{
    private const int MaxFileBytes = 512 * 1024;
    private static readonly ConcurrentDictionary<string, object> Locks = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _path;
    private readonly object _sync;

    public PromptInjectionStore(string path)
    {
        _path = Path.GetFullPath(path);
        _sync = Locks.GetOrAdd(_path, _ => new object());
    }

    public PromptInjectionSettings Load()
    {
        lock (_sync) return LoadCore();
    }

    public PromptInjectionSettings Save(PromptInjectionSettings draft, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.Validate();
        if (expectedRevision < 1) throw new ArgumentException("Expected prompt revision must be positive.");
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var fileLock = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = LoadCore();
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException("Prompts changed elsewhere. Reload before saving your draft.");
            var saved = draft with { Revision = checked(current.Revision + 1), Entries = Array.AsReadOnly(draft.Entries.ToArray()) };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, WireJson.Options);
            if (bytes.Length > MaxFileBytes) throw new ArgumentException("Prompt settings exceed 512 KiB.");
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(bytes);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temp, _path, overwrite: true);
                return saved;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private PromptInjectionSettings LoadCore()
    {
        if (!File.Exists(_path)) return DefaultPromptPresets.Create();
        try
        {
            using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaxFileBytes) throw new InvalidDataException("Prompt settings exceed 512 KiB.");
            var settings = JsonSerializer.Deserialize<PromptInjectionSettings>(input, WireJson.Options)
                ?? throw new JsonException("Prompt settings are empty.");
            settings.Validate();
            return settings with { Entries = Array.AsReadOnly(settings.Entries.ToArray()) };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Prompt settings are invalid. The existing file was preserved; repair or restore it before saving.", ex);
        }
    }
}
