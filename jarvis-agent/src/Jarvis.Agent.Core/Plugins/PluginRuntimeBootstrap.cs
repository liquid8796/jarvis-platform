using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Plugins;

public interface IPluginLifecycleHook
{
    string PluginId { get; }
    string HookName { get; }
    Task InvokeAsync(AgentLifecycleEvent evt, CancellationToken cancellationToken);
}

public sealed record PluginPackage(PluginManifest Manifest, byte[] EntryBytes);

/// <summary>
/// Production bootstrap for host-supplied plugin tools/hooks and hash-pinned local manifests.
/// The runtime never loads the pinned entry file as executable code; it is provenance/package
/// material while implementations remain compiled/supplied by the local host.
/// </summary>
public sealed class PluginRuntimeBootstrap : IDisposable
{
    private readonly string _directory;
    private readonly IReadOnlyList<IAgentTool> _implementations;
    private readonly Version _agentVersion;
    private readonly IReadOnlyDictionary<string, IPluginLifecycleHook> _hookImplementations;
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private int _disposed;
    private int _hookFailureCount;
    private string? _lastHookError;

    public PluginRuntimeBootstrap(string directory, IEnumerable<IAgentTool> implementations, Version agentVersion,
        IEnumerable<IPluginLifecycleHook>? lifecycleHooks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(implementations);
        ArgumentNullException.ThrowIfNull(agentVersion);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _implementations = implementations.ToArray();
        _agentVersion = agentVersion;
        var hooks = new Dictionary<string, IPluginLifecycleHook>(StringComparer.Ordinal);
        foreach (var hook in lifecycleHooks ?? [])
        {
            ArgumentNullException.ThrowIfNull(hook);
            if (!ValidId(hook.PluginId) || hook.HookName is not ("interrupt" or "stop" or "subagentStop"))
                throw new ArgumentException("Plugin lifecycle hook binding is invalid.");
            if (!hooks.TryAdd(HookKey(hook.PluginId, hook.HookName), hook))
                throw new ArgumentException("Duplicate plugin lifecycle hook binding.");
        }
        _hookImplementations = hooks;
        Snapshot = LoadValidated();
    }

    public string DirectoryPath => _directory;
    public PluginCatalogSnapshot Snapshot { get; private set; }
    public string? LastReloadError { get; private set; }
    public int HookFailureCount => Volatile.Read(ref _hookFailureCount);
    public string? LastHookError { get { lock (_sync) return _lastHookError; } }
    public bool Watching => _watcher is not null;
    public event Action<PluginCatalogSnapshot>? Changed;

    public PluginCatalogSnapshot Reload()
    {
        ThrowIfDisposed();
        PluginCatalogSnapshot next;
        try { next = LoadValidated(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            LastReloadError = ex.GetType().Name;
            throw;
        }

        PluginCatalogSnapshot previous;
        lock (_sync)
        {
            previous = Snapshot;
            Snapshot = next;
            LastReloadError = null;
        }
        try { Changed?.Invoke(next); }
        catch (Exception ex)
        {
            lock (_sync) { Snapshot = previous; LastReloadError = ex.GetType().Name; }
            try { Changed?.Invoke(previous); } catch { }
            throw;
        }
        return next;
    }

    public IDisposable BindLifecycle(AgentLifecycleHub lifecycle)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(lifecycle);
        return lifecycle.Subscribe(async (evt, ct) =>
        {
            var hookName = evt.Kind switch
            {
                AgentLifecycleKind.Interrupt => "interrupt",
                AgentLifecycleKind.Stop => "stop",
                AgentLifecycleKind.SubagentStop => "subagentStop",
                _ => null
            };
            if (hookName is null) return;
            PluginCatalogSnapshot snapshot;
            lock (_sync) snapshot = Snapshot;
            foreach (var manifest in snapshot.Manifests)
            {
                if (!manifest.Hooks.Contains(hookName, StringComparer.Ordinal)) continue;
                if (!_hookImplementations.TryGetValue(HookKey(manifest.Id, hookName), out var hook)) continue;
                try { await hook.InvokeAsync(evt, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _hookFailureCount);
                    lock (_sync) _lastHookError = manifest.Id + ":" + hookName + ":" + ex.GetType().Name;
                }
            }
        });
    }

    public void StartWatching()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (_watcher is not null) return;
            _debounce = new Timer(_ => WatcherReload(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(_directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
        }
    }

    public void InstallOrUpdate(PluginPackage package)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(package.Manifest);
        ArgumentNullException.ThrowIfNull(package.EntryBytes);
        if (!ValidId(package.Manifest.Id)) throw new ArgumentException("Plugin package ID is invalid.");
        var actual = Convert.ToHexString(SHA256.HashData(package.EntryBytes)).ToLowerInvariant();
        if (!StringComparer.OrdinalIgnoreCase.Equals(actual, package.Manifest.Sha256))
            throw new ArgumentException("Plugin package bytes do not match the manifest SHA-256 pin.");
        var entry = ContainedPath(package.Manifest.EntryFile);
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        var manifest = Path.Combine(_directory, package.Manifest.Id + ".plugin.json");
        var oldEntry = File.Exists(entry) ? File.ReadAllBytes(entry) : null;
        var oldManifest = File.Exists(manifest) ? File.ReadAllBytes(manifest) : null;
        try
        {
            AtomicWrite(entry, package.EntryBytes);
            AtomicWrite(manifest, JsonSerializer.SerializeToUtf8Bytes(package.Manifest, WireJson.Options));
            Reload();
        }
        catch
        {
            Restore(entry, oldEntry);
            Restore(manifest, oldManifest);
            try { Reload(); } catch { }
            throw;
        }
    }

    public bool Uninstall(string pluginId)
    {
        ThrowIfDisposed();
        if (!ValidId(pluginId)) throw new ArgumentException("Plugin ID is invalid.", nameof(pluginId));
        var manifests = FindManifestFiles(pluginId).ToArray();
        if (manifests.Length == 0) return false;
        var backups = manifests.ToDictionary(path => path, File.ReadAllBytes, PathComparer());
        try
        {
            foreach (var path in manifests) File.Delete(path);
            Reload();
            return true;
        }
        catch
        {
            foreach (var pair in backups) AtomicWrite(pair.Key, pair.Value);
            try { Reload(); } catch { }
            throw;
        }
    }

    private PluginCatalogSnapshot LoadValidated()
    {
        var snapshot = PluginCatalog.Load(_directory, _implementations, _agentVersion);
        foreach (var manifest in snapshot.Manifests)
            foreach (var hook in manifest.Hooks)
                if (!_hookImplementations.ContainsKey(HookKey(manifest.Id, hook)))
                    throw new ArgumentException($"Plugin {manifest.Id} declares lifecycle hook {hook} without a locally supplied implementation.");
        return snapshot;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        lock (_sync)
        {
            if (_debounce is null || Volatile.Read(ref _disposed) != 0) return;
            _debounce.Change(250, Timeout.Infinite);
        }
    }

    private void WatcherReload()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { Reload(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        { /* Last-known-good snapshot and LastReloadError are retained. */ }
    }

    private IReadOnlyList<string> FindManifestFiles(string pluginId)
    {
        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.plugin.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllBytes(path), WireJson.Options);
                if (manifest is not null && StringComparer.Ordinal.Equals(manifest.Id, pluginId)) result.Add(path);
            }
            catch (JsonException) { }
        }
        return result;
    }

    private string ContainedPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new ArgumentException("Plugin package path must be relative.");
        var full = Path.GetFullPath(Path.Combine(_directory, relative));
        var prefix = _directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Plugin package path escapes the plugin directory.");
        return full;
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, overwrite: true); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static void Restore(string path, byte[]? bytes)
    {
        if (bytes is null) { try { File.Delete(path); } catch { } }
        else AtomicWrite(path, bytes);
    }

    private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100 &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');
    private static string HookKey(string pluginId, string hook) => pluginId + "\0" + hook;
    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_sync)
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileChanged; _watcher.Created -= OnFileChanged; _watcher.Deleted -= OnFileChanged; _watcher.Renamed -= OnFileChanged;
                _watcher.Dispose(); _watcher = null;
            }
            _debounce?.Dispose(); _debounce = null;
        }
    }
}
