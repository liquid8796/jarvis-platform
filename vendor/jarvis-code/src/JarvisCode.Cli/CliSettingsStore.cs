using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Settings;

namespace JarvisCode.Cli;

/// <summary>
/// Settings selected by a CLI invocation. Overlays stay in memory, so a language
/// override cannot replace the user's provider accounts or write decrypted keys
/// to a temporary JSON file. Normal runs still save to the normal profile.
/// </summary>
internal sealed class CliSettingsStore(ISettingsStore profile, CliOptions options, string cwd,
    Func<string, string?>? environment = null, string? userSettingsPath = null) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private AppSettings? _current;
    private JsonObject? _profileState;
    private JsonObject? _baseline;
    public string? Agent { get; private set; }
    public string? OutputStyle { get; private set; }
    public string? ApiKeyHelper { get; private set; }
    public IReadOnlyList<JarvisCode.Core.Hooks.HookDefinition> ExplicitHooks { get; private set; } = [];
    public Dictionary<string, string> EnvironmentValues { get; } = new(StringComparer.Ordinal);
    public List<string> AdditionalDirectories { get; } = [];
    public IReadOnlySet<string> Sources { get; } = ParseSources(options.SettingSources);

    public static IReadOnlySet<string> ParseSources(string? source)
    {
        var selected = source is null ? new[] { "user", "project", "local" }
            : source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (selected.Any(value => value is not ("user" or "project" or "local")))
            throw new CliError("--setting-sources accepts only user, project and local.");
        return new HashSet<string>(selected, StringComparer.Ordinal);
    }

    public AppSettings Load()
    {
        if (_current is not null) return _current;
        var stored = options.Bare ? new AppSettings() : profile.Load();
        _profileState = JsonSerializer.SerializeToNode(stored)!.AsObject();
        var current = Sources.Contains("user") && !options.Bare && !options.Restricted ? stored : new AppSettings
        { ApiKeys = stored.ApiKeys, ApiKeySets = stored.ApiKeySets };
        var root = JsonSerializer.SerializeToNode(current)!.AsObject();
        if (!options.Bare && !options.SafeMode && !options.Restricted)
        {
            if (Sources.Contains("user") && userSettingsPath is not null)
            {
                if (File.Exists(userSettingsPath))
                {
                    var raw = Parse(File.ReadAllText(userSettingsPath));
                    var aliases = new JsonObject();
                    foreach (var name in new[] { "model", "language", "effortLevel", "agent", "outputStyle", "apiKeyHelper", "env", "permissions" })
                        if (raw[name] is { } value) aliases[name] = value.DeepClone();
                    Apply(aliases, true, true); // Credentials were already decrypted by the profile store.
                }
                var extra = Path.Combine(Path.GetDirectoryName(userSettingsPath)!, "sdk-settings.json");
                if (File.Exists(extra)) Apply(Parse(File.ReadAllText(extra)), true, true);
            }
            if (Sources.Contains("project")) AddProject("settings.json");
            if (Sources.Contains("local")) AddProject("settings.local.json");
        }
        if (options.Settings is { } explicitSettings)
        {
            var json = explicitSettings.TrimStart().StartsWith('{')
                ? explicitSettings : File.ReadAllText(Path.GetFullPath(explicitSettings, cwd));
            Apply(Parse(json), allowProviderSettings: true, allowElevatedMode: true);
            if (!options.Bare && !options.SafeMode) ExplicitHooks = Core.Hooks.HookRunner.ParseConfiguration(json);
        }
        _current = root.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
        // Bare mode reads only the credential the invoking process explicitly
        // supplied. It never decrypts the profile's stored key sets above.
        if (options.Bare)
        {
            _current.ApiKeys.Remove("anthropic");
            _current.ApiKeySets.Remove("anthropic");
            string? ReadEnvironment(string name) => EnvironmentValues.GetValueOrDefault(name) ??
                (environment ?? Environment.GetEnvironmentVariable)(name);
            var readEnvironment = ReadEnvironment;
            var key = readEnvironment("ANTHROPIC_API_KEY");
            if (!string.IsNullOrWhiteSpace(key)) _current.ApiKeySets["anthropic"] = [key.Trim()];
            if (readEnvironment("ANTHROPIC_BASE_URL") is { Length: > 0 } endpoint)
                _current.AnthropicBaseUrl = endpoint;
        }
        MarkRuntimeInitialized();
        return _current;

        void AddProject(string file)
        {
            var primary = Path.Combine(cwd, ".jarvis", file);
            var compatible = Path.Combine(cwd, ".claude", file);
            var selected = File.Exists(primary) ? primary : compatible;
            if (File.Exists(selected)) Apply(Parse(File.ReadAllText(selected)), false, false);
        }

        void Apply(JsonObject supplied, bool allowProviderSettings, bool allowElevatedMode)
        {
            foreach (var (key, value) in supplied)
            {
                var property = typeof(AppSettings).GetProperties().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                // A repository may select behavior, never redirect the provider
                // endpoint or replace the credentials loaded from the user's profile.
                if (property is not null && (allowProviderSettings || property.Name is
                    "Language" or "ThinkingEffortName" or "AutoCompactEnabled" or "AutoCompactWindow" or "InstructionFileExcludes"))
                    Merge(root, property.Name, value);
                switch (key)
                {
                    case "model": root["DefaultModelId"] = value?.DeepClone(); break;
                    case "language": root["Language"] = value?.DeepClone(); break;
                    case "effortLevel": root["ThinkingEffortName"] = CliOptions.MapEffort(value?.ToString()) ?? value?.ToString(); break;
                    case "agent": Agent = value?.GetValue<string>(); break;
                    case "outputStyle": OutputStyle = value?.GetValue<string>(); break;
                    case "apiKeyHelper" when allowProviderSettings: ApiKeyHelper = value?.GetValue<string>(); break;
                    case "env" when value is JsonObject variables && !options.SafeMode:
                        foreach (var (name, content) in variables)
                        {
                            if (string.IsNullOrEmpty(name) || name.Contains('=') || name.Contains('\0') || content is not JsonValue scalar ||
                                !scalar.TryGetValue<string>(out var text) || text.Contains('\0'))
                                throw new CliError("Settings env must map environment variable names to strings.");
                            EnvironmentValues[name] = text;
                        }
                        break;
                    case "permissions" when value is JsonObject permissions:
                        if (permissions["defaultMode"]?.GetValue<string>() is { } mode &&
                            (allowElevatedMode || mode is "manual" or "acceptEdits" or "plan")) root["PermissionModeName"] = mode;
                        var previousRules = root["PermissionRuleLines"] as JsonArray ?? new JsonArray();
                        var rules = new JsonArray();
                        if (permissions["rules"] is JsonArray nativeRules)
                            foreach (var rule in nativeRules) if (rule?.GetValue<string>() is { } line) rules.Add(line);
                        foreach (var kind in new[] { "deny", "ask", "allow" })
                            if (permissions[kind] is JsonArray entries)
                                foreach (var rule in ToolNames.ToRuleLines(entries.Select(entry => entry!.GetValue<string>()), kind)) rules.Add(rule);
                        foreach (var previous in previousRules) rules.Add(previous?.DeepClone());
                        root["PermissionRuleLines"] = rules;
                        if (permissions["additionalDirectories"] is JsonArray directories)
                            foreach (var entry in directories)
                                if (entry?.GetValue<string>() is { } directory)
                                {
                                    if (Core.Utilities.NetworkPaths.IsNetworkPath(directory)) throw new CliError("Additional directories must be local paths.");
                                    var full = Path.GetFullPath(directory, cwd);
                                    if (!AdditionalDirectories.Contains(full, StringComparer.OrdinalIgnoreCase)) AdditionalDirectories.Add(full);
                                }
                        break;
                }
            }
        }
    }

    private static JsonObject Parse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? throw new CliError("--settings must contain a JSON object."); }
        catch (JsonException ex) { throw new CliError("Invalid settings JSON: " + ex.Message); }
    }

    private static void Merge(JsonObject target, string name, JsonNode? value)
    {
        if (value is JsonObject patch && target[name] is JsonObject existing)
        { foreach (var (key, child) in patch) Merge(existing, key, child); }
        else target[name] = value?.DeepClone();
    }

    public void Save(AppSettings settings)
    {
        _current = settings;
        if (options.Bare || options.SafeMode) return;
        var updated = JsonSerializer.SerializeToNode(settings)!.AsObject();
        var persisted = JsonSerializer.SerializeToNode(profile.Load())!.AsObject();
        foreach (var (key, value) in updated)
            if (!JsonNode.DeepEquals(_baseline?[key], value)) persisted[key] = value?.DeepClone();
        profile.Save(persisted.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings());
        _profileState = persisted;
        _baseline = updated;
    }

    public void MarkRuntimeInitialized() => _baseline = _current is null ? null : JsonSerializer.SerializeToNode(_current)!.AsObject();

    /// <summary>Persists an explicit permission edit against the saved user tier, preserving project/CLI overlays in memory.</summary>
    internal void PersistPermissions(string type, string? mode, string? behavior, IReadOnlyList<string> rules)
    {
        if (options.Bare || options.SafeMode || options.Restricted) throw new CliError("Persistent permission changes are disabled for this invocation.");
        var saved = profile.Load();
        if (type == "setMode") saved.PermissionModeName = mode;
        else saved.PermissionRuleLines = SdkPermissionUpdates.EditRules(saved.PermissionRuleLines, type, behavior!, rules).ToList();
        profile.Save(saved);
        _profileState = JsonSerializer.SerializeToNode(saved)!.AsObject();
    }
}
