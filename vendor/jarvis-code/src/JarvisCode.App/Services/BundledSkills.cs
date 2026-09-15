using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Services;

/// <summary>
/// Skills this build ships with, embedded in the assembly.
/// </summary>
/// <remarks>
/// The reference carries its built-ins inside the CLI binary; this app carries
/// its own as embedded markdown, parsed by the same frontmatter reader that
/// reads a user's skill from disk, so a bundled skill and a hand-written one
/// behave identically once loaded.
///
/// They are added <em>last</em> in <see cref="SkillCatalog"/>, so a user,
/// project or plugin skill of the same name shadows the bundled one — the
/// reference's own find-first order.
///
/// Only skills whose instructions are true of <em>this</em> app are bundled. Most
/// of the reference's built-ins are written for its own environment (apt-get and
/// xvfb, `.claude/settings.json`, its own transcript directory, folder-skill
/// resources it ships beside them), and a skill that tells the model to use
/// machinery this app does not have is worse than no skill; those are declared
/// in the parity manifest instead.
/// </remarks>
internal static class BundledSkills
{
    /// <summary>The Source value a bundled skill carries.</summary>
    internal const string SourceName = "built-in";

    private const string ResourcePrefix = "JarvisCode.App.Assets.Skills.";

    private static readonly Lazy<IReadOnlyList<SkillDefinition>> Cached = new(Load);

    /// <summary>The bundled skills, parsed once.</summary>
    internal static IReadOnlyList<SkillDefinition> All => Cached.Value;

    private static IReadOnlyList<SkillDefinition> Load()
    {
        var assembly = typeof(BundledSkills).Assembly;
        var skills = new List<SkillDefinition>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                                        && name.EndsWith(".md", StringComparison.Ordinal))
                     .OrderBy(static name => name, StringComparer.Ordinal))
        {
            var name = resource[ResourcePrefix.Length..^".md".Length];
            if (Read(assembly, resource) is { } text)
            {
                skills.Add(Parse(name, text));
            }
        }

        // The reference's built-ins that ship resource files beside their body
        // (dataviz's references/ and scripts/, verify's examples/, claude-api's
        // per-language folders) are content files rather than embedded resources,
        // for the reason SkillPacks gives: a folder skill's body names its own
        // files, and Core answers that with the folder's real path.
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "BundledSkills");
        if (Directory.Exists(folder))
        {
            skills.AddRange(Skills.LoadDirectory(folder, SourceName));
        }

        return skills;
    }

    private static string? Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Frontmatter and body, through Core's own reader so a bundled skill
    /// supports exactly what a file on disk supports.
    /// </summary>
    private static SkillDefinition Parse(string name, string text)
    {
        var parsed = Frontmatter.ParseRich(text);
        return new SkillDefinition(
            Name: name,
            Description: parsed.Fields.TryGetValue("description", out var description) ? description : "",
            Author: "Jarvis",
            Body: parsed.Body,
            // No path: a bundled skill has no file, and the base-directory
            // header a folder skill gets would name a directory that is not there.
            FilePath: "",
            LastUpdated: DateTimeOffset.MinValue,
            Source: SourceName)
        {
            DescriptionDeclared = parsed.Fields.ContainsKey("description"),
            WhenToUse = parsed.Fields.TryGetValue("when-to-use", out var whenToUse) ? whenToUse : null,
            ArgumentHint = parsed.Fields.TryGetValue("argument-hint", out var hint) ? hint : null,
        };
    }
}
