using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JarvisCode.Cli;

/// <summary>
/// `jarvis plugin validate` - the reference's manifest validator, measured
/// against CLI 2.1.260 by running it over hand-built manifests and comparing
/// stdout and the exit code for every flag combination.
///
/// <para>
/// It has two modes, and which one a directory takes is measured rather than
/// assumed: a manifest under .claude-plugin wins (marketplace.json ahead of
/// plugin.json) and is reported as "Validating {kind} manifest: {file}";
/// otherwise a directory holding skills is reported as "Validating components
/// in: {dir}" with a null manifest, and only a directory with neither gets the
/// no-manifest error. A component appears in the report only when it has
/// something to say - a valid skill contributes no block and no contents entry.
/// </para>
/// <para>
/// What is deliberately shallower is the diagnostic set. The reference runs a
/// zod schema per manifest kind, so it can report a per-field type error for
/// every key; this carries the checks that were measured stating a sentence -
/// the manifest itself, the required keys, unknown keys, the two placement
/// rules, whether a declared component path exists, and the three a skill
/// raises. The gap is declared in Deltas/reference-surface-deltas.tsv.
/// </para>
/// </summary>
internal static class PluginValidation
{
    /// <summary>One diagnostic. Its code is always null in the reference's own output.</summary>
    private sealed record Note(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("code")] string? Code = null);

    /// <summary>One validated file: a manifest, or a component under it.</summary>
    private sealed record Subject(string File, string Kind, List<Note> Errors, List<Note> Warnings)
    {
        public bool Quiet => Errors.Count == 0 && Warnings.Count == 0;

        public JsonObject ToJson() => new()
        {
            ["file"] = File,
            ["type"] = Kind,
            ["errors"] = Array(Errors),
            ["warnings"] = Array(Warnings),
            ["notes"] = new JsonArray(),
        };
    }

    /// <summary>
    /// The keys plugin.json declares. The strict key is known but belongs in the
    /// marketplace entry, which is why it is listed here and warned about below.
    /// </summary>
    private static readonly HashSet<string> PluginKeys = new(StringComparer.Ordinal)
    {
        "name", "description", "version", "author", "homepage", "repository", "license",
        "keywords", "commands", "agents", "skills", "hooks", "mcpServers", "monitors",
        "experimental", "strict",
    };

    private static readonly HashSet<string> MarketplaceKeys = new(StringComparer.Ordinal)
    {
        "name", "description", "owner", "metadata", "plugins",
    };

    /// <summary>
    /// The keys whose value names a path the runtime loader will open, in the
    /// order the reference was measured reporting them - which is not the order
    /// the schema declares them in, and not the order the manifest writes them.
    /// mcpServers and monitors are deliberately absent: a missing path under
    /// either was measured raising nothing.
    /// </summary>
    private static readonly string[] PathKeys = ["commands", "hooks", "agents", "skills"];

    /// <summary>
    /// The path keys whose schema wants an array, so a bare string is an error
    /// as well as a path to check. Measured on agents alone: hooks and skills
    /// take a string without complaint.
    /// </summary>
    private static readonly string[] ArrayPathKeys = ["agents"];

    /// <summary>
    /// The path keys that label a bare string with an index anyway, because the
    /// schema coerced it into a one-element array before reporting.
    /// </summary>
    private static readonly string[] IndexedPathKeys = ["hooks", "agents", "skills"];

    /// <summary>
    /// The reference prints its report with JSON.stringify, which escapes none
    /// of the characters this serializer escapes by default - an apostrophe in a
    /// message would come back as a numeric escape and stop reading as the
    /// reference's own text.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOutput = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(IReadOnlyList<string> args)
    {
        var json = false;
        var strict = false;
        string? target = null;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--json": json = true; break;
                case "--strict": strict = true; break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"error: unknown option {Quoted(arg)}");
                        return 1;
                    }

                    target ??= arg;
                    break;
            }
        }

        if (target is null)
        {
            Console.Error.WriteLine($"error: missing required argument {Quoted("path")}");
            return 1;
        }

        var full = Path.GetFullPath(target);
        var (manifest, contents) = Validate(full);

        var errors = (manifest?.Errors.Count ?? 0) + contents.Sum(static c => c.Errors.Count);
        var warnings = (manifest?.Warnings.Count ?? 0) + contents.Sum(static c => c.Warnings.Count);
        var failed = errors > 0 || (strict && warnings > 0);

        if (json)
        {
            var document = new JsonObject
            {
                ["success"] = !failed,
                ["strict"] = strict,
                ["target"] = manifest?.File ?? full,
                ["manifest"] = manifest?.ToJson(),
                ["contents"] = new JsonArray(
                    [.. contents.Where(static c => !c.Quiet).Select(static c => (JsonNode?)c.ToJson())]),
            };
            Console.WriteLine(document.ToJsonString(JsonOutput));
            return failed ? 1 : 0;
        }

        if (manifest is not null)
        {
            Console.WriteLine($"Validating {manifest.Kind} manifest: {manifest.File}");
            Report(manifest);
        }
        else
        {
            Console.WriteLine($"Validating components in: {full}");
            foreach (var component in contents.Where(static c => !c.Quiet))
            {
                Console.WriteLine();
                Console.WriteLine($"Validating {component.Kind}: {component.File}");
                Report(component);
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            // A run that failed only because --strict promoted its warnings says
            // so; one carrying a real error reports the plain sentence.
            failed && errors == 0 ? "✘ Validation failed (--strict treats warnings as errors)"
            : failed ? "✘ Validation failed"
            : warnings > 0 ? "✔ Validation passed with warnings"
            : "✔ Validation passed");
        return failed ? 1 : 0;
    }

    /// <summary>A value in single quotes, which commander uses in its own errors.</summary>
    private static string Quoted(string value)
    {
        var quote = (char)39;
        return quote + value + quote;
    }

    private static JsonArray Array(List<Note> notes) =>
        new([.. notes.Select(static n => JsonSerializer.SerializeToNode(n))]);

    private static void Report(Subject subject)
    {
        Report("✘", "error", subject.Errors);
        Report("⚠", "warning", subject.Warnings);
    }

    private static void Report(string glyph, string noun, List<Note> notes)
    {
        if (notes.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{glyph} Found {notes.Count} {noun}{(notes.Count == 1 ? "" : "s")}:");
        Console.WriteLine();
        foreach (var note in notes)
        {
            Console.WriteLine($"  ❯ {note.Path}: {note.Message}");
        }
    }

    private static (Subject? Manifest, List<Subject> Contents) Validate(string target)
    {
        var (file, kind) = Resolve(target);
        if (file is not null)
        {
            return (ValidateManifest(file, kind), []);
        }

        // No manifest: a directory holding skills is validated as loose
        // components, and one holding nothing recognisable is the error.
        var skills = SkillFiles(target).ToList();
        if (skills.Count == 0)
        {
            var missing = new Subject(target, "plugin", [], []);
            missing.Errors.Add(new Note(
                "directory",
                "No manifest found in directory. Expected .claude-plugin/marketplace.json " +
                "or .claude-plugin/plugin.json"));
            return (missing, []);
        }

        return (null, [.. skills.Select(ValidateSkill)]);
    }

    private static IEnumerable<string> SkillFiles(string directory)
    {
        var skills = Path.Combine(directory, "skills");
        if (!Directory.Exists(skills))
        {
            yield break;
        }

        foreach (var candidate in Directory.EnumerateFiles(skills, "SKILL.md", SearchOption.AllDirectories))
        {
            yield return candidate;
        }
    }

    /// <summary>
    /// A skill file. The three diagnostics carried are the ones the reference
    /// was measured raising: an absent frontmatter block, a non-string name and
    /// an absent description.
    /// </summary>
    private static Subject ValidateSkill(string file)
    {
        var subject = new Subject(file, "skill", [], []);
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (IOException exception)
        {
            subject.Errors.Add(new Note("file", exception.Message));
            return subject;
        }

        var frontmatter = Frontmatter(text);
        if (frontmatter is null)
        {
            subject.Warnings.Add(new Note(
                "frontmatter",
                "No frontmatter block found. Add YAML frontmatter between --- delimiters at the " +
                "top of the file to set description and other metadata."));
            return subject;
        }

        if (frontmatter.TryGetValue("name", out var name) && ScalarKind(name) is { } wrong)
        {
            subject.Errors.Add(new Note("name", $"name must be a string, got {wrong}."));
        }

        if (!frontmatter.ContainsKey("description"))
        {
            subject.Warnings.Add(new Note(
                "description",
                "No description in frontmatter. A description helps users and Jarvis understand " +
                "when to use this skill."));
        }

        return subject;
    }

    /// <summary>The YAML scalar type a bare value reads as, or null when it is a string.</summary>
    private static string? ScalarKind(string value) =>
        value.Length > 0 && value.All(static c => char.IsAsciiDigit(c) || c == '.' || c == '-') ? "number"
        : value is "true" or "false" ? "boolean"
        : null;

    /// <summary>The top-level scalars of a leading --- block, or null when there is none.</summary>
    private static Dictionary<string, string>? Frontmatter(string text)
    {
        var normalized = text.ReplaceLineEndings("\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return null;
        }

        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in normalized[4..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return fields;
    }

    private static Subject ValidateManifest(string file, string kind)
    {
        var subject = new Subject(file, kind, [], []);
        JsonObject? manifest = null;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        }
        catch (JsonException exception)
        {
            subject.Errors.Add(new Note("file", $"Invalid JSON: {exception.Message}"));
        }
        catch (IOException exception)
        {
            subject.Errors.Add(new Note("file", exception.Message));
        }

        if (manifest is null)
        {
            if (subject.Errors.Count == 0)
            {
                subject.Errors.Add(new Note("file", "Manifest is not a JSON object"));
            }

            return subject;
        }

        var known = kind == "marketplace" ? MarketplaceKeys : PluginKeys;
        foreach (var (key, value) in manifest)
        {
            if (!known.Contains(key))
            {
                subject.Warnings.Add(new Note(
                    key, $"Unknown field {Quoted(key)}. Jarvis Code ignores it at load time."));
                continue;
            }

        }

        // The two placement warnings keep this order whatever order the manifest
        // declares the keys in - measured on a manifest writing monitors first
        // and still being told about strict first.
        if (kind == "plugin" && manifest["strict"] is not null)
        {
            subject.Warnings.Add(new Note(
                "strict",
                $"Field {Quoted("strict")} belongs in the marketplace entry (marketplace.json), " +
                "not plugin.json. It's harmless here but unused — Jarvis Code " +
                "ignores it at load time."));
        }

        if (kind == "plugin" && manifest["monitors"] is not null)
        {
            subject.Warnings.Add(new Note(
                "monitors",
                $"{Quoted("monitors")} is an experimental component; declare it under " +
                $"{Quoted("experimental.monitors")} instead of at the top level. Top-level still " +
                "loads for now but will be removed in a future release."));
        }

        // The schema errors come first and in the schema's own field order, and
        // the path errors after them in their own - both measured, and neither
        // is the order the manifest declares its keys in.
        if (manifest["name"] is not JsonValue name || name.GetValueKind() != JsonValueKind.String)
        {
            subject.Errors.Add(new Note("name", "Invalid input"));
        }

        if (kind == "plugin" && manifest.TryGetPropertyValue("version", out var version)
            && (version is not JsonValue declared || declared.GetValueKind() != JsonValueKind.String))
        {
            subject.Errors.Add(new Note("version", "Invalid input"));
        }

        foreach (var key in ArrayPathKeys)
        {
            if (kind == "plugin" && manifest[key] is JsonValue)
            {
                subject.Errors.Add(new Note(key, "Invalid input"));
            }
        }

        var schemaFailed = subject.Errors.Count > 0;

        if (kind == "plugin")
        {
            foreach (var key in PathKeys)
            {
                CheckPaths(Path.GetDirectoryName(file)!, key, manifest[key], subject.Errors);
            }
        }

        // The metadata warnings follow the per-key ones and keep this order, but
        // only on a manifest whose schema parsed: a run that reported a field
        // error was measured emitting none of them.
        if (kind == "plugin" && !schemaFailed)
        {
            if (manifest["version"] is null)
            {
                subject.Warnings.Add(new Note(
                    "version",
                    "No version specified. Consider adding a version following semver (e.g., \"1.0.0\")"));
            }

            if (manifest["description"] is null)
            {
                subject.Warnings.Add(new Note(
                    "description",
                    "No description provided. Adding a description helps users understand " +
                    "what your plugin does"));
            }

            if (manifest["author"] is null)
            {
                subject.Warnings.Add(new Note(
                    "author",
                    "No author information provided. Consider adding author details for plugin attribution"));
            }
        }

        if (kind == "marketplace" && manifest["description"] is null)
        {
            subject.Warnings.Add(new Note(
                "description",
                "No marketplace description provided. Adding a description helps users understand " +
                "what this marketplace offers"));
        }

        return subject;
    }

    /// <summary>
    /// A declared component path is opened by the loader, so one that is not
    /// there is an error rather than a warning - the reference's own wording.
    /// GetFullPath normalises the leading "./" a manifest usually writes.
    /// </summary>
    private static void CheckPaths(string manifestDirectory, string key, JsonNode? value, List<Note> errors)
    {
        // The plugin root is the directory holding .claude-plugin, so a relative
        // path in the manifest resolves against its parent.
        var root = Path.GetDirectoryName(manifestDirectory) ?? manifestDirectory;
        foreach (var (path, label) in Declared(key, value))
        {
            var resolved = Path.GetFullPath(Path.Combine(root, path));
            if (!File.Exists(resolved) && !Directory.Exists(resolved))
            {
                errors.Add(new Note(
                    label,
                    $"Path not found: {path}. The runtime loader will report this as a load failure."));
            }
        }
    }

    private static IEnumerable<(string Path, string Label)> Declared(string key, JsonNode? value)
    {
        switch (value)
        {
            case JsonValue single when single.GetValueKind() == JsonValueKind.String:
                // A key whose schema wants an array labels its bare string [0],
                // because the value was coerced before it was reported.
                yield return (
                    single.GetValue<string>(),
                    IndexedPathKeys.Contains(key) ? $"{key}[0]" : key);
                break;
            case JsonArray many:
                for (var i = 0; i < many.Count; i++)
                {
                    if (many[i] is JsonValue item && item.GetValueKind() == JsonValueKind.String)
                    {
                        yield return (item.GetValue<string>(), $"{key}[{i}]");
                    }
                }

                break;
        }
    }

    private static (string? File, string Kind) Resolve(string target)
    {
        if (File.Exists(target))
        {
            return (target,
                Path.GetFileName(target).Equals("marketplace.json", StringComparison.OrdinalIgnoreCase)
                    ? "marketplace"
                    : "plugin");
        }

        var marketplace = Path.Combine(target, ".claude-plugin", "marketplace.json");
        if (File.Exists(marketplace))
        {
            return (marketplace, "marketplace");
        }

        var plugin = Path.Combine(target, ".claude-plugin", "plugin.json");
        return File.Exists(plugin) ? (plugin, "plugin") : (null, "plugin");
    }
}
