namespace JarvisCode.Core.Customization;

/// <summary>A reusable instruction pack the model can load on demand.</summary>
public sealed record SkillDefinition(
    string Name,
    string Description,
    string Author,
    string Body,
    string FilePath,
    DateTimeOffset LastUpdated,
    string Source)
{
    /// <summary>The skill's base directory (the folder for folder skills, the containing directory otherwise).</summary>
    public string Directory { get; init; } = "";

    /// <summary>Frontmatter "when-to-use": appended to the listing entry as "description - whenToUse".</summary>
    public string? WhenToUse { get; init; }

    /// <summary>Frontmatter "argument-hint": display-only hint shown next to the command name.</summary>
    public string? ArgumentHint { get; init; }

    /// <summary>Frontmatter "arguments": named argument list, substituted as $name in the body.</summary>
    public IReadOnlyList<string> ArgumentNames { get; init; } = [];

    /// <summary>Frontmatter "allowed-tools": permission rules auto-granted while the skill runs.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Frontmatter "disallowed-tools": permission rules denied while the skill runs.</summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>Frontmatter "paths": gitignore-style patterns; the skill stays dormant until an edited file matches.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Frontmatter "disable-model-invocation": reserved for explicit /name invocation by the user.</summary>
    public bool DisableModelInvocation { get; init; }

    /// <summary>Frontmatter "user-invocable" (default true): false hides the skill from the "/" menu.</summary>
    public bool UserInvocable { get; init; } = true;

    /// <summary>Frontmatter "model": model id for forked execution ("inherit" keeps the session's).</summary>
    public string? Model { get; init; }

    /// <summary>Frontmatter "effort": effort level for forked execution.</summary>
    public string? Effort { get; init; }

    /// <summary>Frontmatter "context: fork" — the skill runs as a subagent instead of inline.</summary>
    public bool Fork { get; init; }

    /// <summary>Frontmatter "agent": the agent type a forked skill runs as.</summary>
    public string? Agent { get; init; }

    /// <summary>Frontmatter "background": forked skills default to background (null = unset → true).</summary>
    public bool? Background { get; init; }

    /// <summary>Frontmatter "shell" for !`cmd` preprocessing: "bash" (default) or "powershell".</summary>
    public string Shell { get; init; } = "bash";

    /// <summary>True when the frontmatter declared "shell" explicitly (part of the reference's approval trigger).</summary>
    public bool ShellDeclared { get; init; }

    /// <summary>Raw frontmatter "hooks" block, parsed by the host when the skill is invoked.</summary>
    public string? HooksBlock { get; init; }

    /// <summary>Frontmatter "hide-from-slash-command-tool" (parsed for compatibility; no behavior).</summary>
    public bool HideFromSlashCommandTool { get; init; }

    /// <summary>For directory-scoped variants renamed "{dir}:{name}": the original bare name.</summary>
    public string? UnqualifiedName { get; init; }

    /// <summary>For directory-scoped skills: the subdirectory (forward slashes) they were found under.</summary>
    public string? ScopeDirectory { get; init; }

    /// <summary>True when the frontmatter carried an explicit description (the reference lists plugin entries only then).</summary>
    public bool DescriptionDeclared { get; init; }

    /// <summary>The listing entry description (reference: "description - whenToUse").</summary>
    public string ListingDescription =>
        string.IsNullOrEmpty(WhenToUse) ? Description : $"{Description} - {WhenToUse}";
}

/// <summary>
/// Loads skills from the project ({cwd}/.jarvis/skills plus the Claude Code
/// compatible {cwd}/.claude/skills) and user-level directories. Frontmatter
/// carries the reference CLI's skill keys; project skills shadow user-level
/// ones with the same name. Directory-scoped skills (a subdirectory's own
/// .claude/skills) are discovered by <see cref="LoadWithDirectoryScoped"/>.
/// </summary>
public static class Skills
{
    public const string ProjectSubdirectory = ".jarvis/skills";
    public const string ClaudeProjectSubdirectory = ".claude/skills";
    public const string ProjectSource = "project";
    public const string UserSource = "user";

    /// <summary>How deep under the project root directory-scoped skill dirs are searched.</summary>
    private const int ScopeScanDepth = 4;

    public static IReadOnlyList<SkillDefinition> Load(string workingDirectory, string? userDirectory) =>
        Load(workingDirectory, userDirectory, claudeUserDirectory: null);

    public static IReadOnlyList<SkillDefinition> Load(
        string workingDirectory, string? userDirectory, string? claudeUserDirectory)
    {
        // Reference resolution is find-FIRST (user → project), so a user-level
        // skill shadows a project one with the same name. Within each level the
        // app-native directory wins over the Claude Code-compatible one.
        var byName = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        void AddAll(IEnumerable<SkillDefinition> loaded)
        {
            foreach (var skill in loaded)
                byName.TryAdd(skill.Name, skill);
        }
        if (userDirectory is not null)
            AddAll(LoadDirectory(userDirectory, UserSource));
        if (claudeUserDirectory is not null)
            AddAll(LoadDirectory(claudeUserDirectory, UserSource));
        AddAll(LoadDirectory(ProjectDirectoryPath(workingDirectory, ProjectSubdirectory), ProjectSource));
        AddAll(LoadDirectory(ProjectDirectoryPath(workingDirectory, ClaudeProjectSubdirectory), ProjectSource));
        return [.. byName.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// <see cref="Load"/> plus the reference's directory-scoped skills: any
    /// subdirectory's .claude/skills (or .jarvis/skills) under the project root.
    /// A scoped skill whose name is free keeps it, with the listing suffix
    /// naming its directory; a collision renames it "{dir}:{name}".
    /// </summary>
    public static IReadOnlyList<SkillDefinition> LoadWithDirectoryScoped(
        string workingDirectory, string? userDirectory, string? claudeUserDirectory)
    {
        var skills = Load(workingDirectory, userDirectory, claudeUserDirectory).ToList();
        var byName = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
            byName[skill.Name] = skill;

        foreach (var (scopeDir, skillsDir) in FindScopedSkillDirectories(workingDirectory))
        {
            foreach (var skill in LoadDirectory(skillsDir, ProjectSource))
            {
                var scoped = skill with { ScopeDirectory = scopeDir };
                if (!byName.ContainsKey(skill.Name))
                {
                    scoped = scoped with
                    {
                        Description = skill.Description +
                            $" (from {scopeDir}/.claude/skills — applies when working on files under {scopeDir}/)",
                    };
                    byName[scoped.Name] = scoped;
                    skills.Add(scoped);
                }
                else
                {
                    var qualified = $"{scopeDir}:{skill.Name}";
                    if (byName.ContainsKey(qualified))
                        continue;
                    scoped = scoped with
                    {
                        Name = qualified,
                        UnqualifiedName = skill.Name,
                        Description = skill.Description +
                            $" (scoped to {scopeDir}/ — use this instead of the unscoped \"{skill.Name}\" skill " +
                            $"when the files being changed are under {scopeDir}/)",
                    };
                    byName[qualified] = scoped;
                    skills.Add(scoped);
                }
            }
        }
        return [.. skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>OS-native project subdirectory path (the constants carry forward slashes).</summary>
    private static string ProjectDirectoryPath(string workingDirectory, string subdirectory) =>
        Path.Combine(workingDirectory, subdirectory.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Subdirectories under the root carrying their own skills dir, as (relative dir, skills dir).</summary>
    private static IEnumerable<(string ScopeDir, string SkillsDir)> FindScopedSkillDirectories(string root)
    {
        var results = new List<(string, string)>();
        void Walk(string directory, string relative, int depth)
        {
            if (depth > ScopeScanDepth)
                return;
            string[] children;
            try
            {
                children = System.IO.Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Length == 0 || name[0] == '.' ||
                    name is "node_modules" or "bin" or "obj" or "dist" or "build" or "target" or "vendor")
                    continue;
                var childRelative = relative.Length == 0 ? name : $"{relative}/{name}";
                foreach (var sub in new[] { ClaudeProjectSubdirectory, ProjectSubdirectory })
                {
                    var skillsDir = Path.Combine(child, sub.Replace('/', Path.DirectorySeparatorChar));
                    if (System.IO.Directory.Exists(skillsDir))
                        results.Add((childRelative, skillsDir));
                }
                Walk(child, childRelative, depth + 1);
            }
        }
        Walk(root, "", 0);
        return results;
    }

    public static IEnumerable<SkillDefinition> LoadDirectory(string directory, string source)
    {
        var skills = new List<SkillDefinition>();
        LoadFlatFiles(directory, source, skills);
        LoadSkillFolders(directory, source, namePrefix: "", depth: 0, skills);
        return skills;
    }

    private static void LoadFlatFiles(string directory, string source, List<SkillDefinition> skills)
    {
        string[] files;
        try
        {
            if (!System.IO.Directory.Exists(directory))
                return;
            files = System.IO.Directory.GetFiles(directory, "*.md");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            if (ReadSkill(file, Path.GetFileNameWithoutExtension(file), directory, source) is { } skill)
                skills.Add(skill);
        }
    }

    /// <summary>
    /// Claude Code-style skills: one folder per skill holding SKILL.md plus
    /// bundled resources. Nested folders become ':'-separated name segments
    /// ("a/b/SKILL.md" → "a:b"), like the reference's plugin skill naming.
    /// </summary>
    private static void LoadSkillFolders(
        string directory, string source, string namePrefix, int depth, List<SkillDefinition> skills)
    {
        if (depth > ScopeScanDepth)
            return;
        string[] subdirectories;
        try
        {
            if (!System.IO.Directory.Exists(directory))
                return;
            subdirectories = System.IO.Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var subdirectory in subdirectories)
        {
            var folderName = Path.GetFileName(subdirectory);
            if (folderName.Length == 0 || folderName[0] == '.')
                continue;
            var defaultName = namePrefix.Length == 0 ? folderName : $"{namePrefix}:{folderName}";
            var skillFile = Path.Combine(subdirectory, "SKILL.md");
            bool hasSkill;
            try
            {
                hasSkill = File.Exists(skillFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (hasSkill)
            {
                if (ReadSkill(skillFile, defaultName, subdirectory, source, namePrefix) is { } skill)
                    skills.Add(skill);
            }
            else
            {
                LoadSkillFolders(subdirectory, source, defaultName, depth + 1, skills);
            }
        }
    }

    private static SkillDefinition? ReadSkill(
        string file, string defaultName, string baseDirectory, string source, string namePrefix = "")
    {
        string content;
        DateTimeOffset updated;
        try
        {
            content = File.ReadAllText(file);
            updated = File.GetLastWriteTime(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var parsed = Frontmatter.ParseRich(content);
        if (parsed.Body.Length == 0)
            return null;
        var fields = parsed.Fields;
        var name = (Frontmatter.Get(fields, "name") ?? defaultName).Trim().ToLowerInvariant();
        // A frontmatter name replaces only the leaf, keeping the folder-derived
        // prefix, like the reference's plugin naming.
        if (namePrefix.Length > 0 && Frontmatter.Get(fields, "name") is not null &&
            !name.StartsWith(namePrefix + ":", StringComparison.OrdinalIgnoreCase))
        {
            name = $"{namePrefix}:{name}";
        }
        if (!IsValidName(name))
            return null;

        // Frontmatter key, not a tool name: skills declare `shell: powershell`.
        var shell = Frontmatter.Get(fields, "shell")?.Trim().ToLowerInvariant();
        var context = Frontmatter.Get(fields, "context")?.Trim().ToLowerInvariant();
        return new SkillDefinition(
            name,
            Frontmatter.Get(fields, "description") ?? "Custom skill",
            Frontmatter.Get(fields, "author") ?? "You",
            parsed.Body,
            file,
            updated,
            source)
        {
            Directory = baseDirectory,
            WhenToUse = Frontmatter.Get(fields, "when-to-use"),
            ArgumentHint = Frontmatter.Get(fields, "argument-hint"),
            ArgumentNames = Frontmatter.GetList(parsed, "arguments") ?? [],
            AllowedTools = Frontmatter.GetList(parsed, "allowed-tools") ?? [],
            DisallowedTools = Frontmatter.GetList(parsed, "disallowed-tools") ?? [],
            Paths = Frontmatter.GetList(parsed, "paths") ?? [],
            DisableModelInvocation = Frontmatter.GetBool(fields, "disable-model-invocation") ?? false,
            UserInvocable = Frontmatter.GetBool(fields, "user-invocable") ?? true,
            Model = Frontmatter.Get(fields, "model"),
            Effort = Frontmatter.Get(fields, "effort"),
            Fork = context == "fork",
            Agent = Frontmatter.Get(fields, "agent"),
            Background = Frontmatter.GetBool(fields, "background"),
            Shell = shell is "powershell" ? "powershell" : "bash",
            ShellDeclared = shell is not null,
            HooksBlock = Frontmatter.GetBlock(parsed, "hooks"),
            HideFromSlashCommandTool = Frontmatter.GetBool(fields, "hide-from-slash-command-tool") ?? false,
            DescriptionDeclared = Frontmatter.Get(fields, "description") is not null,
        };
    }

    /// <summary>Reference name validation: 1-256 chars, no whitespace/control chars and no angle brackets.</summary>
    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= 256 &&
        !name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>');
}
