using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Settings;

namespace JarvisCode.App.Services;

/// <summary>One saved inference configuration: its id, its name and the file it lives in.</summary>
public sealed record InferenceConfigEntry(string Id, string Name, string Path, string? Provider, string? Note);

/// <summary>
/// The named configurations the reference's "Configure third-party inference"
/// window keeps (ion-dist <c>c71860c77-DuPx-LoQ.js</c>): a list you switch between,
/// with New configuration, Duplicate…, Rename…, Import configuration…, Export,
/// Show in Explorer and Delete, one of them marked "applied". The reference's
/// configuration is the whole third-party inference profile a managed deployment
/// pushes; here it is the part of <see cref="AppSettings"/> that decides where
/// inference goes — the endpoints, the custom providers and the model catalog —
/// stored one JSON file per configuration beside the settings file.
///
/// The reference's read-only rows for a configuration an MDM profile or a
/// bootstrap URL set are not offered: there is no managed-settings channel here to
/// set one, so every configuration this build shows is the user's own.
/// </summary>
public static class InferenceConfigurations
{
    /// <summary>The AppSettings fields a configuration carries — everything that decides where inference goes.</summary>
    public static readonly IReadOnlyList<string> Fields =
    [
        "OllamaBaseUrl", "NvidiaBaseUrl", "OpenRouterBaseUrl", "TokenRouterBaseUrl", "DeepSeekBaseUrl",
        "ZhipuBaseUrl", "MiniMaxBaseUrl", "LlmApiBaseUrl", "LlmApiProtocolName",
        "BedrockRegion", "BedrockAccessKeyId", "VertexProjectId", "VertexRegion",
        "CustomProviders", "CustomModels", "ModelContextOverrides", "DefaultModelId",
    ];

    public static string Root(ProfilePaths paths) => Path.Combine(paths.Root, "inference");

    private static string AppliedFile(ProfilePaths paths) => Path.Combine(Root(paths), "applied.txt");

    /// <summary>The reference's name for a configuration created with no name of its own.</summary>
    public const string UntitledName = "Untitled";

    /// <summary>What the reference names a duplicate.</summary>
    public static string CopyName(string name) => $"{name} copy";

    public static IReadOnlyList<InferenceConfigEntry> List(ProfilePaths paths)
    {
        var root = Root(paths);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var entries = new List<InferenceConfigEntry>();
        foreach (var file in Directory.EnumerateFiles(root, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var document = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                var id = Path.GetFileNameWithoutExtension(file);
                var name = (string?)document?["name"] ?? id;
                var settings = document?["settings"] as JsonObject;
                var provider = ProviderOf(settings);
                var models = (settings?["CustomModels"] as JsonArray)?.Count ?? 0;
                entries.Add(new InferenceConfigEntry(
                    id, name, file, provider,
                    models > 0 ? $"{models} model{(models == 1 ? "" : "s")}" : null));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A configuration file that no longer parses is left out of the list.
            }
        }

        return entries;
    }

    private static string? ProviderOf(JsonObject? settings)
    {
        if (settings?["CustomProviders"] is JsonArray customs && customs.Count > 0)
        {
            return (string?)(customs[0] as JsonObject)?["DisplayName"] ?? "Custom";
        }

        if ((string?)settings?["VertexProjectId"] is { Length: > 0 })
        {
            return "Vertex AI";
        }

        if ((string?)settings?["BedrockAccessKeyId"] is { Length: > 0 })
        {
            return "Bedrock";
        }

        return null;
    }

    public static string? AppliedId(ProfilePaths paths)
    {
        try
        {
            var file = AppliedFile(paths);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void WriteApplied(ProfilePaths paths, string? id)
    {
        try
        {
            Directory.CreateDirectory(Root(paths));
            if (id is null)
            {
                File.Delete(AppliedFile(paths));
            }
            else
            {
                File.WriteAllText(AppliedFile(paths), id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the applied marker costs a dot in a menu, nothing more.
        }
    }

    /// <summary>The live settings as a configuration document.</summary>
    public static JsonObject Snapshot(AppSettings settings, string name)
    {
        var node = JsonSerializer.SerializeToNode(settings) as JsonObject ?? [];
        var kept = new JsonObject();
        foreach (var field in Fields)
        {
            if (node[field] is { } value)
            {
                kept[field] = value.DeepClone();
            }
        }

        return new JsonObject { ["name"] = name, ["settings"] = kept };
    }

    /// <summary>Writes a configuration; returns its id.</summary>
    public static string Save(ProfilePaths paths, string? id, string name, AppSettings settings)
    {
        id ??= Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Root(paths));
        File.WriteAllText(
            Path.Combine(Root(paths), id + ".json"),
            Snapshot(settings, name).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return id;
    }

    public static string Duplicate(ProfilePaths paths, InferenceConfigEntry source, string name)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var document = Read(source) ?? new JsonObject { ["settings"] = new JsonObject() };
        document["name"] = name;
        Directory.CreateDirectory(Root(paths));
        File.WriteAllText(
            Path.Combine(Root(paths), id + ".json"),
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return id;
    }

    public static void Rename(InferenceConfigEntry entry, string name)
    {
        var document = Read(entry);
        if (document is null)
        {
            return;
        }

        document["name"] = name;
        File.WriteAllText(entry.Path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Delete(ProfilePaths paths, InferenceConfigEntry entry)
    {
        try
        {
            File.Delete(entry.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (AppliedId(paths) == entry.Id)
        {
            WriteApplied(paths, null);
        }
    }

    public static JsonObject? Read(InferenceConfigEntry entry)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(entry.Path)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies a configuration's fields onto the live settings and marks it applied.
    /// The reference asks to relaunch after this, because the running process keeps
    /// the endpoints it started with; so does this app.
    /// </summary>
    public static void Apply(ProfilePaths paths, InferenceConfigEntry entry, AppSettings settings)
    {
        if (Read(entry)?["settings"] is not JsonObject stored)
        {
            return;
        }

        var live = JsonSerializer.SerializeToNode(settings) as JsonObject ?? [];
        foreach (var (key, value) in stored)
        {
            live[key] = value?.DeepClone();
        }

        var updated = live.Deserialize<AppSettings>();
        if (updated is null)
        {
            return;
        }

        foreach (var field in Fields)
        {
            var property = typeof(AppSettings).GetProperty(field);
            if (property is { CanWrite: true })
            {
                property.SetValue(settings, property.GetValue(updated));
            }
        }

        WriteApplied(paths, entry.Id);
    }

    /// <summary>The three refusals the reference gives an import that cannot be read.</summary>
    public const string ImportTooLarge = "Couldn’t import the file. It’s too large.";

    public const string ImportUnreadable = "Couldn’t read the selected file.";
    public const string ImportNotJson = "Couldn’t import the file. It isn’t valid JSON.";
    public const string ImportNoSettings = "Couldn’t import the file. It doesn’t contain any configuration settings.";

    /// <summary>The reference's import size guard, in bytes.</summary>
    public const long MaxImportBytes = 1 << 20;

    /// <summary>Reads a configuration file the user picked; returns its id or the refusal.</summary>
    public static (string? Id, string? Error) Import(ProfilePaths paths, string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxImportBytes)
            {
                return (null, ImportTooLarge);
            }

            var text = File.ReadAllText(path);
            JsonObject? document;
            try
            {
                document = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                return (null, ImportNotJson);
            }

            if (document is null)
            {
                return (null, ImportNotJson);
            }

            var settings = document["settings"] as JsonObject ?? document;
            if (!Fields.Any(f => settings[f] is not null))
            {
                return (null, ImportNoSettings);
            }

            var id = Guid.NewGuid().ToString("N")[..8];
            var name = (string?)document["name"] ?? Path.GetFileNameWithoutExtension(path);
            Directory.CreateDirectory(Root(paths));
            File.WriteAllText(
                Path.Combine(Root(paths), id + ".json"),
                new JsonObject { ["name"] = name, ["settings"] = settings.DeepClone() }
                    .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (id, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ImportUnreadable);
        }
    }

    /// <summary>Whether a configuration carries a value the reference would call sensitive.</summary>
    public static bool HasSensitiveValues(JsonObject? document) =>
        (string?)((document?["settings"] as JsonObject)?["BedrockAccessKeyId"]) is { Length: > 0 };
}
