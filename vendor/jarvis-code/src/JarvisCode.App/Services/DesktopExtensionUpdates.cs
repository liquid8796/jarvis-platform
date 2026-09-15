using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace JarvisCode.App.Services;

/// <summary>
/// The authenticated directory contract, kept separate from local bundle installs.
/// The reference reads versions and /extensions/{id}/download/{version} through
/// its account client. An implementation must verify the publisher signature and
/// policy verdict; a local path or a manifest homepage is never an update source.
/// </summary>
public interface IDesktopExtensionDirectory
{
    bool IsAvailable { get; }
    Task<DesktopExtensionRelease?> FindUpdateAsync(string directoryId, string currentVersion, CancellationToken token);
    Task<Stream> OpenBundleAsync(string directoryId, DesktopExtensionRelease release, CancellationToken token);
}

public sealed record DesktopExtensionRelease(
    string Version, string Sha256, bool PolicyAllowed = true,
    bool SignatureRequired = false, bool SignatureVerified = false, bool IsInternal = false);

internal sealed class UnavailableExtensionDirectory : IDesktopExtensionDirectory
{
    public bool IsAvailable => false;
    public Task<DesktopExtensionRelease?> FindUpdateAsync(string directoryId, string currentVersion, CancellationToken token) =>
        Task.FromResult<DesktopExtensionRelease?>(null);
    public Task<Stream> OpenBundleAsync(string directoryId, DesktopExtensionRelease release, CancellationToken token) =>
        throw new InvalidOperationException("No extension directory is connected.");
}

public sealed record PendingExtensionUpdate(
    string ExtensionId, string DirectoryId, string Name, string PreviousVersion, string Version, string Sha256,
    bool IsInternal, bool Approved = false);

public sealed record ExtensionUpdateResult(int Checked, int Staged, int Applied, IReadOnlyList<string> Errors);

/// <summary>
/// Desktop 1.46388.3.0's six-hour updater: check/cache while running; install the
/// cache at next startup, before MCP servers start. Local files are ineligible.
/// The directory is absent in a provider-key installation, so such a profile
/// performs no invented network lookup; its toggle still controls the real runner.
/// </summary>
public sealed class DesktopExtensionUpdates : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private readonly ProfilePaths _paths;
    private readonly Func<bool> _enabled;
    private readonly IDesktopExtensionDirectory _directory;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Timer? _timer;
    private bool _started;

    public DesktopExtensionUpdates(ProfilePaths paths, Func<bool> enabled, IDesktopExtensionDirectory? directory = null)
    {
        _paths = paths;
        _enabled = enabled;
        _directory = directory ?? new UnavailableExtensionDirectory();
    }

    public event Action? Changed;
    public bool DirectoryAvailable => _directory.IsAvailable;
    public static bool IsEligible(InstalledExtension extension) => extension.DirectoryId is { Length: > 0 } id &&
        !id.StartsWith("local.", StringComparison.Ordinal);
    private string PendingRoot => Path.Combine(DesktopExtensions.UpdateRoot(_paths), "pending");

    public void Start()
    {
        if (_started) return;
        _started = true;
        ApplyPending();
        _timer = new Timer(_ => _ = CheckNowAsync(), null, CheckInterval, CheckInterval);
        _ = CheckNowAsync();
    }

    public IReadOnlyList<PendingExtensionUpdate> Pending()
    {
        var updates = new List<PendingExtensionUpdate>();
        if (!Directory.Exists(PendingRoot)) return updates;
        foreach (var path in Directory.EnumerateDirectories(PendingRoot))
        {
            try
            {
                var update = JsonSerializer.Deserialize<PendingExtensionUpdate>(File.ReadAllText(Path.Combine(path, "update.json")));
                if (update is { ExtensionId.Length: > 0, DirectoryId.Length: > 0, Name.Length: > 0,
                    Version.Length: > 0, Sha256.Length: 64 } && update.ExtensionId == Path.GetFileName(path)) updates.Add(update);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return updates;
    }

    public async Task<ExtensionUpdateResult> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (_lifetime.IsCancellationRequested || !_enabled() || !_directory.IsAvailable)
            return new(0, 0, 0, []);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        if (!await _checking.WaitAsync(0, token).ConfigureAwait(false)) return new(0, 0, 0, []);
        var checkedCount = 0;
        var stagedCount = 0;
        var errors = new List<string>();
        try
        {
            foreach (var extension in DesktopExtensions.List(_paths).Where(IsEligible))
            {
                string? staging = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    checkedCount++;
                    var update = await _directory.FindUpdateAsync(extension.DirectoryId!, extension.Manifest.Version, token).ConfigureAwait(false);
                    if (update is null || !ExtensionVersions.IsNewer(update.Version, extension.Manifest.Version)) continue;
                    var cached = Pending().FirstOrDefault(entry => entry.ExtensionId == extension.Id);
                    if (cached is not null && !ExtensionVersions.IsNewer(update.Version, cached.Version)) continue;
                    if (!update.PolicyAllowed || update.SignatureRequired && !update.SignatureVerified)
                        throw new InvalidDataException("The directory did not approve this update's policy or signature.");
                    if (update.Sha256.Length != 64 || update.Sha256.Any(c => !Uri.IsHexDigit(c)))
                        throw new InvalidDataException("The directory did not supply a valid update hash.");
                    staging = Path.Combine(DesktopExtensions.UpdateRoot(_paths), "download", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(staging);
                    var bundle = Path.Combine(staging, "bundle.mcpb");
                    await using (var input = await _directory.OpenBundleAsync(extension.DirectoryId!, update, token).ConfigureAwait(false))
                    await using (var output = new FileStream(bundle, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                        {
                            total += read;
                            if (total > DesktopExtensions.MaxBundleBytes) throw new InvalidDataException("The update is larger than 2048 MB.");
                            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        }
                    }
                    VerifyHash(bundle, update.Sha256);
                    var prepared = DesktopExtensions.StageBundle(_paths, bundle, extension.Manifest.Name, update.Version);
                    DesktopExtensions.DeleteUpdateDirectory(_paths, prepared.Directory);
                    var metadata = new PendingExtensionUpdate(extension.Id, extension.DirectoryId!, extension.Manifest.Name,
                        extension.Manifest.Version, update.Version, update.Sha256, update.IsInternal);
                    File.WriteAllText(Path.Combine(staging, "update.json"), JsonSerializer.Serialize(metadata));
                    var destination = Path.Combine(PendingRoot, extension.Id);
                    Directory.CreateDirectory(PendingRoot);
                    DesktopExtensions.DeleteUpdateDirectory(_paths, destination);
                    Directory.Move(staging, destination);
                    staging = null;
                    stagedCount++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or JsonException or System.Net.Http.HttpRequestException)
                {
                    errors.Add(extension.Manifest.Title + ": " + error.Message);
                }
                finally { if (staging is not null) DesktopExtensions.DeleteUpdateDirectory(_paths, staging); }
            }
        }
        finally { _checking.Release(); }
        if (stagedCount > 0 || errors.Count > 0) Changed?.Invoke();
        return new(checkedCount, stagedCount, 0, errors);
    }

    public ExtensionUpdateResult ApplyPending(bool confirmInternal = false)
    {
        var applied = 0;
        var errors = new List<string>();
        foreach (var update in Pending())
        {
            var directory = Path.Combine(PendingRoot, update.ExtensionId);
            var installed = DesktopExtensions.List(_paths).FirstOrDefault(entry => entry.Id == update.ExtensionId);
            if (installed is null || !IsEligible(installed) || installed.DirectoryId != update.DirectoryId ||
                !ExtensionVersions.IsNewer(update.Version, installed.Manifest.Version))
            {
                DesktopExtensions.DeleteUpdateDirectory(_paths, directory);
                continue;
            }
            if (update.IsInternal && !update.Approved && !confirmInternal) continue;
            try
            {
                var bundle = Path.Combine(directory, "bundle.mcpb");
                VerifyHash(bundle, update.Sha256);
                var (replacement, error) = DesktopExtensions.Install(_paths, bundle, update.DirectoryId, update.Name, update.Version);
                if (replacement is null) throw new InvalidDataException(error ?? "The update could not be installed.");
                applied++;
                DesktopExtensions.DeleteUpdateDirectory(_paths, directory);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or JsonException)
            {
                errors.Add(update.Name + ": " + error.Message);
            }
        }
        if (applied > 0 || errors.Count > 0) Changed?.Invoke();
        return new(0, 0, applied, errors);
    }

    public void ApproveInternalUpdate(string extensionId)
    {
        var update = Pending().FirstOrDefault(entry => entry.ExtensionId == extensionId && entry.IsInternal);
        if (update is null) return;
        File.WriteAllText(Path.Combine(PendingRoot, extensionId, "update.json"), JsonSerializer.Serialize(update with { Approved = true }));
        Changed?.Invoke();
    }

    private static void VerifyHash(string file, string expected)
    {
        using var stream = File.OpenRead(file);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update archive did not match the directory's hash.");
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _lifetime.Cancel();
    }
}

internal static class ExtensionVersions
{
    public static bool IsNewer(string candidate, string current)
    {
        static (int[] Main, string[] Pre)? Parse(string value)
        {
            var text = value.Split('+')[0].Split('-', 2);
            var core = text[0].Split('.');
            if (core.Length != 3 || core.Any(part => !int.TryParse(part, out var n) || n < 0)) return null;
            return (core.Select(int.Parse).ToArray(), text.Length == 1 ? [] : text[1].Split('.'));
        }
        if (Parse(candidate) is not { } left || Parse(current) is not { } right) return false;
        for (var i = 0; i < 3; i++) if (left.Main[i] != right.Main[i]) return left.Main[i] > right.Main[i];
        if (left.Pre.Length == 0 || right.Pre.Length == 0) return left.Pre.Length == 0 && right.Pre.Length != 0;
        for (var i = 0; i < Math.Min(left.Pre.Length, right.Pre.Length); i++)
        {
            var a = left.Pre[i]; var b = right.Pre[i];
            if (a == b) continue;
            var an = long.TryParse(a, out var av); var bn = long.TryParse(b, out var bv);
            return an && bn ? av > bv : an != bn ? !an : string.CompareOrdinal(a, b) > 0;
        }
        return left.Pre.Length > right.Pre.Length;
    }
}
