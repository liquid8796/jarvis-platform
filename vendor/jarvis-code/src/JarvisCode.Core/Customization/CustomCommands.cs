namespace JarvisCode.Core.Customization;

/// <summary>A user-defined slash command backed by a markdown prompt template.</summary>
public sealed record CustomCommandDefinition(string Name, string Description, string Template)
{
    public const string ArgumentsPlaceholder = "$ARGUMENTS";

    /// <summary>The description a file with no frontmatter "description" falls back to.</summary>
    public const string DefaultDescription = "Custom command";

    /// <summary>True when the frontmatter carried an explicit description.</summary>
    public bool DescriptionDeclared { get; init; }

    /// <summary>Fills the template: replaces $ARGUMENTS, or appends the arguments when absent.</summary>
    public string BuildPrompt(string arguments)
    {
        if (Template.Contains(ArgumentsPlaceholder, StringComparison.Ordinal))
            return Template.Replace(ArgumentsPlaceholder, arguments, StringComparison.Ordinal);
        return arguments.Length == 0 ? Template : $"{Template}\n\n{arguments}";
    }
}

/// <summary>
/// Loads custom slash commands from markdown files: {cwd}/.jarvis/commands/*.md
/// (project) plus a user-level directory. The file name is the command name;
/// optional frontmatter provides a description. Project commands shadow
/// user-level ones with the same name.
/// </summary>
public static class CustomCommands
{
    public const string ProjectSubdirectory = ".jarvis/commands";

    public static IReadOnlyList<CustomCommandDefinition> Load(string workingDirectory, string? userDirectory)
    {
        var byName = new Dictionary<string, CustomCommandDefinition>(StringComparer.OrdinalIgnoreCase);
        if (userDirectory is not null)
        {
            foreach (var command in LoadDirectory(userDirectory))
                byName[command.Name] = command;
        }
        foreach (var command in LoadDirectory(Path.Combine(workingDirectory, ProjectSubdirectory)))
            byName[command.Name] = command;
        return [.. byName.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public static IEnumerable<CustomCommandDefinition> LoadDirectory(string directory)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(directory))
                return [];
            files = Directory.GetFiles(directory, "*.md");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var commands = new List<CustomCommandDefinition>();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file).Trim().ToLowerInvariant();
            if (name.Length == 0 || name.Any(char.IsWhiteSpace))
                continue;
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var (fields, body) = Frontmatter.Parse(content);
            if (body.Length == 0)
                continue;
            string? declared = fields.TryGetValue("description", out var d) ? d : null;
            commands.Add(new CustomCommandDefinition(
                name, declared ?? CustomCommandDefinition.DefaultDescription, body)
            {
                DescriptionDeclared = declared is not null,
            });
        }
        return commands;
    }
}
