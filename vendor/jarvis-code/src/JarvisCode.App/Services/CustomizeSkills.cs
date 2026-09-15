using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Services;

/// <summary>The sort orders the Skills list offers, in the reference's spellings.</summary>
public enum SkillSort
{
    /// <summary>"updated" — Last edited, the default.</summary>
    Updated,

    /// <summary>"name".</summary>
    Name,

    /// <summary>"plugin" — Plugin name; offered only while plugin rows are listed.</summary>
    Plugin,

    /// <summary>"most_used_by_me"; offered only while some row carries a usage count.</summary>
    MostUsedByMe,
}

/// <summary>Who a skill row is credited to — the reference's createdBy facet (its Wi).</summary>
public enum SkillCreator
{
    You,
    Anthropic,
    Partners,
    Org,
}

/// <summary>The "Filter by" state of the Skills list: a creator and a plugin, both optional.</summary>
public sealed record SkillFilter(SkillCreator? CreatedBy, string? PluginId)
{
    public static readonly SkillFilter None = new(null, null);

    /// <summary>How many facets are set — the reference's ga, which the reset control reads.</summary>
    public int ActiveCount => (CreatedBy is null ? 0 : 1) + (PluginId is null ? 0 : 1);
}

/// <summary>The three kinds of row the reference's tabbed list mixes in one column.</summary>
public enum SkillRowKind
{
    /// <summary>A skill in the user's own library.</summary>
    Skill,

    /// <summary>A built-in skill the build ships.</summary>
    BuiltIn,

    /// <summary>A skill an installed plugin carries.</summary>
    Plugin,
}

/// <summary>One row of the Skills list, with everything the sort and the filter read.</summary>
public sealed record SkillRow(
    SkillRowKind Kind,
    SkillDefinition Skill,
    DateTimeOffset? UpdatedAt,
    string? PluginName,
    string? PluginId,
    int? OwnUses90d,
    SkillCreator CreatedBy,
    bool Enabled)
{
    /// <summary>The name the row prints: a plugin skill without its "plugin:" prefix.</summary>
    public string Name => Kind == SkillRowKind.Plugin && PluginName is not null &&
        Skill.Name.StartsWith(PluginName + ":", StringComparison.OrdinalIgnoreCase)
            ? Skill.Name[(PluginName.Length + 1)..]
            : Skill.Name;

    /// <summary>The reference's kindRank: library skills, then built-ins, then plugin rows.</summary>
    public int KindRank => Kind switch
    {
        SkillRowKind.Skill => 0,
        SkillRowKind.BuiltIn => 1,
        _ => 2,
    };

    /// <summary>Only a skill in the user's own directories can be edited or removed here.</summary>
    public bool IsEditable =>
        Kind == SkillRowKind.Skill && Skill.Source is Skills.UserSource or Skills.ProjectSource;
}

/// <summary>The Skills list split three ways, the reference's attention / main / createdByYou.</summary>
public sealed record SkillSections(
    IReadOnlyList<SkillRow> Attention,
    IReadOnlyList<SkillRow> Main,
    IReadOnlyList<SkillRow> CreatedByYou);

/// <summary>
/// The rules of the reference's tabbed Skills list (c5e558aae <c>Fi</c>): how
/// rows are credited (<c>Wi</c>), split into sections (<c>Me</c>), filtered
/// (<c>wa</c>), searched (<c>ls</c>/<c>is</c>) and sorted (<c>qi</c>/<c>Gi</c>).
/// Pure, so the tests can pin the order without a window.
/// </summary>
public static class SkillListPresentation
{
    /// <summary>The window the "runs by you" count covers — the reference's ownUses90d.</summary>
    public static readonly TimeSpan OwnUsesWindow = TimeSpan.FromDays(90);

    /// <summary>The reference's search cap on the Skills and Plugins boxes.</summary>
    public const int SearchMaxLength = 200;

    /// <summary>
    /// The reference's <c>Wi</c>: a skill the user made is "you", Anthropic's are
    /// "anthropic", everything else is the organization's. Here the user's own
    /// directories, the project's and the legacy commands are "you"; the build's
    /// own skills are Anthropic's except the packs another vendor published, which
    /// are partners'; and a plugin's skills take the plugin's provenance.
    /// </summary>
    public static SkillCreator CreatorOf(SkillDefinition skill, SkillCreator? pluginCreator = null)
    {
        if (skill.Source.StartsWith("plugin", StringComparison.OrdinalIgnoreCase))
            return pluginCreator ?? SkillCreator.Org;
        if (string.Equals(skill.Source, BundledSkills.SourceName, StringComparison.OrdinalIgnoreCase))
        {
            var prefix = skill.Name.IndexOf(':');
            if (prefix > 0 && !skill.Name[..prefix].Equals("anthropic", StringComparison.OrdinalIgnoreCase))
                return SkillCreator.Partners;
            return SkillCreator.Anthropic;
        }
        return SkillCreator.You;
    }

    /// <summary>The row kind a source implies.</summary>
    public static SkillRowKind KindOf(SkillDefinition skill) =>
        skill.Source.StartsWith("plugin", StringComparison.OrdinalIgnoreCase) ? SkillRowKind.Plugin
        : string.Equals(skill.Source, BundledSkills.SourceName, StringComparison.OrdinalIgnoreCase) ? SkillRowKind.BuiltIn
        : SkillRowKind.Skill;

    /// <summary>The plugin a "plugin:{name}" source names, or null.</summary>
    public static string? PluginOf(SkillDefinition skill) =>
        skill.Source.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase) ? skill.Source["plugin:".Length..] : null;

    /// <summary>
    /// The usage count the "Most used by me" sort reads: this app keeps one
    /// lifetime counter per skill with the time it was last used, so a count
    /// counts only while the last use is inside the 90-day window.
    /// </summary>
    public static int? OwnUses(SkillUsageEntry? usage, DateTimeOffset now) =>
        usage is { UsageCount: > 0 } && now - usage.LastUsedAt <= OwnUsesWindow ? usage.UsageCount : null;

    /// <summary>Builds one row from a loaded skill and the session's stores.</summary>
    public static SkillRow ToRow(SkillDefinition skill, UiSettings ui, DateTimeOffset now)
    {
        var kind = KindOf(skill);
        var plugin = PluginOf(skill);
        ui.SkillUsage.TryGetValue(skill.Name, out var usage);
        return new SkillRow(
            kind,
            skill,
            kind == SkillRowKind.Skill && skill.LastUpdated > DateTimeOffset.MinValue ? skill.LastUpdated : null,
            plugin,
            plugin,
            OwnUses(usage, now),
            CreatorOf(skill),
            !string.Equals(
                SkillCatalog.OverrideFor(ui, skill.Name), SkillCatalog.OverrideOff, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The reference's Me: the user's own last, attention first, the rest between.</summary>
    public static SkillSections Split(IEnumerable<SkillRow> rows, Func<SkillRow, bool>? needsAttention = null)
    {
        var attention = new List<SkillRow>();
        var main = new List<SkillRow>();
        var created = new List<SkillRow>();
        foreach (var row in rows)
        {
            if (row.Kind == SkillRowKind.Skill && row.CreatedBy == SkillCreator.You)
                created.Add(row);
            else if (needsAttention is not null && needsAttention(row))
                attention.Add(row);
            else
                main.Add(row);
        }
        return new SkillSections(attention, main, created);
    }

    /// <summary>The reference's wa: a facet that is set must match.</summary>
    public static bool Matches(SkillFilter filter, SkillRow row)
    {
        if (filter.CreatedBy is { } creator && row.CreatedBy != creator)
            return false;
        if (filter.PluginId is { } plugin && !string.Equals(row.PluginId, plugin, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>
    /// The reference's search (<c>ls</c>/<c>is</c>): a library or built-in row
    /// matches on its name, a plugin row on "{skill} {plugin}", case-insensitively.
    /// </summary>
    public static IReadOnlyList<SkillRow> Search(IEnumerable<SkillRow> rows, string query)
    {
        var needle = query.Trim().ToLowerInvariant();
        if (needle.Length == 0)
            return [.. rows];
        return [.. rows.Where(row => row.Kind == SkillRowKind.Plugin
            ? $"{row.Name} {row.PluginName}".ToLowerInvariant().Contains(needle)
            : row.Name.ToLowerInvariant().Contains(needle))];
    }

    /// <summary>
    /// A sort whose option is not offered falls back to Last edited (the
    /// reference's Te): Plugin name needs plugin rows, Most used needs a count.
    /// </summary>
    public static SkillSort EffectiveSort(SkillSort requested, bool havePluginRows, bool haveOwnUses) =>
        requested == SkillSort.Plugin && !havePluginRows ? SkillSort.Updated
        : requested == SkillSort.MostUsedByMe && !haveOwnUses ? SkillSort.Updated
        : requested;

    /// <summary>The reference's qi: the comparator each sort order uses.</summary>
    public static int Compare(SkillSort sort, SkillRow a, SkillRow b)
    {
        switch (sort)
        {
            case SkillSort.Name:
                return Names(a, b);
            case SkillSort.Plugin:
                if (string.Equals(a.PluginId, b.PluginId, StringComparison.Ordinal))
                    return Names(a, b);
                if (a.PluginName is null || a.PluginId is null)
                    return 1;
                if (b.PluginName is null || b.PluginId is null)
                    return -1;
                var plugin = string.Compare(a.PluginName, b.PluginName, StringComparison.CurrentCultureIgnoreCase);
                return plugin != 0 ? plugin : string.CompareOrdinal(a.PluginId, b.PluginId);
            case SkillSort.MostUsedByMe:
                var uses = (b.OwnUses90d ?? -1) - (a.OwnUses90d ?? -1);
                return uses != 0 ? uses : ByUpdated(a, b);
            default:
                return ByUpdated(a, b);
        }
    }

    /// <summary>The reference's Gi: newest first, undated last, ties by kind then name.</summary>
    private static int ByUpdated(SkillRow a, SkillRow b)
    {
        if (a.UpdatedAt == b.UpdatedAt)
        {
            var kind = a.KindRank - b.KindRank;
            return kind != 0 ? kind : Names(a, b);
        }
        if (a.UpdatedAt is null)
            return 1;
        if (b.UpdatedAt is null)
            return -1;
        return b.UpdatedAt.Value.CompareTo(a.UpdatedAt.Value);
    }

    private static int Names(SkillRow a, SkillRow b) =>
        string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>The sort menu's rows, in the reference's order and with its gates.</summary>
    public static IReadOnlyList<(SkillSort Value, string Label)> SortOptions(bool havePluginRows, bool haveOwnUses)
    {
        var options = new List<(SkillSort, string)>
        {
            (SkillSort.Updated, "Last edited"),
            (SkillSort.Name, "Name"),
        };
        if (havePluginRows)
            options.Add((SkillSort.Plugin, "Plugin name"));
        if (haveOwnUses)
            options.Add((SkillSort.MostUsedByMe, "Most used by me"));
        return options;
    }

    /// <summary>
    /// The credit under the name: the reference prints "by you" for the user's
    /// own and "by {author}" for everything else that names one.
    /// </summary>
    public static string? Credit(SkillRow row)
    {
        if (row.CreatedBy == SkillCreator.You)
            return "by you";
        var author = row.Kind == SkillRowKind.Plugin && row.PluginName is { Length: > 0 } plugin
            ? plugin
            : row.CreatedBy == SkillCreator.Anthropic ? "Anthropic" : row.Skill.Author;
        return string.IsNullOrWhiteSpace(author) || author == "You" ? null : $"by {author}";
    }

    /// <summary>The reference's Author column: Anthropic, "Your admin", or "You".</summary>
    public static string AuthorCell(SkillRow row) => row.CreatedBy switch
    {
        SkillCreator.Anthropic => "Anthropic",
        SkillCreator.You => "You",
        _ => "Your admin",
    };

    /// <summary>"{n} run" / "{n} runs" — the ownRuns reading of a count.</summary>
    public static string RunsLabel(int count) => count == 1 ? "1 run" : $"{count:N0} runs";
}

/// <summary>
/// The skill editor's rules (c5e558aae <c>mi</c>, <c>oi</c>, <c>ri</c>,
/// <c>ui</c> and the helpers it imports from c8544f173): the name sanitizer,
/// the reserved words, the description limits, the frontmatter the editor
/// writes, and the version label under the editor.
/// </summary>
public static class SkillEditorRules
{
    /// <summary>The reference's maxLength on the name box.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Descriptions past this fail; the counter appears at <see cref="DescriptionCounterFrom"/>.</summary>
    public const int MaxDescriptionLength = 1024;

    /// <summary>The counter "{current}/{max}" shows from 900 characters on.</summary>
    public const int DescriptionCounterFrom = 900;

    /// <summary>The words a skill name may not contain (the reference's ri).</summary>
    public static readonly IReadOnlyList<string> ReservedWords = ["anthropic", "claude"];

    /// <summary>The name shape a saved skill must have.</summary>
    public static readonly Regex ValidName = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    private static readonly Regex XmlTag = new("[<﹤＜].+[>﹥＞]", RegexOptions.Compiled);

    private static readonly Regex Separator = new(@"[\s\p{Pc}]", RegexOptions.Compiled);

    private static readonly Regex NameCharacter =
        new(@"[\p{Lu}\p{Ll}\p{Lt}\p{Lm}\p{Lo}\p{Nl}\p{Mn}\p{Mc}\p{Nd}-]", RegexOptions.Compiled);

    /// <summary>
    /// The reference's oi, applied to every keystroke: whitespace and connector
    /// punctuation become hyphens, letters, digits, marks, hyphens and the two
    /// zero-width joiners stay, everything else is dropped, and the result is
    /// lower-cased and cut at 64.
    /// </summary>
    public static string SanitizeName(string typed)
    {
        var builder = new StringBuilder();
        foreach (var rune in typed.EnumerateRunes())
        {
            var text = rune.ToString();
            if (Separator.IsMatch(text))
                builder.Append('-');
            else if (NameCharacter.IsMatch(text) || rune.Value is 0x200C or 0x200D)
                builder.Append(text);
        }
        var lowered = builder.ToString().ToLowerInvariant();
        return lowered.Length <= MaxNameLength ? lowered : lowered[..MaxNameLength];
    }

    /// <summary>The reserved word a name contains, or null.</summary>
    public static string? ReservedWordIn(string name)
    {
        var lowered = name.ToLowerInvariant();
        return ReservedWords.FirstOrDefault(lowered.Contains);
    }

    /// <summary>The reference's message for a name carrying a reserved word.</summary>
    public static string ReservedWordError(string word) => $"Name cannot contain the word “{word}”.";

    /// <summary>The reference's ve: over the limit.</summary>
    public static bool DescriptionTooLong(string description) => description.Length > MaxDescriptionLength;

    /// <summary>The reference's ye: something that reads as an XML tag.</summary>
    public static bool DescriptionHasXml(string description) => XmlTag.IsMatch(description);

    /// <summary>Whether the counter shows for this length.</summary>
    public static bool ShowsCounter(string description) => description.Length >= DescriptionCounterFrom;

    /// <summary>
    /// The reference's ze: the save button's gate. A name that is blank or
    /// reserved, or a description that is blank, too long or tagged, blocks the
    /// save with "Add a valid name and description before saving.".
    /// </summary>
    public static bool NameAndDescriptionInvalid(string name, string description) =>
        name.Trim().Length == 0 ||
        ReservedWordIn(name) is not null ||
        description.Trim().Length == 0 ||
        DescriptionTooLong(description) ||
        DescriptionHasXml(description);

    /// <summary>The reference's ra: the instructions open with a frontmatter fence.</summary>
    public static bool StartsWithFrontmatter(string instructions) =>
        Regex.IsMatch(instructions, @"^﻿?(?:[ \t]*\r?\n)*---[ \t]*\r?\n");

    /// <summary>
    /// The reference's ui: the SKILL.md a created skill is written as. Name and
    /// description are JSON-quoted, which is valid YAML and survives any
    /// character the user typed.
    /// </summary>
    public static string ComposeSkillMd(string name, string description, string instructions) =>
        $"---\nname: {JsonSerializer.Serialize(name)}\ndescription: {JsonSerializer.Serialize(description)}\n---\n\n{instructions}";

    /// <summary>
    /// The reference's <c>ta</c> (c8544f173): rewrite the description line of an
    /// existing SKILL.md's frontmatter, or add one; null when the file has no
    /// frontmatter, which the editor reports as "SKILL.md is missing its
    /// frontmatter…".
    /// </summary>
    public static string? WithDescription(string skillMd, string description)
    {
        var split = SplitFrontmatter(skillMd);
        if (split is null)
            return null;
        var (head, frontmatter, tail) = split.Value;
        var line = $"description: {JsonSerializer.Serialize(description)}";
        var existing = new Regex(@"^description[ \t]*:[^\n]*(?:(?:\n[ \t]*)*\n[ \t]+\S[^\n]*)*", RegexOptions.Multiline);
        var updated = existing.IsMatch(frontmatter)
            ? existing.Replace(frontmatter, line.Replace("$", "$$"), 1)
            : $"{frontmatter}\n{line}";
        return head + updated + tail;
    }

    /// <summary>Head, frontmatter and tail of a file that has one (the reference's l).</summary>
    public static (string Head, string Frontmatter, string Tail)? SplitFrontmatter(string text)
    {
        var match = Regex.Match(text, @"^(﻿?(?:[ \t]*\r?\n)*---[ \t]*\r?\n)([\s\S]*?)(?=\r?\n---[ \t]*(?:\r?\n|$))");
        if (!match.Success)
            return null;
        var head = match.Groups[1].Value;
        var frontmatter = match.Groups[2].Value;
        return (head, frontmatter, text[(head.Length + frontmatter.Length)..]);
    }

    /// <summary>The body under the frontmatter, as the editor shows it.</summary>
    public static string BodyOf(string skillMd)
    {
        var split = SplitFrontmatter(skillMd);
        if (split is null)
            return skillMd;
        var tail = split.Value.Tail;
        var fence = Regex.Match(tail, @"^\r?\n---[^\n]*\r?\n?");
        return fence.Success ? tail[fence.Length..].TrimStart('\r', '\n') : tail;
    }

    /// <summary>
    /// The reference's Qe: what the editor's footer says about the version.
    /// Editing with unsaved changes says so; a saved version prints
    /// "v{n} · {date}"; a skill with a date but no version says "Saved {date}",
    /// one with neither "Current version"; and a new skill is a "Draft".
    /// </summary>
    public static string VersionLabel(bool editing, bool dirty, int? version, DateTimeOffset? updatedAt)
    {
        if (!editing)
            return "Draft";
        if (dirty)
            return "Unsaved changes";
        var date = updatedAt?.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
        if (version is { } n)
            return date is null ? $"v{n}" : $"v{n} · {date}";
        return date is null ? "Current version" : $"Saved {date}";
    }
}

/// <summary>One saved version of a skill, as the version history lists it.</summary>
public sealed record SkillVersionEntry(int Version, DateTimeOffset SavedAt, string Path)
{
    /// <summary>The row the version list prints: the reference's "{version} · {date}".</summary>
    public string Label => $"v{Version} · {SavedAt.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture)}";
}

/// <summary>
/// Version history for the user's own skills. The reference keeps versions on
/// its backend and the desktop reads the head version number; nothing on disk
/// in a Claude Code skill folder holds one, so this app keeps its snapshots
/// beside its profile — <c>skill-versions/{skill}/v{n}/SKILL.md</c> plus an
/// index — where they never reach the model and never ship with the skill.
/// </summary>
public sealed class SkillVersions(string root)
{
    private sealed class Index
    {
        public List<Entry> Versions { get; set; } = [];
    }

    private sealed class Entry
    {
        public int Version { get; set; }
        public DateTimeOffset SavedAt { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The directory the snapshots live under, beside ui-settings.json.</summary>
    public const string DirectoryName = "skill-versions";

    public string Root { get; } = root;

    private string FolderFor(string skillName) => Path.Combine(Root, Safe(skillName));

    private static string Safe(string name) =>
        new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());

    /// <summary>The versions saved so far, newest first — the order the list prints.</summary>
    public IReadOnlyList<SkillVersionEntry> List(string skillName)
    {
        var index = Load(skillName);
        return [.. index.Versions
            .OrderByDescending(v => v.Version)
            .Select(v => new SkillVersionEntry(
                v.Version, v.SavedAt, Path.Combine(FolderFor(skillName), $"v{v.Version}", "SKILL.md")))];
    }

    /// <summary>The newest version number, or null before the first save.</summary>
    public int? Head(string skillName)
    {
        var index = Load(skillName);
        return index.Versions.Count == 0 ? null : index.Versions.Max(v => v.Version);
    }

    /// <summary>Snapshots a SKILL.md as the next version and returns its number.</summary>
    public int Save(string skillName, string skillMd, DateTimeOffset? at = null)
    {
        var index = Load(skillName);
        var next = (index.Versions.Count == 0 ? 0 : index.Versions.Max(v => v.Version)) + 1;
        var folder = Path.Combine(FolderFor(skillName), $"v{next}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), skillMd, new UTF8Encoding(false));
        index.Versions.Add(new Entry { Version = next, SavedAt = at ?? DateTimeOffset.Now });
        Store(skillName, index);
        return next;
    }

    /// <summary>Reads back one saved version, or null when its file is gone.</summary>
    public string? Read(string skillName, int version)
    {
        try
        {
            var path = Path.Combine(FolderFor(skillName), $"v{version}", "SKILL.md");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Forgets a skill's history — what removing the skill does.</summary>
    public void Remove(string skillName)
    {
        var folder = FolderFor(skillName);
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Carries a history over to a new name (rename, duplicate).</summary>
    public void Copy(string fromSkill, string toSkill)
    {
        var from = FolderFor(fromSkill);
        if (!Directory.Exists(from))
            return;
        var to = FolderFor(toSkill);
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private Index Load(string skillName)
    {
        var file = Path.Combine(FolderFor(skillName), "index.json");
        try
        {
            if (!File.Exists(file))
                return new Index();
            return JsonSerializer.Deserialize<Index>(File.ReadAllText(file), Options) ?? new Index();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Index();
        }
    }

    private void Store(string skillName, Index index)
    {
        var folder = FolderFor(skillName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "index.json"), JsonSerializer.Serialize(index, Options), new UTF8Encoding(false));
    }
}

/// <summary>What a skill file picked for upload turned out to hold.</summary>
public sealed record SkillUploadPreview(string? Name, string? Description, string SkillMd, IReadOnlyList<string> Files);

/// <summary>The result of an upload: a skill was written, the name is taken, or the file was refused.</summary>
public sealed record SkillUploadOutcome(string Kind, string? SkillName, IReadOnlyList<string> Messages)
{
    public const string Ok = "ok";
    public const string Conflict = "conflict";
    public const string Invalid = "invalid";
}

/// <summary>
/// The writes the Skills page makes to a skills directory: create, save a
/// version, duplicate, rename, remove, and the upload of a .md, .zip or .skill
/// file. Every skill it writes is a folder skill (SKILL.md plus resources), the
/// shape both this app and Claude Code read.
/// </summary>
public static class SkillLibrary
{
    /// <summary>The extensions the upload accepts.</summary>
    public static readonly IReadOnlyList<string> UploadExtensions = [".zip", ".skill", ".md"];

    /// <summary>The reference reads a skill file up to this size and refuses beyond it.</summary>
    public const long MaxUploadBytes = 31_457_280;

    public static bool HasUploadExtension(string fileName) =>
        UploadExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a library skill's folder lives.</summary>
    public static string FolderFor(string skillsDirectory, string name) => Path.Combine(skillsDirectory, name);

    /// <summary>The SKILL.md of a library skill, or its flat file when it was written that way.</summary>
    public static string? ExistingPath(string skillsDirectory, string name)
    {
        var folder = Path.Combine(skillsDirectory, name, "SKILL.md");
        if (File.Exists(folder))
            return folder;
        var flat = Path.Combine(skillsDirectory, name + ".md");
        return File.Exists(flat) ? flat : null;
    }

    public static bool Exists(string skillsDirectory, string name) => ExistingPath(skillsDirectory, name) is not null;

    /// <summary>Creates a folder skill from the editor's three fields.</summary>
    public static string Create(string skillsDirectory, string name, string description, string instructions)
    {
        var folder = FolderFor(skillsDirectory, name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "SKILL.md");
        File.WriteAllText(path, SkillEditorRules.ComposeSkillMd(name, description, instructions), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// Saves the editor over an existing SKILL.md, snapshotting what was there
    /// first when a version store is given. Returns the version number saved.
    /// </summary>
    public static int? SaveVersion(
        string skillMdPath, string skillName, string description, string instructions, SkillVersions? versions)
    {
        var current = File.Exists(skillMdPath) ? File.ReadAllText(skillMdPath) : "";
        int? number = null;
        if (versions is not null && current.Length > 0)
            number = versions.Save(skillName, current);
        var updated = SkillEditorRules.WithDescription(current, description);
        var body = updated is null
            ? SkillEditorRules.ComposeSkillMd(skillName, description, instructions)
            : ReplaceBody(updated, instructions);
        File.WriteAllText(skillMdPath, body, new UTF8Encoding(false));
        return number;
    }

    /// <summary>Keeps a file's frontmatter and swaps the instructions under it.</summary>
    public static string ReplaceBody(string skillMd, string instructions)
    {
        var split = SkillEditorRules.SplitFrontmatter(skillMd);
        if (split is null)
            return instructions;
        var (head, frontmatter, tail) = split.Value;
        var fence = Regex.Match(tail, @"^\r?\n---[ \t]*(?:\r?\n|$)");
        var closing = fence.Success ? (fence.Value.EndsWith('\n') ? fence.Value : fence.Value + "\n") : "\n---\n";
        return $"{head}{frontmatter}{closing}\n{instructions}";
    }

    /// <summary>Copies a skill's folder to "{name}-copy" (then "-copy-2"…) and returns the new name.</summary>
    public static string Duplicate(string skillsDirectory, string name)
    {
        var source = ExistingPath(skillsDirectory, name)
            ?? throw new FileNotFoundException("The skill has no file to copy.", name);
        var target = $"{name}-copy";
        for (var n = 2; Exists(skillsDirectory, target); n++)
            target = $"{name}-copy-{n}";
        var folder = FolderFor(skillsDirectory, target);
        Directory.CreateDirectory(folder);
        if (Path.GetFileName(source).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            CopyDirectory(Path.GetDirectoryName(source)!, folder);
        else
            File.Copy(source, Path.Combine(folder, "SKILL.md"));
        var path = Path.Combine(folder, "SKILL.md");
        File.WriteAllText(path, WithName(File.ReadAllText(path), target), new UTF8Encoding(false));
        return target;
    }

    /// <summary>Renames a skill's folder and the name in its frontmatter.</summary>
    public static void Rename(string skillsDirectory, string name, string newName)
    {
        var source = ExistingPath(skillsDirectory, name)
            ?? throw new FileNotFoundException("The skill has no file to rename.", name);
        var folder = FolderFor(skillsDirectory, newName);
        if (Path.GetFileName(source).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(Path.GetDirectoryName(source)!, folder);
        }
        else
        {
            Directory.CreateDirectory(folder);
            File.Move(source, Path.Combine(folder, "SKILL.md"));
        }
        var path = Path.Combine(folder, "SKILL.md");
        File.WriteAllText(path, WithName(File.ReadAllText(path), newName), new UTF8Encoding(false));
    }

    /// <summary>The name line rewritten, or added when the frontmatter lacks one.</summary>
    public static string WithName(string skillMd, string name)
    {
        var split = SkillEditorRules.SplitFrontmatter(skillMd);
        if (split is null)
            return $"---\nname: {name}\n---\n\n{skillMd}";
        var (head, frontmatter, tail) = split.Value;
        var nameLine = new Regex(@"^(name[ \t]*:)[ \t]*(?:\S.*)?$", RegexOptions.Multiline);
        var updated = nameLine.IsMatch(frontmatter)
            ? nameLine.Replace(frontmatter, m => $"{m.Groups[1].Value} {name}", 1)
            : $"name: {name}\n{frontmatter}";
        return head + updated + tail;
    }

    /// <summary>Deletes a skill's folder (or flat file).</summary>
    public static void Remove(string skillMdPath)
    {
        if (Path.GetFileName(skillMdPath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(skillMdPath) is { } folder)
            Directory.Delete(folder, recursive: true);
        else
            File.Delete(skillMdPath);
    }

    /// <summary>
    /// Reads a picked file: a .md is the SKILL.md itself, an archive must carry a
    /// SKILL.md at its root or one folder down, and the name and description come
    /// out of the frontmatter. Null when the file cannot be read as a skill.
    /// </summary>
    public static SkillUploadPreview? Preview(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length > MaxUploadBytes)
                return null;
            if (filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                var text = File.ReadAllText(filePath);
                var (name, description) = NameAndDescription(text);
                return new SkillUploadPreview(name, description, text, ["SKILL.md"]);
            }
            if (!HasUploadExtension(filePath))
                return null;
            using var archive = ZipFile.OpenRead(filePath);
            var entries = ArchiveSkillFiles(archive);
            if (entries is null)
                return null;
            var skillMd = archive.GetEntry(entries.SkillMdEntry)!;
            using var reader = new StreamReader(skillMd.Open());
            var md = reader.ReadToEnd();
            var parsed = NameAndDescription(md);
            return new SkillUploadPreview(parsed.Name, parsed.Description, md, entries.Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private sealed record ArchiveLayout(string SkillMdEntry, string Prefix, IReadOnlyList<string> Files);

    /// <summary>
    /// The SKILL.md an archive carries and the folder it sits in: at the root, or
    /// inside a single top-level folder (how a zipped skill folder comes out).
    /// </summary>
    private static ArchiveLayout? ArchiveSkillFiles(ZipArchive archive)
    {
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).Where(n => !n.EndsWith('/')).ToList();
        var root = names.FirstOrDefault(n => n.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (root is not null)
            return new ArchiveLayout(root, "", names);
        var nested = names.FirstOrDefault(n =>
            n.Count(c => c == '/') == 1 && n.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (nested is null)
            return null;
        var prefix = nested[..(nested.IndexOf('/') + 1)];
        return new ArchiveLayout(nested, prefix,
            [.. names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Select(n => n[prefix.Length..])]);
    }

    /// <summary>The frontmatter's name and description, trimmed, null when absent.</summary>
    public static (string? Name, string? Description) NameAndDescription(string skillMd)
    {
        var parsed = Frontmatter.ParseRich(skillMd);
        var name = Frontmatter.Get(parsed.Fields, "name")?.Trim();
        var description = Frontmatter.Get(parsed.Fields, "description")?.Trim();
        return (string.IsNullOrEmpty(name) ? null : name, string.IsNullOrEmpty(description) ? null : description);
    }

    /// <summary>
    /// Installs a picked file into a skills directory under the name its
    /// frontmatter declares (a .md may fall back to its file name). A taken name
    /// is a conflict unless <paramref name="overwrite"/> says to replace it.
    /// </summary>
    public static SkillUploadOutcome Upload(
        string skillsDirectory, string filePath, bool overwrite, SkillVersions? versions)
    {
        if (!HasUploadExtension(filePath))
            return new SkillUploadOutcome(SkillUploadOutcome.Invalid, null,
                ["Skill files must have a .skill, .zip, or .md file extension."]);
        var preview = Preview(filePath);
        if (preview is null)
            return new SkillUploadOutcome(SkillUploadOutcome.Invalid, null,
                [filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    ? ".md file must contain skill name and description formatted in YAML"
                    : ".zip or .skill file must include a SKILL.md file"]);
        var name = preview.Name ??
            (filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? SkillEditorRules.SanitizeName(Path.GetFileNameWithoutExtension(filePath))
                : null);
        if (name is null || preview.Description is null)
            return new SkillUploadOutcome(SkillUploadOutcome.Invalid, null,
                [".md file must contain skill name and description formatted in YAML"]);
        if (!SkillEditorRules.ValidName.IsMatch(name) || name.Length > SkillEditorRules.MaxNameLength)
            return new SkillUploadOutcome(SkillUploadOutcome.Invalid, name,
                ["Names can only contain lowercase letters, numbers, and hyphens."]);
        if (SkillEditorRules.ReservedWordIn(name) is not null)
            return new SkillUploadOutcome(SkillUploadOutcome.Invalid, name,
                ["That name is reserved. Choose a different one."]);

        var existing = ExistingPath(skillsDirectory, name);
        if (existing is not null && !overwrite)
            return new SkillUploadOutcome(SkillUploadOutcome.Conflict, name, []);
        if (existing is not null)
        {
            versions?.Save(name, File.ReadAllText(existing));
            Remove(existing);
        }

        var folder = FolderFor(skillsDirectory, name);
        Directory.CreateDirectory(folder);
        if (filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), preview.SkillMd, new UTF8Encoding(false));
        }
        else
        {
            using var archive = ZipFile.OpenRead(filePath);
            var layout = ArchiveSkillFiles(archive)!;
            foreach (var entry in archive.Entries)
            {
                var full = entry.FullName.Replace('\\', '/');
                if (full.EndsWith('/') || !full.StartsWith(layout.Prefix, StringComparison.Ordinal))
                    continue;
                var relative = full[layout.Prefix.Length..];
                if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal))
                    continue;
                var destination = Path.GetFullPath(Path.Combine(folder, relative));
                if (!destination.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }
        return new SkillUploadOutcome(SkillUploadOutcome.Ok, name, []);
    }

    /// <summary>The files a skill folder holds, relative and forward-slashed, SKILL.md first.</summary>
    public static IReadOnlyList<string> Files(string skillMdPath)
    {
        if (!Path.GetFileName(skillMdPath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(skillMdPath) is not { } folder || !Directory.Exists(folder))
            return File.Exists(skillMdPath) ? [Path.GetFileName(skillMdPath)] : [];
        try
        {
            return [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
                .OrderBy(f => f.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether the editor can round-trip this skill: the reference refuses to
    /// edit one carrying files beyond its SKILL.md, and says to use Replace.
    /// </summary>
    public static bool HasExtraFiles(string skillMdPath) =>
        Files(skillMdPath).Count(f => !f.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Zips a skill folder for Download.</summary>
    public static void Export(string skillMdPath, string zipPath)
    {
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        if (Path.GetFileName(skillMdPath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(skillMdPath) is { } folder)
        {
            ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return;
        }
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(skillMdPath, "SKILL.md");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
