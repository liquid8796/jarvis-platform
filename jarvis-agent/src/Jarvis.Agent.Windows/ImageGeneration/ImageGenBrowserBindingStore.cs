using System.IO;
using System.Text.Json;
using Jarvis.Agent.Core.ImageGeneration;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.ImageGeneration;

/// <summary>Local UI/extension-owned selection. Remote ImageGen tool arguments cannot create or change this binding.</summary>
public sealed class ImageGenBrowserBindingStore(string root)
{
    public string FilePath => Path.Combine(root, "imagegen-browser.json");
    public ImageBrowserBinding? Read()
    {
        ImageGenerationSafety.NoLinks(FilePath);
        if (!File.Exists(FilePath)) return null;
        if (new FileInfo(FilePath).Length > 8192) throw new IOException("ImageGen browser configuration is oversized.");
        using var file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(file);
        var binding = JsonSerializer.Deserialize<ImageBrowserBinding>(reader.ReadToEnd(), WireJson.Options)
            ?? throw new IOException("ImageGen browser configuration is invalid.");
        Validate(binding);
        return binding;
    }
    public ImageBrowserBinding Save(string instance, string family, string? expectedRevision)
    {
        ImageGenerationSafety.NoLinks(FilePath);
        Directory.CreateDirectory(root);
        ImageGenerationSafety.NoLinks(FilePath + ".lock");
        using var lease = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var old = Read();
        if (old?.Revision != expectedRevision) throw new IOException("ImageGen browser selection changed. Refresh before saving.");
        if (old?.ExtensionInstanceId == instance && old.BrowserFamily == family) return old;
        var binding = new ImageBrowserBinding(instance, family, Guid.NewGuid().ToString("N")); Validate(binding);
        ImageGenerationSafety.AtomicWrite(FilePath, JsonSerializer.Serialize(binding, WireJson.Options));
        return binding;
    }
    public void Clear(string? expectedRevision)
    {
        ImageGenerationSafety.NoLinks(FilePath);
        Directory.CreateDirectory(root);
        ImageGenerationSafety.NoLinks(FilePath + ".lock");
        using var lease = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Read()?.Revision != expectedRevision) throw new IOException("ImageGen browser selection changed. Refresh first.");
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
    private static void Validate(ImageBrowserBinding binding)
    {
        if (!Guid.TryParse(binding.ExtensionInstanceId, out _) || !Guid.TryParseExact(binding.Revision, "N", out _) ||
            binding.BrowserFamily is not ("chrome" or "edge")) throw new IOException("Select an actual Chrome or Edge extension instance for ImageGen.");
    }
}
