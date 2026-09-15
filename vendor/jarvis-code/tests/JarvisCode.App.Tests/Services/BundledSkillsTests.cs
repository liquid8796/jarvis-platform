using System.IO;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The skills this build ships embedded. They go through Core's own frontmatter
/// reader, so what is asserted here is that they are found, parsed, and
/// shadowable.
/// </summary>
public class BundledSkillsTests
{
    [Fact]
    public void Simplify_is_bundled_and_parsed()
    {
        var simplify = BundledSkills.All.SingleOrDefault(s => s.Name == "simplify");
        Assert.NotNull(simplify);
        Assert.Equal(BundledSkills.SourceName, simplify!.Source);
        Assert.True(simplify.DescriptionDeclared);
        Assert.StartsWith(
            "Review the changed code for reuse, simplification, efficiency, and altitude cleanups",
            simplify.Description,
            StringComparison.Ordinal);
        Assert.Equal("[<target>]", simplify.ArgumentHint);
    }

    [Fact]
    public void The_body_is_the_skill_not_the_frontmatter()
    {
        var simplify = BundledSkills.All.Single(s => s.Name == "simplify");
        Assert.DoesNotContain("argument-hint:", simplify.Body, StringComparison.Ordinal);
        Assert.Contains("## Phase 0 — Gather the diff", simplify.Body, StringComparison.Ordinal);
        Assert.Contains("Launch **4 independent review agents** via the Agent tool",
            simplify.Body, StringComparison.Ordinal);
        Assert.EndsWith("(or confirm the code was already clean).", simplify.Body.TrimEnd(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_bundled_skill_has_no_file_path_to_resolve()
    {
        // A folder skill's "Base directory for this skill:" header is built from
        // FilePath; an embedded one has no directory, and must not claim one. The
        // reference's folder built-ins (dataviz, verify, claude-api) ship as
        // folders here precisely so that header names a real directory.
        Assert.NotEmpty(BundledSkills.All);
        foreach (var skill in BundledSkills.All)
        {
            if (skill.FilePath.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(Directory.Exists(skill.Directory), $"{skill.Name} claims a directory that is not there");
                Assert.True(File.Exists(skill.FilePath));
            }
            else
            {
                Assert.Equal("", skill.FilePath);
                Assert.Equal("", skill.Directory);
            }
        }
    }

    [Fact]
    public void Every_bundled_skill_declares_a_description()
    {
        Assert.NotEmpty(BundledSkills.All);
        foreach (var skill in BundledSkills.All)
        {
            Assert.True(skill.DescriptionDeclared, $"{skill.Name} has no description");
            Assert.False(string.IsNullOrWhiteSpace(skill.Description));
        }
    }

    [Fact]
    public void Loading_twice_returns_the_same_parse()
        => Assert.Same(BundledSkills.All, BundledSkills.All);
}
