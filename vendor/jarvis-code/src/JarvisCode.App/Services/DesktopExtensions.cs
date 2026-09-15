using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>One field a `.mcpb` extension asks the user for (its <c>user_config</c> entry).</summary>
public sealed record ExtensionConfigField(
    string Key,
    string Type,
    string Title,
    string Description,
    bool Required,
    bool Multiple,
    bool Sensitive,
    JsonNode? Default,
    double? Min,
    double? Max);

/// <summary>The manifest inside a `.mcpb` / `.dxt` bundle.</summary>
public sealed record ExtensionManifest(
    string Name,
    string? DisplayName,
    string Version,
    string Description,
    string? LongDescription,
    string? AuthorName,
    string? Homepage,
    string? Documentation,
    string? Support,
    string? Icon,
    IReadOnlyList<string> Keywords,
    string? License,
    IReadOnlyList<string> Platforms,
    string? ServerType,
    string? EntryPoint,
    JsonObject? McpConfig,
    IReadOnlyList<ExtensionConfigField> UserConfig)
{
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;
}

/// <summary>An installed extension: its folder, its manifest and the values the user filled in.</summary>
public sealed record InstalledExtension(
    string Id,
    string Directory,
    ExtensionManifest Manifest,
    IReadOnlyDictionary<string, JsonNode?> UserConfig,
    bool Enabled)
{
    /// <summary>Null for a local file; a directory installation carries its catalogue identity.</summary>
    public string? DirectoryId { get; init; }
}

/// <summary>
/// Desktop extensions — the `.MCPB` / `.DXT` bundles the reference's
/// Settings › Desktop app › Extensions page installs. A bundle is a zip carrying a
/// <c>manifest.json</c>; the manifest's schema was measured from the reference's own
/// validator (app.asar 1.40609.1.0, <c>index.chunk-DnlgCaT3.js</c>: its <c>mJe</c>
/// for the document, <c>lJe</c> for <c>server</c>, <c>iJe</c>/<c>cJe</c> for
/// <c>mcp_config</c> and its <c>platform_overrides</c>, and <c>pJe</c> for a
/// <c>user_config</c> field), and the config resolution from its <c>lYe</c> and
/// <c>cYe</c>: the platform's overrides win over the base command/args/env, then
/// every <c>${…}</c> hole is filled from <c>__dirname</c>, <c>pathSeparator</c>,
/// <c>/</c>, the system directories (<c>HOME</c>, <c>DESKTOP</c>, <c>DOCUMENTS</c>,
/// <c>DOWNLOADS</c>) and <c>user_config.KEY</c>, an array element that is exactly
/// one <c>${user_config.key}</c> expanding into its values. An extension with an
/// unfilled required field contributes no server at all, as the reference's
/// <c>dYe</c> makes it skip one.
/// </summary>
public static partial class DesktopExtensions
{
    /// <summary>The two extensions the reference installs, in the order it tests them.</summary>
    public static readonly IReadOnlyList<string> BundleExtensions = [".mcpb", ".dxt"];

    public static bool IsBundle(string path)
    {
        var extension = Path.GetExtension(path);
        return BundleExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where installed bundles live; each is one folder named by its manifest name.</summary>
    public static string Root(ProfilePaths paths) => Path.Combine(paths.Root, "extensions");

    /// <summary>The generated MCP config the extensions contribute; joined as an extra config file.</summary>
    public static string McpFile(ProfilePaths paths) => Path.Combine(Root(paths), "extensions-mcp.json");

    /// <summary>
    /// The extra MCP config files a session loads: the plugins' own plus the
    /// extensions' generated one, when any extension has produced a server.
    /// </summary>
    public static IReadOnlyList<string> ConfigFiles(ProfilePaths paths, IReadOnlyList<string> pluginFiles)
    {
        var file = McpFile(paths);
        return File.Exists(file) ? [.. pluginFiles, file] : pluginFiles;
    }

    private static string StateFile(ProfilePaths paths) => Path.Combine(Root(paths), "state.json");

    // ---- manifest ----

    public static ExtensionManifest? ParseManifest(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject manifest)
        {
            return null;
        }

        // The reference refuses a manifest that declares neither version marker.
        if (manifest["manifest_version"] is null && manifest["dxt_version"] is null)
        {
            return null;
        }

        var name = (string?)manifest["name"];
        var version = (string?)manifest["version"];
        var description = (string?)manifest["description"];
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version) || description is null)
        {
            return null;
        }

        var server = manifest["server"] as JsonObject;
        var fields = new List<ExtensionConfigField>();
        if (manifest["user_config"] is JsonObject userConfig)
        {
            foreach (var (key, value) in userConfig)
            {
                if (value is not JsonObject field)
                {
                    continue;
                }

                fields.Add(new ExtensionConfigField(
                    key,
                    (string?)field["type"] ?? "string",
                    (string?)field["title"] ?? key,
                    (string?)field["description"] ?? "",
                    (bool?)field["required"] ?? false,
                    (bool?)field["multiple"] ?? false,
                    (bool?)field["sensitive"] ?? false,
                    field["default"]?.DeepClone(),
                    (double?)field["min"],
                    (double?)field["max"]));
            }
        }

        return new ExtensionManifest(
            name!,
            (string?)manifest["display_name"],
            version!,
            description,
            (string?)manifest["long_description"],
            (manifest["author"] as JsonObject) is { } author ? (string?)author["name"] : null,
            (string?)manifest["homepage"],
            (string?)manifest["documentation"],
            (string?)manifest["support"],
            (string?)manifest["icon"],
            [.. (manifest["keywords"] as JsonArray ?? []).Select(k => (string?)k ?? "").Where(k => k.Length > 0)],
            (string?)manifest["license"],
            [.. ((manifest["compatibility"] as JsonObject)?["platforms"] as JsonArray ?? [])
                .Select(p => (string?)p ?? "").Where(p => p.Length > 0)],
            (string?)server?["type"],
            (string?)server?["entry_point"],
            server?["mcp_config"] as JsonObject,
            fields);
    }

    // ---- installing ----

    /// <summary>Unpacks a bundle into the extensions folder; returns the installed entry or null with a reason.</summary>
    public static (InstalledExtension? Extension, string? Error) Install(
        ProfilePaths paths, string bundlePath, string? directoryId = null,
        string? expectedName = null, string? expectedVersion = null)
    {
        string? staged = null;
        try
        {
            var prepared = StageBundle(paths, bundlePath, expectedName, expectedVersion);
            staged = prepared.Directory;
            return (CommitBundle(paths, prepared.Directory, prepared.Manifest, directoryId), null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (null, "The extension could not be installed: " + ex.Message);
        }
        finally { if (staged is not null) DeleteUpdateDirectory(paths, staged); }
    }

    internal const long MaxBundleBytes = 2048L * 1024 * 1024;
    internal static string UpdateRoot(ProfilePaths paths) => Path.Combine(paths.Root, "extension-updates");
    private static readonly Lock MutationLock = new();

    internal static (string Directory, ExtensionManifest Manifest) StageBundle(
        ProfilePaths paths, string bundlePath, string? expectedName = null, string? expectedVersion = null)
    {
        using var archive = ZipFile.OpenRead(bundlePath);
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("The bundle has no manifest.json.");
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("The extension manifest is too large.");
        using var reader = new StreamReader(entry.Open());
        var manifest = ParseManifest(reader.ReadToEnd()) ?? throw new InvalidDataException("The extension manifest could not be read.");
        var id = Sanitize(manifest.Name);
        if (id is "" or "." or "..") throw new InvalidDataException("The extension name is invalid.");
        if (expectedName is not null && manifest.Name != expectedName || expectedVersion is not null && manifest.Version != expectedVersion)
            throw new InvalidDataException("The update does not match the installed extension and requested version.");
        if (manifest.Platforms.Count > 0 && !manifest.Platforms.Contains("win32", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("This extension does not support Windows.");
        var target = Path.Combine(UpdateRoot(paths), "stage", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(target);
            ExtractSafely(archive, target);
            return (target, manifest);
        }
        catch { DeleteUpdateDirectory(paths, target); throw; }
    }

    private static InstalledExtension CommitBundle(
        ProfilePaths paths, string staged, ExtensionManifest manifest, string? directoryId)
    {
        lock (MutationLock)
        {
            var id = Sanitize(manifest.Name);
            var target = Path.GetFullPath(Path.Combine(Root(paths), id));
            RequireInside(Root(paths), target);
            var entries = List(paths).ToList();
            var previous = entries.FirstOrDefault(entry => entry.Id == id);
            var installed = new InstalledExtension(id, target, manifest,
                previous?.UserConfig ?? new Dictionary<string, JsonNode?>(), previous?.Enabled ?? true) { DirectoryId = directoryId };
            var stateFile = StateFile(paths);
            var state = File.Exists(stateFile) ? File.ReadAllBytes(stateFile) : null;
            if (state is not null && ReadState(paths) is null) throw new InvalidDataException("The saved extension configuration could not be read.");
            var mcpFile = McpFile(paths);
            var mcp = File.Exists(mcpFile) ? File.ReadAllBytes(mcpFile) : null;
            var backup = Path.Combine(UpdateRoot(paths), "backup", id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            Directory.CreateDirectory(Root(paths));
            var movedOld = false;
            var movedNew = false;
            try
            {
                if (Directory.Exists(target)) { Directory.Move(target, backup); movedOld = true; }
                Directory.Move(staged, target);
                movedNew = true;
                entries.RemoveAll(entry => entry.Id == id);
                entries.Add(installed);
                WriteStateCore(paths, entries);
            }
            catch
            {
                if (movedNew) { RequireInside(Root(paths), target); Directory.Delete(target, recursive: true); }
                if (movedOld) Directory.Move(backup, target);
                Restore(stateFile, state);
                Restore(mcpFile, mcp);
                throw;
            }
            DeleteUpdateDirectory(paths, backup);
            return installed;
        }
    }

    private static void Restore(string path, byte[]? contents)
    {
        if (contents is null) { if (File.Exists(path)) File.Delete(path); return; }
        if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(contents)) File.WriteAllBytes(path, contents);
    }

    internal static void DeleteUpdateDirectory(ProfilePaths paths, string directory)
    {
        RequireInside(UpdateRoot(paths), directory);
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void RequireInside(string root, string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The extension path is outside its storage folder.");
    }

    /// <summary>A zip entry may not escape the target directory.</summary>
    private static void ExtractSafely(ZipArchive archive, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        long total = 0;
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            total = checked(total + entry.Length);
            if (total > MaxBundleBytes) throw new InvalidDataException("The extension exceeds the 2048 MB size limit.");
            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("An extension entry is outside its installation folder.");
            }
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("The extension contains a symbolic link.");

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            if (!destinations.Add(destination)) throw new InvalidDataException("The extension contains duplicate file paths.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex Unsafe();

    public static string Sanitize(string name) => Unsafe().Replace(name, "-").Trim('-');

    public static void Uninstall(ProfilePaths paths, InstalledExtension extension)
    {
        try
        {
            RequireInside(Root(paths), extension.Directory);
            if (Directory.Exists(extension.Directory))
            {
                Directory.Delete(extension.Directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        WriteState(paths, List(paths));
    }

    // ---- reading what is installed ----

    public static IReadOnlyList<InstalledExtension> List(ProfilePaths paths)
    {
        var root = Root(paths);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var state = ReadState(paths);
        var installed = new List<InstalledExtension>();
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            ExtensionManifest? manifest;
            try
            {
                manifest = ParseManifest(File.ReadAllText(manifestPath));
            }
            catch (IOException)
            {
                continue;
            }

            if (manifest is null)
            {
                continue;
            }

            var id = Path.GetFileName(directory);
            var entry = state?[id] as JsonObject;
            var enabled = (bool?)entry?["enabled"] ?? true;
            var config = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            if (entry?["config"] is JsonObject stored)
            {
                foreach (var (key, value) in stored)
                {
                    config[key] = value?.DeepClone();
                }
            }

            installed.Add(new InstalledExtension(id, directory, manifest, config, enabled)
            {
                DirectoryId = (string?)entry?["directoryId"],
            });
        }

        return installed;
    }

    private static JsonObject? ReadState(ProfilePaths paths)
    {
        try
        {
            var file = StateFile(paths);
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, JsonNode?> ReadStoredConfig(ProfilePaths paths, string id)
    {
        var config = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (ReadState(paths)?[id] is JsonObject entry && entry["config"] is JsonObject stored)
        {
            foreach (var (key, value) in stored)
            {
                config[key] = value?.DeepClone();
            }
        }

        return config;
    }

    /// <summary>Stores the enabled flag and the filled-in configuration, then regenerates the MCP file.</summary>
    public static void Save(ProfilePaths paths, IReadOnlyList<InstalledExtension> extensions)
    {
        WriteState(paths, extensions);
    }

    private static void WriteState(ProfilePaths paths, IReadOnlyList<InstalledExtension> extensions)
    {
        try { lock (MutationLock) WriteStateCore(paths, extensions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void WriteStateCore(ProfilePaths paths, IReadOnlyList<InstalledExtension> extensions)
    {
        var root = new JsonObject();
        foreach (var extension in extensions)
        {
            var config = new JsonObject();
            foreach (var (key, value) in extension.UserConfig)
            {
                config[key] = value?.DeepClone();
            }

            root[extension.Id] = new JsonObject
            {
                ["enabled"] = extension.Enabled,
                ["config"] = config,
                ["directoryId"] = extension.DirectoryId,
            };
        }

        Directory.CreateDirectory(Root(paths));
        File.WriteAllText(StateFile(paths), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        WriteMcpFile(paths, extensions);
    }

    /// <summary>Regenerates the servers file every session load joins as an extra MCP config.</summary>
    public static void WriteMcpFile(ProfilePaths paths, IReadOnlyList<InstalledExtension> extensions)
    {
        var servers = new JsonObject();
        foreach (var extension in extensions.Where(e => e.Enabled))
        {
            if (ResolveServer(extension) is { } entry)
            {
                servers[extension.Manifest.Name] = entry;
            }
        }

        Directory.CreateDirectory(Root(paths));
        File.WriteAllText(
            McpFile(paths),
            new JsonObject { ["mcpServers"] = servers }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---- resolving one extension's server (the reference's lYe) ----

    /// <summary>Whether a required field is still unfilled (the reference's <c>dYe</c>).</summary>
    public static bool MissingRequiredConfig(InstalledExtension extension)
    {
        foreach (var field in extension.Manifest.UserConfig.Where(f => f.Required))
        {
            var value = extension.UserConfig.GetValueOrDefault(field.Key) ?? field.Default;
            if (IsEmpty(value))
            {
                return true;
            }

            if (value is JsonArray array && (array.Count == 0 || array.Any(IsEmpty)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEmpty(JsonNode? value) =>
        value is null || (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0);

    /// <summary>The <c>mcpServers</c> entry an extension contributes, or null when it contributes none.</summary>
    public static JsonObject? ResolveServer(InstalledExtension extension)
    {
        if (extension.Manifest.McpConfig is not { } config || MissingRequiredConfig(extension))
        {
            return null;
        }

        var resolved = (JsonObject)config.DeepClone();
        if (resolved["platform_overrides"] is JsonObject overrides &&
            overrides["win32"] is JsonObject win32)
        {
            foreach (var key in new[] { "command", "args", "env" })
            {
                if (win32[key] is { } value)
                {
                    resolved[key] = value.DeepClone();
                }
            }
        }

        resolved.Remove("platform_overrides");

        var substitutions = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["__dirname"] = extension.Directory,
            ["pathSeparator"] = Path.DirectorySeparatorChar.ToString(),
            ["/"] = Path.DirectorySeparatorChar.ToString(),
            ["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["DESKTOP"] = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            ["DOCUMENTS"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["DOWNLOADS"] = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };

        foreach (var field in extension.Manifest.UserConfig)
        {
            var value = extension.UserConfig.GetValueOrDefault(field.Key) ?? field.Default;
            if (value is not null)
            {
                substitutions["user_config." + field.Key] = value.DeepClone();
            }
        }

        return (JsonObject)Substitute(resolved, substitutions)!;
    }

    /// <summary>
    /// The reference's <c>cYe</c>: a <c>${key}</c> hole in a string is replaced,
    /// an array element that is exactly one <c>${user_config.key}</c> holding a
    /// list expands into its members, and an array value in a string context is
    /// left alone.
    /// </summary>
    public static JsonNode? Substitute(JsonNode? node, IReadOnlyDictionary<string, JsonNode?> values)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
            {
                foreach (var (key, replacement) in values)
                {
                    var hole = "${" + key + "}";
                    if (!text.Contains(hole, StringComparison.Ordinal) || replacement is JsonArray)
                    {
                        continue;
                    }

                    text = text.Replace(hole, Scalar(replacement), StringComparison.Ordinal);
                }

                return JsonValue.Create(text);
            }

            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var element in array)
                {
                    if (element is JsonValue v && v.TryGetValue<string>(out var text) &&
                        WholeUserConfigHole().Match(text) is { Success: true } match &&
                        values.TryGetValue(match.Groups[1].Value, out var replacement))
                    {
                        if (replacement is JsonArray items)
                        {
                            foreach (var item in items)
                            {
                                result.Add(JsonValue.Create(Scalar(item)));
                            }
                        }
                        else
                        {
                            result.Add(JsonValue.Create(Scalar(replacement)));
                        }

                        continue;
                    }

                    result.Add(Substitute(element?.DeepClone(), values));
                }

                return result;
            }

            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    result[key] = Substitute(value?.DeepClone(), values);
                }

                return result;
            }

            default:
                return node?.DeepClone();
        }
    }

    [GeneratedRegex(@"^\$\{(user_config\.[^}]+)\}$")]
    private static partial Regex WholeUserConfigHole();

    /// <summary>How a value reads inside a string: a boolean is "true"/"false", everything else its text.</summary>
    private static string Scalar(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString().Trim('"'),
    };
}
