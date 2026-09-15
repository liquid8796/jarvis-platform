using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace JarvisCode.App.Services;

/// <summary>
/// Raised when the Electron runtime the Browser pane needs is not on disk and
/// could not be fetched. The message is the one the pane shows the user, so it
/// names the cause rather than the exception that carried it.
/// </summary>
public sealed class ElectronRuntimeUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>How far along <see cref="ElectronRuntime.EnsureAsync"/> is, for the pane's first-run card.</summary>
/// <param name="Stage">Downloading, Verifying or Extracting.</param>
/// <param name="BytesDone">Bytes received so far, or 0 outside the download stage.</param>
/// <param name="BytesTotal">Total bytes when the server declared a length, else null.</param>
public readonly record struct ElectronRuntimeProgress(string Stage, long BytesDone, long? BytesTotal);

/// <summary>
/// Resolves the Electron build the Browser pane renders with, downloading it
/// once per machine on first use.
///
/// The version is pinned rather than tracked: this app's pane exists to match
/// the reference desktop's, and the reference (Claude 1.40609.1.0) ships
/// Electron 42.10.0 — measured from its own <c>app/version</c> file and
/// confirmed by the <c>Chrome/148.0.7778.280</c> string in both that build's
/// <c>claude.exe</c> and this Electron's <c>electron.exe</c>. Upgrading the pin
/// changes what the pane renders with and so is a parity decision, never a
/// maintenance one.
///
/// The download is checked against the SHA-256 Electron publishes in its
/// release's <c>SHASUMS256.txt</c>, so a corrupted or substituted archive is
/// refused rather than unpacked. Nothing is executed from a partial download:
/// bytes land in a <c>.part</c> file, the hash is checked before the archive is
/// opened, extraction goes to a scratch directory, and only a complete tree is
/// moved into place and marked ready.
///
/// The cache is per machine-user and **not** per <c>--profile</c>: the runtime
/// is immutable and 374 MB, so a second profile reuses the first profile's copy
/// instead of fetching its own. It lives under LocalApplicationData rather than
/// the roaming ApplicationData the rest of the profile uses, because a roaming
/// profile must not carry a third of a gigabyte of binaries between machines.
/// </summary>
public sealed class ElectronRuntime : IDisposable
{
    /// <summary>The reference desktop's own Electron version (Claude 1.40609.1.0, <c>app/version</c>).</summary>
    public const string Version = "42.10.0";

    /// <summary>
    /// The Chromium this Electron carries, from electron/electron's <c>DEPS</c> at
    /// <c>v42.10.0</c>. Recorded so a pin change that moves the engine is visible in the diff.
    /// </summary>
    public const string ChromiumVersion = "148.0.7778.280";

    /// <summary>The archive Electron publishes for this platform.</summary>
    public const string ArchiveName = "electron-v" + Version + "-win32-x64.zip";

    /// <summary>
    /// The archive's SHA-256, copied from the release's own <c>SHASUMS256.txt</c>.
    /// A mismatch fails the install; it is never downgraded to a warning.
    /// </summary>
    public const string ArchiveSha256 = "6988553dc944800c127f6600133b9dd7810a83a82b9b68d2faa07dbb10ef5071";

    public const string DownloadUrl =
        "https://github.com/electron/electron/releases/download/v" + Version + "/" + ArchiveName;

    /// <summary>
    /// Written last, after the extracted tree is in place. Its absence marks an
    /// interrupted install, so a directory left behind by a killed process is
    /// rebuilt rather than launched.
    /// </summary>
    private const string ReadyMarker = ".ready";

    private readonly string _root;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _expectedSha256;

    private readonly Lock _gate = new();
    private Task<string>? _ensuring;

    /// <param name="expectedSha256">
    /// The archive hash to accept. Defaults to the published one and is
    /// overridden only by tests, which serve an archive of their own — the
    /// production path is always pinned to <see cref="ArchiveSha256"/>.
    /// </param>
    public ElectronRuntime(string? root = null, HttpClient? http = null, string? expectedSha256 = null)
    {
        _root = root ?? DefaultRoot;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _expectedSha256 = expectedSha256 ?? ArchiveSha256;
    }

    /// <summary>Where a machine-user's pinned runtimes live, one directory per version.</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisCode", "electron", Version);

    /// <summary>The directory the archive unpacks into.</summary>
    public string InstallRoot => _root;

    public string ExecutablePath => Path.Combine(_root, "electron.exe");

    /// <summary>
    /// True when a complete runtime is on disk. Both the marker and the
    /// executable are checked: either alone can survive a half-finished install.
    /// </summary>
    public bool IsInstalled =>
        File.Exists(Path.Combine(_root, ReadyMarker)) && File.Exists(ExecutablePath);

    /// <summary>
    /// The path to <c>electron.exe</c>, fetching and unpacking the runtime first
    /// when it is not already there. Concurrent callers share one install rather
    /// than racing to write the same directory.
    /// </summary>
    public Task<string> EnsureAsync(
        IProgress<ElectronRuntimeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsInstalled)
        {
            return Task.FromResult(ExecutablePath);
        }

        lock (_gate)
        {
            // A faulted or cancelled attempt must not be handed to the next
            // caller as this one's answer — it is dropped and the install retried.
            if (_ensuring is { IsCompleted: true, IsCompletedSuccessfully: false })
            {
                _ensuring = null;
            }

            return _ensuring ??= InstallAsync(progress, cancellationToken);
        }
    }

    private async Task<string> InstallAsync(
        IProgress<ElectronRuntimeProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);

        // Two app instances (two --profile runs, say) can install at once, so
        // no attempt writes to a path another attempt also owns, and none
        // publishes over a finished install. That is the whole invariant; a
        // cross-process lock is not needed to hold it, and a Mutex could not
        // hold it here anyway - it is thread-affine, and this method resumes on
        // a different thread after every await.
        var attempt = Guid.NewGuid().ToString("n");
        var archive = _root + "." + attempt + ".zip";
        var partial = archive + ".part";
        var staging = _root + "." + attempt + ".staging";

        try
        {
            await DownloadAsync(partial, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new ElectronRuntimeProgress("Verifying", 0, null));
            await VerifyAsync(partial, _expectedSha256, cancellationToken).ConfigureAwait(false);
            File.Move(partial, archive, overwrite: true);

            progress?.Report(new ElectronRuntimeProgress("Extracting", 0, null));
            ZipFile.ExtractToDirectory(archive, staging);

            if (!File.Exists(Path.Combine(staging, "electron.exe")))
            {
                throw new ElectronRuntimeUnavailableException(
                    $"The Electron {Version} archive unpacked without an electron.exe. " +
                    "Delete " + _root + "* and try again.");
            }

            // The marker goes inside the staged tree, so the directory that
            // becomes the install is already complete when it arrives.
            await File.WriteAllTextAsync(
                Path.Combine(staging, ReadyMarker), Version, cancellationToken).ConfigureAwait(false);

            return Publish(staging);
        }
        catch (Exception ex) when (ex is not (ElectronRuntimeUnavailableException or OperationCanceledException))
        {
            throw new ElectronRuntimeUnavailableException(
                $"The Browser pane needs Electron {Version} and it could not be installed: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(staging);
            TryDeleteFile(partial);
            TryDeleteFile(archive);
        }
    }

    /// <summary>
    /// Moves a complete staged tree into place, unless someone got there first.
    /// A finished install is never deleted to make room for an identical one:
    /// the runtime is content-pinned, so whoever won produced the same bytes.
    /// </summary>
    private string Publish(string staging)
    {
        if (IsInstalled)
        {
            return ExecutablePath;
        }

        try
        {
            Directory.Move(staging, _root);
        }
        catch (IOException) when (IsInstalled)
        {
            // Another attempt published between the check and the move.
        }

        return ExecutablePath;
    }

    private async Task DownloadAsync(
        string partial, IProgress<ElectronRuntimeProgress>? progress, CancellationToken cancellationToken)
    {
        TryDeleteFile(partial);

        using var response = await _http
            .GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ElectronRuntimeUnavailableException(
                $"Downloading Electron {Version} failed with HTTP {(int)response.StatusCode} " +
                $"({response.ReasonPhrase}). The Browser pane cannot open until it is installed.");
        }

        var total = response.Content.Headers.ContentLength;
        progress?.Report(new ElectronRuntimeProgress("Downloading", 0, total));

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var file = File.Create(partial))
        {
            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                progress?.Report(new ElectronRuntimeProgress("Downloading", done, total));
            }
        }
    }

    private static async Task VerifyAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));

        if (!string.Equals(hash, expected, StringComparison.Ordinal))
        {
            throw new ElectronRuntimeUnavailableException(
                $"The downloaded Electron {Version} archive did not match its published checksum " +
                $"(expected {expected}, got {hash}). It was discarded rather than unpacked.");
        }
    }

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Cleanup on the way out of a failed install: never masks the real error.</summary>
    private static void TryDelete(string directory)
    {
        try
        {
            Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover file costs disk, not correctness: the next install
            // overwrites it. Failing the install over it would be worse.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
