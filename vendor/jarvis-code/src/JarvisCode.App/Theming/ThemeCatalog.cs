using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Theming;

public enum ThemeSection { Stock, Custom, Gaming, Common }

public sealed record ThemeCatalogEntry(ThemeDefinition Theme, ThemeSection Section);

/// <summary>
/// The full palette catalog: the stock palette, user themes from the profile's
/// themes directory, and the bundled built-in / gaming / community palettes.
/// </summary>
public sealed class ThemeCatalog
{
    public const string StockKey = "";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nordic"] = "nord",
    };

    private readonly Dictionary<string, ThemeCatalogEntry> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ThemeCatalogEntry> All { get; }

    private ThemeCatalog(List<ThemeCatalogEntry> entries)
    {
        All = entries;
        foreach (var entry in entries)
        {
            _byKey[entry.Theme.Key] = entry;
        }
    }

    public ThemeDefinition? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (Aliases.TryGetValue(key, out var target))
        {
            key = target;
        }

        return _byKey.TryGetValue(key, out var entry) ? entry.Theme : null;
    }

    public static ThemeCatalog Load(string? userThemesDirectory)
    {
        var entries = new List<ThemeCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // User themes win over bundled ones with the same key.
        foreach (var theme in LoadUserThemes(userThemesDirectory))
        {
            if (seen.Add(theme.Key))
            {
                entries.Add(new ThemeCatalogEntry(theme, ThemeSection.Custom));
            }
        }

        foreach (var theme in LoadEmbedded("builtin_themes.json"))
        {
            if (seen.Add(theme.Key))
            {
                var section = string.Equals(theme.Category, "gaming", StringComparison.OrdinalIgnoreCase)
                    ? ThemeSection.Gaming
                    : ThemeSection.Common;
                entries.Add(new ThemeCatalogEntry(theme, section));
            }
        }

        foreach (var theme in LoadEmbedded("gaming_themes.json"))
        {
            if (seen.Add(theme.Key))
            {
                entries.Add(new ThemeCatalogEntry(theme, ThemeSection.Gaming));
            }
        }

        foreach (var theme in LoadEmbedded("community_themes.json"))
        {
            if (seen.Add(theme.Key))
            {
                entries.Add(new ThemeCatalogEntry(theme, ThemeSection.Common));
            }
        }

        entries.Sort(static (a, b) =>
        {
            var bySection = a.Section.CompareTo(b.Section);
            return bySection != 0
                ? bySection
                : string.Compare(a.Theme.DisplayName, b.Theme.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        return new ThemeCatalog(entries);
    }

    private static IEnumerable<ThemeDefinition> LoadUserThemes(string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(static f => f, StringComparer.OrdinalIgnoreCase))
        {
            ThemeDefinition? theme = null;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(file));
                if (node is JsonObject obj)
                {
                    theme = FromNode(Path.GetFileNameWithoutExtension(file), obj);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken custom theme file must not take the whole catalog down.
            }

            if (theme is not null)
            {
                yield return theme;
            }
        }
    }

    private static IEnumerable<ThemeDefinition> LoadEmbedded(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith($".Assets.Themes.{fileName}", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded theme resource '{resourceName}'.");
        using var reader = new StreamReader(stream);
        var root = JsonNode.Parse(reader.ReadToEnd()) as JsonObject
            ?? throw new InvalidOperationException($"Theme resource '{fileName}' is not a JSON object.");

        foreach (var (key, value) in root)
        {
            if (value is JsonObject obj && FromNode(key, obj) is { } theme)
            {
                yield return theme;
            }
        }
    }

    private static ThemeDefinition? FromNode(string key, JsonObject obj)
    {
        var light = ReadTokenMap(obj["light"]);
        var dark = ReadTokenMap(obj["dark"]);

        if (light is null && dark is null)
        {
            // Flat themes (a bare token map) apply to both variants.
            var flat = ReadTokenMap(obj);
            if (flat is null || flat.Count == 0)
            {
                return null;
            }

            light = flat;
            dark = flat;
        }

        SpinnerSpec? spinner = null;
        if (obj["spinner"] is JsonObject spinnerNode)
        {
            spinner = spinnerNode.Deserialize<SpinnerSpec>();
        }

        return new ThemeDefinition
        {
            Key = key,
            Name = obj["name"]?.GetValue<string>(),
            Category = obj["category"]?.GetValue<string>(),
            Light = light ?? dark!,
            Dark = dark ?? light!,
            Spinner = spinner,
        };
    }

    private static Dictionary<string, string>? ReadTokenMap(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in obj)
        {
            if (key.StartsWith("--", StringComparison.Ordinal) && value is JsonValue v && v.TryGetValue<string>(out var s))
            {
                map[key] = s;
            }
        }

        return map;
    }
}
