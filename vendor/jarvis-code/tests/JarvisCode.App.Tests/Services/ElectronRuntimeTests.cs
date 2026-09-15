using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// Serves one canned response to every request, and counts them — which is how
/// the single-flight test tells one shared install from two racing ones.
/// </summary>
internal sealed class CannedHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(respond());
    }
}

/// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread.</summary>
internal sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public sealed class ElectronRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "jarvis-electron-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_root)!;
        foreach (var path in Directory.Exists(parent) ? Directory.GetDirectories(parent) : [])
        {
            if (path.StartsWith(_root, StringComparison.Ordinal))
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>An archive shaped like Electron's: an electron.exe plus a sibling.</summary>
    private static byte[] Archive(string exeContent = "MZ fake")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = new StreamWriter(zip.CreateEntry("electron.exe").Open()))
            {
                entry.Write(exeContent);
            }

            using var version = new StreamWriter(zip.CreateEntry("version").Open());
            version.Write(ElectronRuntime.Version);
        }

        return buffer.ToArray();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public void ReportsTheEngineTheReferenceShips()
    {
        // The pin is the whole point of the class: these two constants are what
        // make the pane render with the reference desktop's own engine.
        Assert.Equal("42.10.0", ElectronRuntime.Version);
        Assert.Equal("148.0.7778.280", ElectronRuntime.ChromiumVersion);
        Assert.Equal(
            "https://github.com/electron/electron/releases/download/v42.10.0/electron-v42.10.0-win32-x64.zip",
            ElectronRuntime.DownloadUrl);
    }

    [Fact]
    public void AnEmptyRootIsNotAnInstall()
    {
        using var runtime = new ElectronRuntime(_root);
        Assert.False(runtime.IsInstalled);
    }

    [Fact]
    public void AnInterruptedInstallIsNotMistakenForAFinishedOne()
    {
        // A killed process can leave the executable behind with no marker. That
        // tree must be rebuilt, not launched.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "electron.exe"), "MZ fake");

        using var runtime = new ElectronRuntime(_root);
        Assert.False(runtime.IsInstalled);
    }

    [Fact]
    public async Task InstallsAVerifiedArchiveAndMarksItReady()
    {
        var bytes = Archive();
        var handler = new CannedHandler(() => Ok(bytes));
        using var runtime = new ElectronRuntime(_root, new HttpClient(handler), Sha256(bytes));

        var exe = await runtime.EnsureAsync();

        Assert.Equal(Path.Combine(_root, "electron.exe"), exe);
        Assert.True(File.Exists(exe));
        Assert.True(runtime.IsInstalled);
        Assert.Equal(ElectronRuntime.Version, File.ReadAllText(Path.Combine(_root, "version")));
    }

    [Fact]
    public async Task AnInstalledRuntimeIsNotDownloadedAgain()
    {
        var bytes = Archive();
        var handler = new CannedHandler(() => Ok(bytes));
        using var runtime = new ElectronRuntime(_root, new HttpClient(handler), Sha256(bytes));

        await runtime.EnsureAsync();
        await runtime.EnsureAsync();

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneInstall()
    {
        var bytes = Archive();
        var handler = new CannedHandler(() => Ok(bytes));
        using var runtime = new ElectronRuntime(_root, new HttpClient(handler), Sha256(bytes));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => runtime.EnsureAsync()));

        Assert.Equal(1, handler.Calls);
        Assert.All(results, path => Assert.Equal(runtime.ExecutablePath, path));
    }

    [Fact]
    public async Task TwoConcurrentInstallsDoNotCollideOverTheSameScratchPaths()
    {
        // Two app instances (two --profile runs) reaching a cold cache at the
        // same time: separate objects, one directory. Each attempt must own its
        // own download and staging paths, or one unpacks into the other's
        // half-written tree. The stronger property - that publishing never
        // deletes a finished install - is held by the IsInstalled check in
        // Publish and is not asserted here: both orderings end at the same
        // bytes, so no end-state assertion can tell them apart.
        var bytes = Archive();
        var sha = Sha256(bytes);
        using var first = new ElectronRuntime(_root, new HttpClient(new CannedHandler(() => Ok(bytes))), sha);
        using var second = new ElectronRuntime(_root, new HttpClient(new CannedHandler(() => Ok(bytes))), sha);

        var paths = await Task.WhenAll(first.EnsureAsync(), second.EnsureAsync());

        Assert.All(paths, path => Assert.True(File.Exists(path), $"{path} is missing"));
        Assert.True(first.IsInstalled);
        Assert.True(second.IsInstalled);

        // Nothing of either attempt is left lying around next to the install.
        var siblings = Directory.GetFileSystemEntries(Path.GetDirectoryName(_root)!)
            .Where(entry => entry.StartsWith(_root, StringComparison.Ordinal) && entry != _root)
            .ToArray();
        Assert.Empty(siblings);
    }

    [Fact]
    public async Task AnArchiveThatFailsItsChecksumIsDiscardedRatherThanUnpacked()
    {
        var bytes = Archive();
        var handler = new CannedHandler(() => Ok(bytes));

        // The pinned hash is the real Electron archive's, which a synthetic one
        // cannot match — exactly the substituted-download case.
        using var runtime = new ElectronRuntime(_root, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ElectronRuntimeUnavailableException>(() => runtime.EnsureAsync());

        Assert.Contains("did not match its published checksum", ex.Message, StringComparison.Ordinal);
        Assert.False(runtime.IsInstalled);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task AnArchiveWithNoElectronExeIsRefused()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry("readme.txt").Open());
            entry.Write("not electron");
        }

        var bytes = buffer.ToArray();
        var handler = new CannedHandler(() => Ok(bytes));
        using var runtime = new ElectronRuntime(_root, new HttpClient(handler), Sha256(bytes));

        var ex = await Assert.ThrowsAsync<ElectronRuntimeUnavailableException>(() => runtime.EnsureAsync());

        Assert.Contains("without an electron.exe", ex.Message, StringComparison.Ordinal);
        Assert.False(runtime.IsInstalled);
    }

    [Fact]
    public async Task AFailedDownloadNamesTheStatusAndLeavesNothingBehind()
    {
        var handler = new CannedHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            ReasonPhrase = "Not Found",
            Content = new ByteArrayContent([]),
        });
        using var runtime = new ElectronRuntime(_root, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ElectronRuntimeUnavailableException>(() => runtime.EnsureAsync());

        Assert.Contains("HTTP 404", ex.Message, StringComparison.Ordinal);
        Assert.False(runtime.IsInstalled);
    }

    [Fact]
    public async Task AFailedAttemptIsRetriedRatherThanCached()
    {
        var bytes = Archive();
        var attempt = 0;
        var handler = new CannedHandler(() => ++attempt == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) }
            : Ok(bytes));
        using var runtime = new ElectronRuntime(_root, new HttpClient(handler), Sha256(bytes));

        await Assert.ThrowsAsync<ElectronRuntimeUnavailableException>(() => runtime.EnsureAsync());
        var exe = await runtime.EnsureAsync();

        Assert.True(File.Exists(exe));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ProgressIsReportedThroughItsThreeStages()
    {
        var bytes = Archive();
        var handler = new CannedHandler(() => Ok(bytes));
        using var runtime = new ElectronRuntime(_root, new HttpClient(handler), Sha256(bytes));

        // Progress<T> marshals its callback through the captured synchronization
        // context, so a report can land after the awaited install returns and
        // the assertion below would race it. Reporting straight through is what
        // makes this deterministic.
        var stages = new List<string>();
        await runtime.EnsureAsync(new DirectProgress<ElectronRuntimeProgress>(p =>
        {
            if (stages.Count == 0 || stages[^1] != p.Stage)
            {
                stages.Add(p.Stage);
            }
        }));

        Assert.Equal(["Downloading", "Verifying", "Extracting"], stages);
    }
}
