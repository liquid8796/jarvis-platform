using System.IO;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Services;

/// <summary>
/// Third-party skill packs this build ships: whole folder skills, copied
/// verbatim from their upstream repositories and laid down beside the
/// executable.
/// </summary>
/// <remarks>
/// They are files on disk rather than embedded resources, which is the whole
/// difference from <see cref="BundledSkills"/>. A folder skill's instructions
/// name its own resources — "run scripts/with_server.py", "read
/// references/api.md", "the fonts in canvas-fonts/" — and Core answers that by
/// putting the folder's real path at the top of the rendered body and behind
/// <c>${CLAUDE_SKILL_DIR}</c>. An embedded copy has no path to hand over, so a
/// packed skill would arrive telling the model to open files that are not
/// anywhere; shipping the folder is what makes the instruction true.
///
/// A pack is one directory here and its name becomes the prefix on every skill
/// inside it (<c>anthropic/canvas-design</c> → <c>anthropic:canvas-design</c>),
/// which is <see cref="Skills.LoadDirectory"/>'s own nested-folder naming and
/// the reference's for plugin skills. The prefix is what keeps a pack from
/// quietly taking a name the user might want, and it is what the "/" menu
/// matches on part-wise, so <c>/canvas</c> still finds the skill.
///
/// They join the catalogue just ahead of the embedded ones, after everything
/// the user, the project and plugins provide, so a hand-written skill of the
/// same name still shadows a shipped one.
///
/// What is <em>in</em> a pack is a licensing question, not a taste one: only
/// work licensed to redistribute is vendored, and
/// <c>Assets/SkillPacks/THIRD-PARTY-NOTICES.txt</c> records each pack's origin
/// commit, its license, and the skills left behind with the reason.
/// </remarks>
internal static class SkillPacks
{
    /// <summary>The Source value a pack skill carries — the same badge a bundled skill shows.</summary>
    internal const string SourceName = BundledSkills.SourceName;

    /// <summary>The packs directory as deployed: Assets\SkillPacks beside the executable.</summary>
    internal static string RootDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "Assets", "SkillPacks");

    private static readonly Lazy<IReadOnlyList<SkillDefinition>> Cached = new(() => LoadFrom(RootDirectory));

    /// <summary>The shipped pack skills, parsed once.</summary>
    internal static IReadOnlyList<SkillDefinition> All => Cached.Value;

    /// <summary>
    /// The packs under <paramref name="root"/>, in name order. Parameterized so
    /// a test can read the repository's own copy: content flows to an app build,
    /// but a test host is not the app.
    /// </summary>
    internal static IReadOnlyList<SkillDefinition> LoadFrom(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. Skills.LoadDirectory(root, SourceName)
            .Select(static skill => skill with { Author = Attribution(skill) })
            .OrderBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Who the Skills page credits. Core defaults an undeclared author to "You",
    /// which is right for a skill the user wrote and a small untruth on one that
    /// was vendored, so a pack skill is credited to its pack — the folder is
    /// named for whoever published it, which keeps this correct for a pack
    /// nobody has added yet.
    /// </summary>
    private static string Attribution(SkillDefinition skill)
    {
        if (PackOf(skill) is not { Length: > 0 } pack)
        {
            return skill.Author;
        }

        return char.ToUpperInvariant(pack[0]) + pack[1..];
    }

    /// <summary>The pack a skill came from ("anthropic"), or null for a name carrying no prefix.</summary>
    internal static string? PackOf(SkillDefinition skill)
    {
        var separator = skill.Name.IndexOf(':');
        return separator > 0 ? skill.Name[..separator] : null;
    }
}
