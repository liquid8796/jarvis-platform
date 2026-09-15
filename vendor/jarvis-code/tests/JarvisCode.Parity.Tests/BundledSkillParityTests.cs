using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JarvisCode.App.Services;
using JarvisCode.Core.Customization;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The skills this build ships are the reference's own text, and this pins them.
/// </summary>
/// <remarks>
/// Two kinds, two checks. A skill whose body the reference holds in its string
/// table (<c>/simplify</c>) is searched for line by line in the installed CLI's
/// bytes. A skill the reference ships zipped — <c>dataviz</c>, <c>verify</c> and
/// <c>claude-api</c> with their resource folders, and <c>workflow-authoring</c>,
/// whose body a byte search finds only in fragments — is pinned against a
/// <b>recording</b> instead: the body the CLI sent when the skill was invoked
/// against a loopback listener (<c>claude -p "/dataviz" --model claude-opus-5
/// --permission-mode plan</c>, 2.1.257), stored under <c>Captures/Skills/</c>
/// with the "Base directory for this skill:" header stripped, since this app's
/// renderer adds its own. The resource files beside a body are compared, when
/// this machine holds the reference's own extraction of them, against
/// <c>%LOCALAPPDATA%\Temp\claude\bundled-skills\{version}\{hash}\{name}</c> —
/// the folder the CLI unpacks a bundled skill into when it is invoked.
/// </remarks>
public class BundledSkillParityTests
{
    private static readonly Regex Skippable = new(
        @"^\s*(?:```|\||#{1,6}\s|-{3,}\s*$)", RegexOptions.Compiled);

    private const int MinimumLength = 40;

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Captures", "Skills");

    /// <summary>The skills with a recorded body.</summary>
    private static IReadOnlyDictionary<string, string> Recorded() =>
        Directory.Exists(FixtureDirectory)
            ? Directory.EnumerateFiles(FixtureDirectory, "*.md")
                .ToDictionary(
                    static f => Path.GetFileNameWithoutExtension(f),
                    static f => File.ReadAllText(f).Replace("\r\n", "\n", StringComparison.Ordinal),
                    StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    [ReferenceCliFact]
    public void Every_line_of_every_string_table_skill_is_in_the_reference()
    {
        Assert.NotEmpty(BundledSkills.All);
        var corpus = ReferenceCorpora.Cli;
        var recorded = Recorded();
        var missing = new List<string>();
        foreach (var skill in BundledSkills.All.Where(s => !recorded.ContainsKey(s.Name)))
        {
            foreach (var line in Lines(skill.Body).Concat(Lines(skill.Description)))
            {
                if (!corpus.ContainsAllowingInterpolation(line))
                {
                    missing.Add($"{skill.Name}: {Truncate(line)}");
                }
            }
        }

        Assert.True(missing.Count == 0, new StringBuilder()
            .AppendLine($"{missing.Count} line(s) of the bundled skills are not in the installed CLI " +
                        $"{ReferenceInstall.CliVersion}:")
            .AppendLine(string.Join("\n", missing.Select(static m => "  " + m)))
            .AppendLine()
            .AppendLine("A bundled skill is the reference's own text. Re-record it (see the class")
            .AppendLine("remarks) rather than editing it here.")
            .ToString());
    }

    [Fact]
    public void Every_recorded_skill_is_shipped_with_the_recorded_body()
    {
        var recorded = Recorded();
        Assert.NotEmpty(recorded);
        foreach (var (name, body) in recorded)
        {
            var skill = BundledSkills.All.SingleOrDefault(s => s.Name == name);
            Assert.True(skill is not null, $"the recorded skill '{name}' is not bundled");
            Assert.Equal(body.TrimEnd(), skill!.Body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
        }
    }

    /// <summary>
    /// The reference lists a skill's description in the harness's skill listing;
    /// these are the ones it sent (workflow-authoring, dataviz, claude-api from a
    /// 2.1.257 listing; verify from the desktop's guide-agent configuration, the
    /// listing itself omitting it as user-invocable only).
    /// </summary>
    [Theory]
    [InlineData("dataviz", "Use this skill whenever you are about to create ANY chart, graph, plot, dashboard, or data visualization")]
    [InlineData("verify", "Verify that a code change actually does what it's supposed to by exercising it end-to-end")]
    [InlineData("claude-api", "Reference for the Claude API / Anthropic SDK")]
    [InlineData("workflow-authoring", "Reference for writing a Workflow tool script")]
    public void Recorded_skills_carry_the_reference_description(string name, string opening)
    {
        var skill = BundledSkills.All.Single(s => s.Name == name);
        Assert.StartsWith(opening, skill.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_is_user_invocable_only_as_the_reference_lists_it()
    {
        var verify = BundledSkills.All.Single(s => s.Name == "verify");
        Assert.True(verify.DisableModelInvocation, "verify is listed to the user and withheld from the model");
    }

    /// <summary>
    /// A folder skill names its own files; every one it names must be beside it,
    /// or the model is told to open something that is nowhere.
    /// </summary>
    [Fact]
    public void Folder_skill_references_resolve()
    {
        foreach (var skill in BundledSkills.All.Where(IsFolderSkill))
        {
            foreach (Match match in Regex.Matches(skill.Body, @"(?<![\w/.])((?:references|scripts|examples)/[\w./-]+)"))
            {
                var relative = match.Groups[1].Value.TrimEnd('.', ',', ')');
                var path = Path.Combine(skill.Directory, relative.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path) || Directory.Exists(path), $"{skill.Name} names {relative}, which is not beside it");
            }
        }
    }

    /// <summary>
    /// The resource files are the reference's own bytes: when this machine holds
    /// the CLI's extraction of a bundled skill, every file must match it, both
    /// ways. Skipped where the reference has not unpacked that skill here.
    /// </summary>
    [ReferenceCliFact]
    public void Folder_skill_resources_match_the_reference_extraction()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "claude", "bundled-skills", ReferenceInstall.CliVersion ?? "");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var skill in BundledSkills.All.Where(IsFolderSkill))
        {
            var extracted = Directory.EnumerateDirectories(root)
                .Select(hash => Path.Combine(hash, skill.Name))
                .Where(Directory.Exists)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (extracted is null)
            {
                continue;
            }

            var theirs = Relative(extracted);
            var ours = Relative(skill.Directory).Where(static p => p != "SKILL.md").ToList();
            Assert.Equal(theirs.OrderBy(static p => p, StringComparer.Ordinal), ours.OrderBy(static p => p, StringComparer.Ordinal));
            foreach (var file in theirs)
            {
                Assert.True(
                    File.ReadAllBytes(Path.Combine(extracted, file)).AsSpan()
                        .SequenceEqual(File.ReadAllBytes(Path.Combine(skill.Directory, file))),
                    $"{skill.Name}/{file} differs from the reference's extraction");
            }
        }
    }

    [Fact]
    public void A_bundled_skill_only_asks_for_machinery_this_app_has()
    {
        // The reference's other built-ins are written for its own environment;
        // the ones bundled here must not name any of it, or the model would be
        // told to use something that is not there.
        string[] foreignMachinery =
        [
            ".claude/settings.json", "~/.claude/projects", "apt-get", "xvfb", "tmux",
            "chromium-cli", "/run-skill-generator",
        ];
        // A recorded skill is the reference's text by contract (verify's table of
        // verification surfaces names xvfb as one way to drive a GUI); the check
        // is for the skills this port assembled itself.
        var recorded = Recorded();
        foreach (var skill in BundledSkills.All.Where(s => !recorded.ContainsKey(s.Name)))
        {
            foreach (var foreign in foreignMachinery)
            {
                Assert.False(
                    skill.Body.Contains(foreign, StringComparison.OrdinalIgnoreCase),
                    $"bundled skill '{skill.Name}' tells the model to use {foreign}, which this app " +
                    "does not have");
            }
        }
    }

    /// <summary>A skill shipped as a folder (SKILL.md plus resources) rather than one embedded file.</summary>
    private static bool IsFolderSkill(SkillDefinition skill) =>
        skill.FilePath.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase) && skill.Directory.Length > 0;

    private static IReadOnlyList<string> Relative(string directory) =>
        [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/'))];

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r\n", "\n")
            .Split('\n')
            .Select(static l => l.Trim())
            .Where(static l => l.Length >= MinimumLength && !Skippable.IsMatch(l));

    private static string Truncate(string line) =>
        line.Length <= 110 ? line : line[..110] + "…";
}
