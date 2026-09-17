using System.Text;

namespace Jarvis.Agent.Core.Plugins;

public sealed record PluginSkillDescriptor(
    string PluginId,
    string Name,
    string Description,
    string Path,
    long Length)
{
    public string Id => PluginId + "/" + Name;
}

public sealed record PluginSkillDocument(PluginSkillDescriptor Descriptor, string Instructions);
public sealed record PluginSkillDiagnostic(string PluginId, string Path, string Error);
public sealed record PluginSkillCatalogSnapshot(
    IReadOnlyList<PluginSkillDescriptor> Skills,
    IReadOnlyList<PluginSkillDiagnostic> Diagnostics);

/// <summary>
/// Reads bounded Markdown skill instructions from catalog-validated plugin skill roots.
/// This loader never executes plugin entry files or Markdown content.
/// </summary>
public sealed class PluginSkillLoader
{
    public const int MaxSkillBytes = 256 * 1024;
    private const int MaxNameChars = 120;
    private const int MaxDescriptionChars = 500;

    public PluginSkillCatalogSnapshot Discover(PluginCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var skills = new List<PluginSkillDescriptor>();
        var diagnostics = new List<PluginSkillDiagnostic>();

        foreach (var plugin in snapshot.Manifests.OrderBy(plugin => plugin.Id, StringComparer.Ordinal))
        {
            if (!snapshot.SkillRoots.TryGetValue(plugin.Id, out var declaredRoots)) continue;
            foreach (var declaredRoot in declaredRoots.OrderBy(path => path, PathComparer))
            {
                string root;
                try
                {
                    root = Path.GetFullPath(declaredRoot);
                    if (!Directory.Exists(root)) continue;
                    if (IsReparsePoint(root))
                    {
                        diagnostics.Add(new(plugin.Id, root, "Skill root is a reparse point and was rejected."));
                        continue;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new(plugin.Id, declaredRoot, "Invalid skill root: " + ex.Message));
                    continue;
                }

                foreach (var file in EnumerateSafeSkillFiles(plugin.Id, root, diagnostics))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length > MaxSkillBytes)
                        {
                            diagnostics.Add(new(plugin.Id, file, $"Skill size {info.Length} bytes exceeds the {MaxSkillBytes}-byte limit."));
                            continue;
                        }

                        var parsed = Parse(ReadMetadataUtf8(file, info.Length));
                        var name = Clip(string.IsNullOrWhiteSpace(parsed.Name) ? new DirectoryInfo(Path.GetDirectoryName(file)!).Name : parsed.Name!, MaxNameChars);
                        if (string.IsNullOrWhiteSpace(name))
                            throw new InvalidDataException("Skill name is empty.");
                        var description = Clip(string.IsNullOrWhiteSpace(parsed.Description) ? InferDescription(parsed.Instructions) : parsed.Description!, MaxDescriptionChars);
                        skills.Add(new(plugin.Id, name, description, file, info.Length));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException)
                    {
                        diagnostics.Add(new(plugin.Id, file, "Skill metadata rejected: " + Clip(ex.Message, 500)));
                    }
                }
            }
        }

        var deduped = skills
            .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(skill => skill.Path, PathComparer).First())
            .OrderBy(skill => skill.Id, StringComparer.Ordinal)
            .ToArray();
        return new(deduped, diagnostics.Take(128).ToArray());
    }

    public PluginSkillDocument Load(PluginCatalogSnapshot snapshot, PluginSkillDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(descriptor);
        var path = Path.GetFullPath(descriptor.Path);
        var root = ResolveDeclaredRoot(snapshot, descriptor.PluginId, path);
        EnsureNoReparseEscape(root, path);
        if (!File.Exists(path)) throw new FileNotFoundException("Skill file no longer exists.", path);
        var info = new FileInfo(path);
        if (info.Length > MaxSkillBytes)
            throw new InvalidDataException($"Skill size {info.Length} bytes exceeds the {MaxSkillBytes}-byte limit.");
        var parsed = Parse(ReadBoundedUtf8(path, info.Length));
        return new(descriptor with { Length = info.Length }, parsed.Instructions);
    }

    public IReadOnlyList<PluginSkillDocument> LoadSelected(
        PluginCatalogSnapshot snapshot,
        IReadOnlyList<PluginSkillDescriptor> descriptors,
        IReadOnlyCollection<string> selectedIds)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(selectedIds);
        if (selectedIds.Count == 0) return [];
        var selected = new HashSet<string>(selectedIds.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
        var documents = new List<PluginSkillDocument>();
        foreach (var descriptor in descriptors.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (!selected.Contains(descriptor.Id) && !selected.Contains(descriptor.Name) &&
                !selected.Contains(descriptor.PluginId + ":" + descriptor.Name)) continue;
            documents.Add(Load(snapshot, descriptor));
        }
        return documents;
    }

    private static IEnumerable<string> EnumerateSafeSkillFiles(
        string pluginId,
        string root,
        List<PluginSkillDiagnostic> diagnostics)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(directory, "SKILL.md", SearchOption.TopDirectoryOnly).ToArray();
                directories = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new(pluginId, directory, "Skill directory could not be read: " + Clip(ex.Message, 500)));
                continue;
            }

            foreach (var file in files.OrderBy(path => path, PathComparer))
            {
                if (IsReparsePoint(file))
                {
                    diagnostics.Add(new(pluginId, file, "Skill file is a reparse point and was rejected."));
                    continue;
                }
                yield return Path.GetFullPath(file);
            }

            foreach (var child in directories.OrderByDescending(path => path, PathComparer))
            {
                if (IsReparsePoint(child))
                {
                    diagnostics.Add(new(pluginId, child, "Skill directory is a reparse point and was not traversed."));
                    continue;
                }
                stack.Push(child);
            }
        }
    }

    private static string ResolveDeclaredRoot(PluginCatalogSnapshot snapshot, string pluginId, string path)
    {
        if (!snapshot.Manifests.Any(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal)))
            throw new UnauthorizedAccessException("Skill plugin is not enabled in the current catalog.");
        if (!snapshot.SkillRoots.TryGetValue(pluginId, out var declaredRoots))
            throw new UnauthorizedAccessException("Skill plugin has no declared skill roots in the current catalog.");
        foreach (var declaredRoot in declaredRoots)
        {
            var root = Path.GetFullPath(declaredRoot);
            if (IsContained(root, path)) return root;
        }
        throw new UnauthorizedAccessException("Skill path is outside the plugin's declared skill roots.");
    }

    private static void EnsureNoReparseEscape(string root, string path)
    {
        if (!IsContained(root, path)) throw new UnauthorizedAccessException("Skill path escapes its declared root.");
        if (IsReparsePoint(root)) throw new UnauthorizedAccessException("Skill root is a reparse point.");
        var relative = Path.GetRelativePath(root, path);
        var cursor = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment);
            if (File.Exists(cursor) || Directory.Exists(cursor))
            {
                if (IsReparsePoint(cursor)) throw new UnauthorizedAccessException("Skill path traverses a reparse point.");
            }
        }
    }

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string ReadMetadataUtf8(string path, long length)
    {
        if (length > MaxSkillBytes) throw new InvalidDataException("Skill size exceeds the configured limit.");
        const int metadataLimit = 16 * 1024;
        var byteCount = (int)Math.Min(length, metadataLimit);
        var bytes = new byte[byteCount];
        using (var stream = File.OpenRead(path))
        {
            var offset = 0;
            while (offset < byteCount)
            {
                var read = stream.Read(bytes, offset, byteCount - offset);
                if (read == 0) break;
                offset += read;
            }
            if (offset != byteCount) Array.Resize(ref bytes, offset);
        }

        var sliceLength = bytes.Length;
        if (StartsWithAscii(bytes, "---\n"))
        {
            var delimiter = Encoding.UTF8.GetBytes("\n---\n");
            var index = IndexOf(bytes, delimiter, 4);
            if (index < 0 && length > bytes.Length)
                throw new InvalidDataException($"Skill front matter exceeds the {metadataLimit}-byte metadata limit.");
            if (index >= 0) sliceLength = index + delimiter.Length;
        }
        else if (StartsWithAscii(bytes, "---\r\n"))
        {
            var delimiter = Encoding.UTF8.GetBytes("\r\n---\r\n");
            var index = IndexOf(bytes, delimiter, 5);
            if (index < 0 && length > bytes.Length)
                throw new InvalidDataException($"Skill front matter exceeds the {metadataLimit}-byte metadata limit.");
            if (index >= 0) sliceLength = index + delimiter.Length;
        }

        return new UTF8Encoding(false, true).GetString(bytes, 0, sliceLength);
    }

    private static bool StartsWithAscii(byte[] bytes, string text)
    {
        var prefix = Encoding.ASCII.GetBytes(text);
        return bytes.AsSpan().StartsWith(prefix);
    }

    private static int IndexOf(byte[] bytes, byte[] needle, int start)
    {
        for (var index = Math.Max(start, 0); index <= bytes.Length - needle.Length; index++)
        {
            if (bytes.AsSpan(index, needle.Length).SequenceEqual(needle)) return index;
        }
        return -1;
    }

    private static string ReadBoundedUtf8(string path, long length)
    {
        if (length > MaxSkillBytes) throw new InvalidDataException("Skill size exceeds the configured limit.");
        return File.ReadAllText(path, new UTF8Encoding(false, true));
    }

    private static ParsedSkill Parse(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return new(null, null, normalized.Trim());
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("Skill front matter is not terminated.");
        var frontMatter = normalized[4..end];
        string? name = null;
        string? description = null;
        foreach (var rawLine in frontMatter.Split('\n'))
        {
            var line = rawLine.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
        }
        return new(name, description, normalized[(end + 5)..].Trim());
    }

    private static string InferDescription(string instructions)
    {
        foreach (var rawLine in instructions.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('#').Trim();
            if (line.Length > 0) return Clip(line, MaxDescriptionChars);
        }
        return "Plugin skill instructions";
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record ParsedSkill(string? Name, string? Description, string Instructions);
}
