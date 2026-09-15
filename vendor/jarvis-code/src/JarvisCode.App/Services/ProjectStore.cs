using System.IO;
using System.Text.Json;

namespace JarvisCode.App.Services;

/// <summary>A chat project: a name, standing instructions, and the context it carries.</summary>
public sealed class ChatProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "";

    /// <summary>Shown under the name, and sent instead of the instructions when there are none.</summary>
    public string? Description { get; set; }

    /// <summary>What the model is told to follow while working in this project.</summary>
    public string Instructions { get; set; } = "";

    /// <summary>Whether this project's chats may recall memory (the reference's "Memory on").</summary>
    public bool MemoryEnabled { get; set; } = true;

    /// <summary>Folders attached as context; their files ride the conversation as documents.</summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>Links attached as reference context.</summary>
    public List<ProjectLinkEntry> Links { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>A link attached to a project, as it is stored.</summary>
public sealed class ProjectLinkEntry
{
    public string Url { get; set; } = "";

    public string? Title { get; set; }
}

/// <summary>The projects file's shape.</summary>
public sealed class ProjectsData
{
    public List<ChatProject> Projects { get; set; } = [];

    /// <summary>Session id → project id, for the chats filed under a project.</summary>
    public Dictionary<string, string> SessionProjects { get; set; } = [];
}

/// <summary>
/// The Chat surface's projects, in the App's own <c>projects.json</c>. A project
/// carries standing instructions, context folders and links, and the chats filed
/// under it; <see cref="ProjectInstructionsBlock"/> is what the model actually sees.
///
/// The engine's Session model is untouched — the session-to-project mapping lives
/// here beside the projects, the way session groups live beside the sessions.
/// </summary>
public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;

    public ProjectStore(string filePath)
    {
        _filePath = filePath;
        Data = Load(filePath);
    }

    public ProjectsData Data { get; }

    /// <summary>Every project, newest first — the order the reference's picker lists them in.</summary>
    public IReadOnlyList<ChatProject> All =>
        [.. Data.Projects.OrderByDescending(static p => p.UpdatedAt)];

    public ChatProject? Find(string? id) =>
        id is null ? null : Data.Projects.FirstOrDefault(p => p.Id == id);

    /// <summary>The project a chat is filed under, or null.</summary>
    public ChatProject? ForSession(string sessionId) =>
        Find(Data.SessionProjects.GetValueOrDefault(sessionId));

    /// <summary>Matches a project by name, the way the picker's search box does.</summary>
    public IReadOnlyList<ChatProject> Search(string? query)
    {
        var trimmed = query?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? All
            : [.. All.Where(p => p.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))];
    }

    public ChatProject Create(string name)
    {
        var project = new ChatProject { Name = name.Trim() };
        Data.Projects.Add(project);
        Save();
        return project;
    }

    public void Update(ChatProject project)
    {
        project.UpdatedAt = DateTimeOffset.Now;
        if (!Data.Projects.Contains(project))
        {
            Data.Projects.Add(project);
        }

        Save();
    }

    public void Delete(string projectId)
    {
        Data.Projects.RemoveAll(p => p.Id == projectId);
        foreach (var session in Data.SessionProjects.Where(pair => pair.Value == projectId).Select(static p => p.Key).ToList())
        {
            Data.SessionProjects.Remove(session);
        }

        Save();
    }

    /// <summary>Files a chat under a project, or removes it from one when the id is null.</summary>
    public void Assign(string sessionId, string? projectId)
    {
        if (projectId is null)
        {
            Data.SessionProjects.Remove(sessionId);
        }
        else
        {
            Data.SessionProjects[sessionId] = projectId;
        }

        Save();
    }

    /// <summary>Drops mappings for sessions that no longer exist.</summary>
    public void Prune(IEnumerable<string> liveSessionIds)
    {
        var live = new HashSet<string>(liveSessionIds, StringComparer.Ordinal);
        var gone = Data.SessionProjects.Keys.Where(id => !live.Contains(id)).ToList();
        if (gone.Count == 0)
        {
            return;
        }

        foreach (var id in gone)
        {
            Data.SessionProjects.Remove(id);
        }

        Save();
    }

    /// <summary>The block this project puts in front of the model, or null when it has nothing to say.</summary>
    public static string? Block(ChatProject? project) =>
        project is null
            ? null
            : ProjectInstructionsBlock.Build(
                project.Name,
                project.Description,
                project.Instructions,
                [.. project.Links.Select(static l => new ProjectLink(l.Url, l.Title))]);

    public void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Options));
        File.Move(tmp, _filePath, overwrite: true);
    }

    private static ProjectsData Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                return JsonSerializer.Deserialize<ProjectsData>(File.ReadAllText(filePath), Options) ?? new ProjectsData();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt projects file must not take the app down with it.
        }

        return new ProjectsData();
    }
}
