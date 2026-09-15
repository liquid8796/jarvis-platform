using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Core.Models;

namespace JarvisCode.App.Tests.Services;

public sealed class CustomOutputStylesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-output-styles-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Unicode_style_round_trips_and_project_override_does_not_modify_user_file()
    {
        var profile = Path.Combine(_root, "profile");
        var project = Path.Combine(_root, "project");
        var userFile = CustomOutputStyles.Save(Path.Combine(profile, "output-styles"), "Tiếng Việt",
            "Giải thích: rõ ràng\nCó ví dụ", "Trả lời tiếng Việt.\nGiữ nguyên mã.", true);
        var original = File.ReadAllBytes(userFile);
        CustomOutputStyles.Save(Path.Combine(project, ".jarvis", "output-styles"), "Tiếng Việt", "Project", "Project answer", false);
        var style = Assert.Single(CustomOutputStyles.Load(project, profile, legacyUserRoot: ""));
        Assert.Equal("Project answer", style.Prompt);
        Assert.False(style.KeepCodingInstructions);
        Assert.Equal(original, File.ReadAllBytes(userFile));
        var user = Assert.Single(CustomOutputStyles.Load(null, profile, legacyUserRoot: ""));
        Assert.Equal("Giải thích: rõ ràng\nCó ví dụ", user.Description);
        Assert.Equal("Trả lời tiếng Việt.\nGiữ nguyên mã.", user.Prompt);
        Assert.True(user.KeepCodingInstructions);
    }

    [Fact]
    public void Claude_style_is_imported_and_jarvis_override_is_scoped()
    {
        var project = Path.Combine(_root, "project");
        var legacy = Path.Combine(project, ".claude", "output-styles");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "imported.md"), "---\nname: Imported\ndescription: Imported style\n---\n\nUse prose.");
        var imported = Assert.Single(CustomOutputStyles.Load(project, null, legacyUserRoot: ""));
        Assert.False(imported.KeepCodingInstructions);
        Assert.Equal("Use prose.", imported.Prompt);
        CustomOutputStyles.Save(Path.Combine(project, ".jarvis", "output-styles"), "Imported", "Local", "Use tables.", true);
        Assert.Equal("Use tables.", Assert.Single(CustomOutputStyles.Load(project, null, legacyUserRoot: "")).Prompt);
        Assert.Contains("Use prose.", File.ReadAllText(Path.Combine(legacy, "imported.md")));
    }

    [Fact]
    public void Custom_style_controls_coding_section_but_preserves_tool_and_permission_instructions()
    {
        var model = new ModelInfo("anthropic", "claude-opus-4-5", "Opus", 200000);
        var styled = ClassicPromptBuilder.Build(_root, model, outputStyleBlock: "# Output Style: Explainer\nExplain concepts.", keepCodingInstructions: false);
        Assert.DoesNotContain("# Doing tasks", styled);
        Assert.Contains("# System", styled);
        Assert.Contains("# Executing actions with care", styled);
        Assert.Contains("# Using your tools", styled);
        Assert.Contains("Explain concepts.", styled);
        Assert.Contains(ReferencePromptBuilder.IntroWithOutputStyle, styled);
        Assert.Contains("# Doing tasks", ClassicPromptBuilder.Build(_root, model));
    }

    [Fact]
    public void Editor_is_reachable_from_output_style_submenu()
    {
        var rows = SessionMenuModel.ForSidebarRow(new SessionMenuContext { OutputStyles = [new("default", "Default", true)] });
        var submenu = Assert.Single(rows, row => row.Label == "Output style");
        Assert.Contains(submenu.Children!, row => row.Action == SessionMenuAction.EditOutputStyles);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Concise")]
    public void Invalid_or_reserved_names_do_not_write_files(string name)
    {
        Assert.Throws<ArgumentException>(() => CustomOutputStyles.Save(Path.Combine(_root, "styles"), name, "", "test", true));
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
