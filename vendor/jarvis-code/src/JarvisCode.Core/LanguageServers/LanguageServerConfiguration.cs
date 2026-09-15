using System.Text.Json.Nodes;

namespace JarvisCode.Core.LanguageServers;

/// <summary>Explicit plugin configuration, measured against CLI 2.1.260's YQe schema.</summary>
public sealed record LanguageServerConfiguration
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required IReadOnlyDictionary<string, string> ExtensionToLanguage { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string? WorkspaceFolder { get; init; }
    public JsonNode? InitializationOptions { get; init; }
    public JsonNode? Settings { get; init; }
    public int? StartupTimeout { get; init; }
    public int? ShutdownTimeout { get; init; }
    public bool RestartOnCrash { get; init; } = true;
    public int MaxRestarts { get; init; } = 3;
    public bool Diagnostics { get; init; } = true;

    public static IReadOnlyList<LanguageServerConfiguration> Load(IEnumerable<string> files) => LoadFiles(files, null);

    public static IReadOnlyList<LanguageServerConfiguration> LoadValid(IEnumerable<string> files, List<string> warnings) => LoadFiles(files, warnings);

    private static IReadOnlyList<LanguageServerConfiguration> LoadFiles(IEnumerable<string> files, List<string>? warnings)
    {
        var result = new List<LanguageServerConfiguration>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (new FileInfo(file).Length > 4 * 1024 * 1024)
                throw new InvalidDataException($"LSP configuration exceeds 4 MiB: {file}");
            var document = JsonNode.Parse(File.ReadAllText(file)) as JsonObject
                ?? throw new InvalidDataException($"LSP configuration must be an object: {file}");
            foreach (var (name, value) in document)
            {
                try
                {
                    if (names.Contains(name)) throw new InvalidDataException($"Duplicate LSP server name: {name}");
                    if (value is not JsonObject config) throw new InvalidDataException($"LSP server '{name}' must be an object.");
                    var command = RequiredString(config["command"], "command");
                    var transport = config["transport"]?.GetValue<string>() ?? "stdio";
                    // The reference accepts socket in its schema but runs every plugin server over stdio.
                    if (transport is not ("stdio" or "socket"))
                        throw new InvalidDataException($"LSP server '{name}': invalid transport '{transport}'.");
                    var mapping = StringMap(config["extensionToLanguage"], "extensionToLanguage");
                    if (mapping.Count == 0 || mapping.Any(pair => !pair.Key.StartsWith('.') || string.IsNullOrWhiteSpace(pair.Value)))
                        throw new InvalidDataException("extensionToLanguage must contain nonempty language IDs keyed by dot-prefixed extensions.");
                    result.Add(new()
                    {
                        Name = name, Command = command, ExtensionToLanguage = mapping,
                        Arguments = config["args"] is JsonArray args
                            ? args.Select(arg => arg?.GetValue<string>() ?? throw new InvalidDataException("args values must be strings.")).ToArray()
                            : config["args"] is null ? [] : throw new InvalidDataException("args must be an array."),
                        Environment = config["env"] is null ? new Dictionary<string, string>() : StringMap(config["env"], "env"),
                        WorkspaceFolder = config["workspaceFolder"]?.GetValue<string>(),
                        InitializationOptions = config["initializationOptions"]?.DeepClone(), Settings = config["settings"]?.DeepClone(),
                        StartupTimeout = config["startupTimeout"] is null ? null : Integer(config, "startupTimeout", 0, 1),
                        ShutdownTimeout = config["shutdownTimeout"] is null ? null : Integer(config, "shutdownTimeout", 0, 1),
                        RestartOnCrash = config["restartOnCrash"]?.GetValue<bool>() ?? true,
                        MaxRestarts = Integer(config, "maxRestarts", 3, 0),
                        Diagnostics = config["diagnostics"]?.GetValue<bool>() ?? true,
                    });
                    names.Add(name);
                }
                catch (Exception error) when (error is InvalidOperationException or FormatException or InvalidDataException)
                {
                    if (warnings is null) throw new InvalidDataException($"Invalid LSP server '{name}': {error.Message}", error);
                    warnings.Add($"Skipped LSP server '{name}': {error.Message}");
                }
            }
        }
        return result;
    }

    private static string RequiredString(JsonNode? node, string field) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : throw new InvalidDataException($"{field} must be a nonempty string.");

    private static Dictionary<string, string> StringMap(JsonNode? node, string field)
    {
        if (node is not JsonObject map) throw new InvalidDataException($"{field} must be an object.");
        return map.ToDictionary(pair => pair.Key, pair => pair.Value?.GetValue<string>()
            ?? throw new InvalidDataException($"{field} values must be strings."), StringComparer.OrdinalIgnoreCase);
    }

    private static int Integer(JsonObject config, string field, int fallback, int minimum)
    {
        var value = config[field]?.GetValue<int>() ?? fallback;
        return value >= minimum ? value : throw new InvalidDataException($"{field} must be at least {minimum}.");
    }
}
