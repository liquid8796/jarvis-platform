using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Core.Memory;

namespace JarvisCode.App.Tests.Services;

/// <summary>The rows the Customize › Memory page lists.</summary>
public sealed class CustomizeMemoryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jarvis-memory-page-" + Guid.NewGuid().ToString("N")[..8]);

    public CustomizeMemoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void AFileWithFrontmatterIsTitledByItsName()
    {
        var entry = CustomizeMemory.Parse(
            "C:/memory/prefs.md",
            "---\nname: build-preferences\ndescription: How they build\n---\n\nAlways run the tests.");

        Assert.Equal("build-preferences", entry.Title);
        Assert.Equal("How they build", entry.Description);
        Assert.Equal("Always run the tests.", entry.Body);
        Assert.Equal("prefs.md", entry.FileName);
    }

    [Fact]
    public void AFileWithoutFrontmatterTakesItsFileName()
    {
        var entry = CustomizeMemory.Parse("C:/memory/MEMORY.md", "- [A note](a.md) — hook");

        Assert.Equal("MEMORY", entry.Title);
        Assert.Null(entry.Description);
        Assert.Equal("- [A note](a.md) — hook", entry.Body);
    }

    [Fact]
    public void TheIndexIsListedFirstAndTheRestAlphabetically()
    {
        File.WriteAllText(Path.Combine(_root, "zebra.md"), "z");
        File.WriteAllText(Path.Combine(_root, "alpha.md"), "a");
        File.WriteAllText(Path.Combine(_root, ProjectMemory.IndexFileName), "index");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not markdown");

        var entries = CustomizeMemory.Load(_root);

        Assert.Equal(["MEMORY", "alpha", "zebra"], entries.Select(e => e.Title));
    }

    [Fact]
    public void AMissingFolderListsNothing() =>
        Assert.Empty(CustomizeMemory.Load(Path.Combine(_root, "gone")));

    [Fact]
    public void DeleteRemovesTheFileAndSaysWhenItCould()
    {
        var path = Path.Combine(_root, "note.md");
        File.WriteAllText(path, "x");

        Assert.True(CustomizeMemory.Delete(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TheSwitchDescriptionSaysWhatTheCurrentStateMeans()
    {
        Assert.Equal(
            "Jarvis will read and update these memories during Code sessions.",
            CustomizeMemory.SwitchDescription(true));
        Assert.Equal(
            "Paused. Existing memories are kept but won’t be read or updated in new sessions.",
            CustomizeMemory.SwitchDescription(false));
    }
}
