using System.IO;

namespace JarvisCode.Parity.Tests;

public sealed class ReferenceDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-reference-discovery-" + Guid.NewGuid().ToString("N"));

    private string Exe(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative, "claude.exe"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    public void Msix_and_regular_bundles_are_compared_by_numeric_version()
    {
        Exe("regular/2.1.99");
        var latest = Exe("msix/2.1.260");
        Assert.Equal(latest, ReferenceInstall.FindCliFrom(null, Path.Combine(_root, "absent.exe"),
            [Path.Combine(_root, "regular"), Path.Combine(_root, "msix")]));
    }

    [Fact]
    public void Standalone_remains_default_but_explicit_bundle_is_authoritative()
    {
        var standalone = Exe("standalone");
        var bundled = Exe("bundle/2.1.260");
        Assert.Equal(standalone, ReferenceInstall.FindCliFrom(null, standalone, [Path.Combine(_root, "bundle")]));
        Assert.Equal(bundled, ReferenceInstall.FindCliFrom(bundled, standalone, []));
        Assert.Null(ReferenceInstall.FindCliFrom(Path.Combine(_root, "missing.exe"), standalone, []));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
