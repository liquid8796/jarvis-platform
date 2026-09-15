using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JarvisCode.Core.Customization;

namespace JarvisCode.Cli;

/// <summary>Explicit session plugins; never installs a plugin or rewrites profile settings.</summary>
internal sealed class CliPluginScope : IAsyncDisposable, IDisposable
{
    private const long MaxDownloadBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 2048L * 1024 * 1024;
    private readonly string _parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "jarvis-cli-plugins"));
    private readonly List<string> _warnings = [];
    private readonly List<string> _outputStyles = [];
    private readonly List<(string Plugin, string Path)> _scopedStyles = [];
    private readonly List<string> _lspFiles = [];
    private readonly List<string> _workflows = [];
    private readonly List<(string Plugin, string Path)> _scopedWorkflows = [];
    private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _archiveNames = new(StringComparer.OrdinalIgnoreCase);
    private int _generated;
    private bool _disposed;

    private CliPluginScope()
    {
        Directory = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }
    public Plugins.PluginContent Content { get; private set; } = Plugins.PluginContent.Empty;
    public IReadOnlyList<string> Warnings => _warnings;
    public IReadOnlyDictionary<string, string> PluginRoots => _roots;
    public IReadOnlyList<string> OutputStylePaths => _outputStyles;
    public IReadOnlyList<(string Plugin, string Path)> ScopedOutputStylePaths => _scopedStyles;
    public IReadOnlyList<string> LspFiles => _lspFiles;
    public IReadOnlyList<string> WorkflowPaths => _workflows;
    public IReadOnlyList<(string Plugin, string Path)> ScopedWorkflowPaths => _scopedWorkflows;

    public static async Task<CliPluginScope> CreateAsync(IEnumerable<string> directories, IEnumerable<string> urls,
        HttpClient http, CancellationToken cancellationToken, string? workingDirectory = null,
        string? dataRoot = null, IReadOnlyDictionary<string, JsonObject>? configValues = null)
    {
        var scope = new CliPluginScope();
        try
        {
            var roots = new List<string>();
            var cwd = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
            foreach (var input in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input, cwd));
                if (System.IO.Directory.Exists(path)) roots.Add(path);
                else if (File.Exists(path) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    roots.Add(await scope.ExtractAsync(path, Path.GetFileNameWithoutExtension(path), cancellationToken));
                else throw new IOException($"Plugin directory or .zip archive not found: {input}");
            }
            foreach (var input in urls.SelectMany(value => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            {
                if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 ||
                    uri.Scheme is not ("https" or "http"))
                    throw new ArgumentException("Plugin URLs must be HTTP or HTTPS URLs without embedded credentials.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri is { } final && uri.Scheme == "https" && final.Scheme != "https")
                    throw new IOException("The plugin download redirected to an insecure URL.");
                if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw new IOException("The plugin archive exceeds 512 MiB.");
                var archivePath = Path.Combine(scope.Directory, "download-" + roots.Count + ".zip");
                await using (var inputStream = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var output = File.Create(archivePath))
                    await CopyBoundedAsync(inputStream, output, MaxDownloadBytes, timeout.Token);
                roots.Add(await scope.ExtractAsync(archivePath, Path.GetFileNameWithoutExtension(uri.AbsolutePath), cancellationToken));
            }
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scope.Load(root, cwd, dataRoot, configValues);
            }
            _ = JarvisCode.Core.LanguageServers.LanguageServerConfiguration.LoadValid(scope.LspFiles, scope._warnings);
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    private async Task<string> ExtractAsync(string archivePath, string fallbackName, CancellationToken cancellationToken)
    {
        var target = Path.Combine(Directory, "archive-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(target);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 100000) throw new InvalidDataException("The plugin archive contains too many entries.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var absolute = name.StartsWith('/');
            var isDirectory = name.EndsWith('/');
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();
            if (parts.Length == 0 && isDirectory) continue;
            if (absolute || parts.Any(part => part == ".." || part.Contains(':') ||
                    part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    Regex.IsMatch(part, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\\..*)?$", RegexOptions.IgnoreCase)) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The plugin archive contains an unsafe path or symbolic link.");
            name = string.Join('/', parts) + (isDirectory ? "/" : "");
            var destination = Path.GetFullPath(Path.Combine(target, name));
            if (!destination.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !paths.Add(destination))
                throw new InvalidDataException("The plugin archive contains an ambiguous path.");
            size = checked(size + entry.Length);
            if (size > MaxExpandedBytes) throw new InvalidDataException("The expanded plugin exceeds 2048 MiB.");
            if (name.EndsWith('/')) { System.IO.Directory.CreateDirectory(destination); continue; }
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyBoundedAsync(source, output, entry.Length, cancellationToken);
        }
        if (HasLayout(target)) { _archiveNames[target] = fallbackName; return target; }
        var candidates = System.IO.Directory.EnumerateDirectories(target).Where(HasLayout).ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new InvalidDataException("The archive must contain one plugin root.");
    }

    private static bool HasLayout(string directory) => File.Exists(Path.Combine(directory, ".claude-plugin", "plugin.json")) ||
        File.Exists(Path.Combine(directory, "plugin.json")) || File.Exists(Path.Combine(directory, "SKILL.md")) ||
        new[] { "skills", "commands", "agents", "hooks" }.Any(name => System.IO.Directory.Exists(Path.Combine(directory, name))) ||
        File.Exists(Path.Combine(directory, ".mcp.json"));

    private void Load(string root, string cwd, string? dataRoot, IReadOnlyDictionary<string, JsonObject>? configValues)
    {
        var manifestPath = new[] { Path.Combine(root, ".claude-plugin", "plugin.json"), Path.Combine(root, "plugin.json") }.FirstOrDefault(File.Exists);
        var manifest = manifestPath is null ? new JsonObject() : ReadObject(manifestPath);
        if (manifestPath is not null && manifest["name"] is null)
            throw new InvalidDataException("A plugin manifest must declare its name.");
        var name = manifest["name"]?.GetValue<string>() ?? _archiveNames.GetValueOrDefault(root, Path.GetFileName(root));
        if (!Regex.IsMatch(name, "^[a-zA-Z0-9][a-zA-Z0-9_-]*$")) throw new InvalidDataException("Plugin names must be kebab-case identifiers.");
        name = name.ToLowerInvariant();
        if (!_roots.TryAdd(name, root)) { _warnings.Add($"Plugin '{name}' was specified more than once; the first source is used."); return; }
        var data = Path.Combine(dataRoot ?? Path.Combine(Directory, "data"), name);
        var values = new JsonObject();
        if (manifest["userConfig"] is JsonObject configSchema)
            foreach (var field in configSchema)
                if (field.Value is JsonObject spec && spec["default"] is { } fallback)
                    values[field.Key] = fallback.DeepClone();
        if (configValues?.GetValueOrDefault(name) is { } supplied)
            foreach (var value in supplied) values[value.Key] = value.Value?.DeepClone();
        string Expand(string text)
        {
            text = text.Replace("${CLAUDE_PLUGIN_ROOT}", root, StringComparison.Ordinal)
                .Replace("${JARVIS_PLUGIN_ROOT}", root, StringComparison.Ordinal)
                .Replace("${CLAUDE_PROJECT_DIR}", cwd, StringComparison.Ordinal)
                .Replace("${CLAUDE_PLUGIN_DATA}", data, StringComparison.Ordinal);
            return Regex.Replace(text, @"\$\{user_config\.([^}]+)\}", match =>
                values[match.Groups[1].Value] is { } value ? value.ToString()
                    : throw new InvalidDataException($"Plugin '{name}' needs a value for user_config.{match.Groups[1].Value}."));
        }

        var commands = new List<CustomCommandDefinition>();
        var agents = new List<CustomAgentDefinition>();
        var skills = new List<SkillDefinition>();
        foreach (var component in Paths(manifest["commands"], root, "commands"))
            foreach (var file in MarkdownFiles(component))
            {
                var parsed = Frontmatter.ParseRich(File.ReadAllText(file));
                if (string.IsNullOrWhiteSpace(parsed.Body)) continue;
                commands.Add(new(name + ":" + Path.GetFileNameWithoutExtension(file).ToLowerInvariant(),
                    Frontmatter.Get(parsed.Fields, "description") ?? CustomCommandDefinition.DefaultDescription, Expand(parsed.Body))
                    { DescriptionDeclared = Frontmatter.Get(parsed.Fields, "description") is not null });
            }
        foreach (var component in Paths(manifest["agents"], root, "agents"))
            foreach (var file in MarkdownFiles(component))
            {
                // The existing parser preserves model, max-turns and cache TTL.
                // A one-file view avoids accidentally loading siblings not named
                // by an explicit manifest path; the original plugin root remains
                // the base used by every expanded resource reference.
                var view = Path.Combine(Directory, "agent-" + ++_generated);
                System.IO.Directory.CreateDirectory(view);
                File.WriteAllText(Path.Combine(view, Path.GetFileName(file)), Expand(File.ReadAllText(file)), new UTF8Encoding(false));
                agents.AddRange(CustomAgents.LoadDirectory(view).Select(agent => agent with { Name = name + ":" + agent.Name }));
            }
        var skillPaths = new List<string> { Path.Combine(root, "skills") };
        skillPaths.AddRange(Paths(manifest["skills"], root, null));
        if (manifest["skills"] is null && !System.IO.Directory.Exists(Path.Combine(root, "skills")) && File.Exists(Path.Combine(root, "SKILL.md"))) skillPaths.Add(root);
        foreach (var component in skillPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(component))
                skills.AddRange(Skills.LoadDirectory(Path.GetDirectoryName(component)!, "plugin:" + name).Where(skill => Path.GetFullPath(skill.FilePath) == Path.GetFullPath(component)));
            else if (System.IO.Directory.Exists(component))
            {
                var loaded = Skills.LoadDirectory(component, "plugin:" + name).ToList();
                var direct = Path.Combine(component, "SKILL.md");
                if (File.Exists(direct))
                {
                    var parsed = Frontmatter.ParseRich(File.ReadAllText(direct));
                    loaded = loaded.Select(skill => Path.GetFullPath(skill.FilePath) == Path.GetFullPath(direct)
                        ? skill with { Name = Frontmatter.Get(parsed.Fields, "name") ?? Path.GetFileName(component) } : skill).ToList();
                }
                skills.AddRange(loaded);
            }
        }
        skills = skills.DistinctBy(skill => skill.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(skill => skill with { Name = name + ":" + skill.Name, Body = Expand(skill.Body) }).ToList();

        var hookFiles = JsonContributions(manifest["hooks"], root, ["hooks/hooks.json", "hooks.json"])
            .Select(document =>
            {
                var normalized = ExpandJson(document, Expand).AsObject();
                normalized["$jarvis_plugin_root"] = root;
                normalized["$jarvis_plugin_data"] = data;
                normalized["$jarvis_project_dir"] = cwd;
                return SaveJson(normalized, "hooks");
            }).ToArray();
        var mcpFiles = JsonContributions(manifest["mcpServers"], root, [".mcp.json", "mcp.json"])
            .Select(document =>
            {
                var normalized = ExpandJson(document, Expand).AsObject();
                var servers = (normalized["mcpServers"] as JsonObject) ?? normalized;
                var scoped = new JsonObject();
                foreach (var server in servers)
                {
                    if (server.Value is not JsonObject config) continue;
                    config = config.DeepClone().AsObject();
                    var env = config["env"] as JsonObject ?? new JsonObject();
                    if (config["env"] is null) config["env"] = env;
                    env["CLAUDE_PLUGIN_ROOT"] = root;
                    env["CLAUDE_PLUGIN_DATA"] = data;
                    env["CLAUDE_PROJECT_DIR"] = cwd;
                    scoped[$"plugin:{name}:{server.Key}"] = config;
                }
                return SaveJson(new JsonObject { ["mcpServers"] = scoped }, "mcp");
            }).ToArray();
        var stylePaths = Paths(manifest["outputStyles"], root, "output-styles").ToArray();
        _outputStyles.AddRange(stylePaths);
        _scopedStyles.AddRange(stylePaths.Select(path => (name, path)));
        var workflowPaths = Paths(manifest["workflows"], root, "workflows").ToArray();
        _workflows.AddRange(workflowPaths);
        _scopedWorkflows.AddRange(workflowPaths.Select(path => (name, path)));
        _lspFiles.AddRange(JsonContributions(manifest["lspServers"], root, [".lsp.json"])
            .Select(document =>
            {
                var source = ExpandJson(document, Expand).AsObject();
                var scoped = new JsonObject();
                foreach (var server in source)
                {
                    if (server.Value is not JsonObject raw) continue;
                    var config = raw.DeepClone().AsObject();
                    var env = config["env"] as JsonObject ?? new JsonObject();
                    if (config["env"] is null) config["env"] = env;
                    env["CLAUDE_PLUGIN_ROOT"] = root;
                    env["CLAUDE_PLUGIN_DATA"] = data;
                    env["CLAUDE_PROJECT_DIR"] = cwd;
                    if (config["command"] is JsonValue command && command.TryGetValue<string>(out var executable) &&
                        (executable.StartsWith("./", StringComparison.Ordinal) || executable.StartsWith(".\\", StringComparison.Ordinal)))
                        config["command"] = Path.GetFullPath(Path.Combine(root, executable));
                    scoped[$"plugin:{name}:{server.Key}"] = config;
                }
                return SaveJson(scoped, "lsp");
            }));
        var info = new PluginInfo(name, root, commands.Count, agents.Count, skills.Count, hookFiles.Length > 0, mcpFiles.Length > 0)
            { Scope = "session", Root = root };
        System.IO.Directory.CreateDirectory(data);
        Content = new([.. Content.Installed, info], [.. Content.Commands, .. commands], [.. Content.Agents, .. agents],
            [.. Content.Skills, .. skills], [.. Content.HookFiles, .. hookFiles], [.. Content.McpFiles, .. mcpFiles]);
    }

    private static IEnumerable<string> Paths(JsonNode? node, string root, string? fallback)
    {
        if (node is null)
        {
            if (fallback is not null)
            {
                var candidate = Path.Combine(root, fallback);
                if (File.Exists(candidate) || System.IO.Directory.Exists(candidate)) yield return candidate;
            }
            yield break;
        }
        foreach (var value in node is JsonArray array ? array : new JsonArray(node.DeepClone()))
        {
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var relative) ||
                (relative != "." && !relative.StartsWith("./", StringComparison.Ordinal)))
                throw new InvalidDataException("Plugin component paths must start with ./ and stay inside the plugin.");
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A plugin component points outside its root.");
            if (!File.Exists(full) && !System.IO.Directory.Exists(full)) throw new FileNotFoundException("A declared plugin component was not found.", full);
            yield return full;
        }
    }

    private static IEnumerable<string> MarkdownFiles(string path) => File.Exists(path)
        ? [path] : System.IO.Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories);

    private static IEnumerable<JsonObject> JsonContributions(JsonNode? declared, string root, string[] defaults)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fallback in defaults)
        {
            var path = Path.Combine(root, fallback);
            if (File.Exists(path) && seen.Add(Path.GetFullPath(path))) yield return ReadObject(path);
        }
        if (declared is JsonObject inline) { yield return inline; yield break; }
        foreach (var path in Paths(declared, root, null))
            if (seen.Add(path)) yield return ReadObject(path);
    }

    private static JsonObject ReadObject(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("The plugin manifest/config is too large.");
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("The plugin manifest/config must be a JSON object.");
    }

    private static JsonNode ExpandJson(JsonNode node, Func<string, string> expand) => node switch
    {
        JsonObject value => new JsonObject(value.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value is null ? null : ExpandJson(pair.Value, expand)))),
        JsonArray value => new JsonArray(value.Select(item => item is null ? null : ExpandJson(item, expand)).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(expand(text))!,
        _ => node.DeepClone(),
    };

    private string SaveJson(JsonNode value, string kind)
    {
        var path = Path.Combine(Directory, kind + "-" + ++_generated + ".json");
        File.WriteAllText(path, value.ToJsonString(), new UTF8Encoding(false));
        return path;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            copied += read;
            if (copied > limit) throw new InvalidDataException("The plugin archive exceeded its declared or allowed size.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var target = Path.GetFullPath(Directory);
        if (!target.StartsWith(_parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The temporary plugin path is outside the owned directory.");
        if (System.IO.Directory.Exists(target))
        {
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The temporary plugin root was replaced by a link; cleanup was refused.");
            System.IO.Directory.Delete(target, recursive: true);
        }
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
