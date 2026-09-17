using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Plugins;

namespace Jarvis.Core.Tests;

public sealed class PluginSkillLoaderTests
{
    [Fact]
    public void Discover_reads_bounded_skill_metadata_without_loading_full_instructions()
    {
        var fixture = SkillFixture.Create("frontend-testing", "Rendered frontend QA", "# Frontend testing\nAlways inspect the rendered page before completion.");
        try
        {
            var loader = new PluginSkillLoader();
            var catalog = loader.Discover(fixture.Snapshot);

            var skill = Assert.Single(catalog.Skills);
            Assert.Equal("demo", skill.PluginId);
            Assert.Equal("frontend-testing", skill.Name);
            Assert.Equal("Rendered frontend QA", skill.Description);
            Assert.EndsWith("SKILL.md", skill.Path, StringComparison.OrdinalIgnoreCase);
            Assert.True(skill.Length > 0);
            Assert.DoesNotContain("Always inspect", skill.Description, StringComparison.Ordinal);
            Assert.Empty(catalog.Diagnostics);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public void Load_selected_returns_full_instructions_only_for_requested_skills()
    {
        var fixture = SkillFixture.Create("frontend-testing", "Rendered frontend QA", "# Frontend testing\nAlways inspect the rendered page before completion.");
        try
        {
            var loader = new PluginSkillLoader();
            var catalog = loader.Discover(fixture.Snapshot);

            var none = loader.LoadSelected(fixture.Snapshot, catalog.Skills, []);
            var selected = loader.LoadSelected(fixture.Snapshot, catalog.Skills, ["demo/frontend-testing"]);

            Assert.Empty(none);
            var document = Assert.Single(selected);
            Assert.Equal("frontend-testing", document.Descriptor.Name);
            Assert.Contains("Always inspect the rendered page", document.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("description:", document.Instructions, StringComparison.OrdinalIgnoreCase);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public void Discover_does_not_decode_full_skill_body_until_selected()
    {
        var fixture = SkillFixture.Create("lazy", "Lazy metadata", "placeholder");
        var path = Path.Combine(fixture.Root, "plugin", "skills", "lazy", "SKILL.md");
        var header = System.Text.Encoding.UTF8.GetBytes("---\nname: lazy\ndescription: Lazy metadata\n---\n");
        File.WriteAllBytes(path, header.Concat(new byte[] { 0xC3, 0x28 }).ToArray());
        try
        {
            var loader = new PluginSkillLoader();
            var catalog = loader.Discover(fixture.Snapshot);

            var skill = Assert.Single(catalog.Skills);
            Assert.Equal("lazy", skill.Name);
            Assert.Throws<System.Text.DecoderFallbackException>(() => loader.Load(fixture.Snapshot, skill));
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public void Oversized_skill_is_isolated_as_diagnostic_and_not_exposed()
    {
        var fixture = SkillFixture.Create("huge", "Huge skill", new string('x', PluginSkillLoader.MaxSkillBytes + 1));
        try
        {
            var loader = new PluginSkillLoader();
            var catalog = loader.Discover(fixture.Snapshot);

            Assert.Empty(catalog.Skills);
            var diagnostic = Assert.Single(catalog.Diagnostics);
            Assert.Equal("demo", diagnostic.PluginId);
            Assert.Contains("size", diagnostic.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public void Paths_outside_validated_skill_roots_are_rejected_on_load()
    {
        var fixture = SkillFixture.Create("safe", "Safe skill", "Safe instructions");
        var outside = Path.Combine(fixture.Root, "outside-SKILL.md");
        File.WriteAllText(outside, "outside");
        try
        {
            var loader = new PluginSkillLoader();
            var forged = new PluginSkillDescriptor("demo", "forged", "forged", outside, new FileInfo(outside).Length);

            Assert.Throws<UnauthorizedAccessException>(() => loader.Load(fixture.Snapshot, forged));
        }
        finally { fixture.Dispose(); }
    }

    private sealed class SkillFixture : IDisposable
    {
        public required string Root { get; init; }
        public required PluginCatalogSnapshot Snapshot { get; init; }

        public static SkillFixture Create(string name, string description, string body)
        {
            var root = Path.Combine(Path.GetTempPath(), "jarvis-skill-" + Guid.NewGuid().ToString("N"));
            var pluginRoot = Path.Combine(root, "plugin");
            var skillRoot = Path.Combine(pluginRoot, "skills");
            var skillDir = Path.Combine(skillRoot, name);
            Directory.CreateDirectory(skillDir);
            var markdown = $"---\nname: {name}\ndescription: {description}\n---\n{body}";
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), markdown);
            return new SkillFixture
            {
                Root = root,
                Snapshot = new PluginCatalogSnapshot(
                    [new PluginManifest { Id = "demo", Version = "1.0.0" }],
                    new Dictionary<string, IAgentTool>(),
                    new Dictionary<string, IReadOnlyList<string>>())
                {
                    SkillRoots = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                    {
                        ["demo"] = [skillRoot]
                    }
                }
            };
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
