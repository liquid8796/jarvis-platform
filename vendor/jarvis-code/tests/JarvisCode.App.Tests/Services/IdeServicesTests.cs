using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class IdeServicesTests
{
    [Fact]
    public void The_delivered_extension_packages_as_an_installable_vsix()
    {
        var destination = Environment.GetEnvironmentVariable("JARVIS_IDE_TEST_VSIX") ??
            Path.Combine(Path.GetTempPath(), "jarvis-ide-package-" + Guid.NewGuid().ToString("N") + ".vsix");
        var keep = Environment.GetEnvironmentVariable("JARVIS_IDE_TEST_VSIX") is not null;
        try
        {
            IdeExtensionInstaller.CreatePackage(Path.Combine(AppContext.BaseDirectory, "Assets", "IdeExtension"), destination);
            using var zip = ZipFile.OpenRead(destination);
            using var manifest = zip.GetEntry("extension.vsixmanifest")!.Open();
            var xml = XDocument.Load(manifest);
            Assert.Equal("jarvis-code-ide", xml.Descendants().Single(e => e.Name.LocalName == "Identity").Attribute("Id")!.Value);
            using var package = new StreamReader(zip.GetEntry("extension/package.json")!.Open());
            var json = JsonNode.Parse(package.ReadToEnd())!;
            Assert.Equal("jarvis-code", json["publisher"]!.ToString());
            Assert.NotNull(zip.GetEntry("extension/extension.js"));
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        }
        finally { if (!keep && File.Exists(destination)) File.Delete(destination); }
    }
}
