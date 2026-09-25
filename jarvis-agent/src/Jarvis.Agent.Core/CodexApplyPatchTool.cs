using System.Text;
using System.Text.Json;
using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core;

/// <summary>Codex-compatible V4A patch application with optimistic session reads and rollback.</summary>
public sealed class CodexApplyPatchTool(SessionFileObservations observations, string privateRoot) : IAgentTool
{
    private const int MaxPatchChars = 1_048_576;

    public ToolDescriptor Descriptor { get; } = new(
        "source.apply_patch",
        "apply_patch",
        "source",
        "Apply a V4A patch to source files. The patch must start with '*** Begin Patch' and end with '*** End Patch'; supported operations are Add File, Update File, Move to, and Delete File. Read existing files in this chat before changing them.",
        WireJson.Element(new
        {
            type = "object",
            properties = new
            {
                patch = new
                {
                    type = "string",
                    minLength = 1,
                    maxLength = MaxPatchChars,
                    description = "V4A patch text, including *** Begin Patch and *** End Patch markers."
                }
            },
            required = new[] { "patch" },
            additionalProperties = false
        }),
        ReadOnly: false,
        Sensitive: false);

    public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken ct)
    {
        try
        {
            var patch = arguments.GetProperty("patch").GetString() ?? "";
            if (patch.Length is < 1 or > MaxPatchChars)
                throw new ArgumentException($"patch must contain 1..{MaxPatchChars} characters.");

            var operations = Parse(patch, context.Workspace);
            if (operations.Count == 0) throw new ArgumentException("Patch contains no file operations.");
            foreach (var operation in operations)
            {
                RejectPrivate(operation.SourcePath);
                if (operation.TargetPath is not null) RejectPrivate(operation.TargetPath);
            }

            var scope = context.IsolationScopeId;
            foreach (var path in operations.SelectMany(o => o.TargetPath is null ? [o.SourcePath] : new[] { o.SourcePath, o.TargetPath }).Distinct(PathComparer))
                if (File.Exists(path)) await observations.ValidateWriteAsync(scope, path, ct);

            var staged = new List<StagedChange>();
            foreach (var operation in operations)
            {
                ct.ThrowIfCancellationRequested();
                staged.Add(Stage(operation));
            }

            ApplyAtomically(staged, ct);
            foreach (var path in staged.SelectMany(s => s.AllPaths).Distinct(PathComparer))
                await observations.RememberAsync(scope, path, ct);

            var summary = string.Join('\n', staged.Select(s => s.Summary));
            return new ToolReply("Done!\n" + summary);
        }
        catch (AgentRequestException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ToolReply.Error(ex.Message);
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private void RejectPrivate(string path)
    {
        var root = ToolExecutionResources.CanonicalPath(privateRoot);
        var target = ToolExecutionResources.CanonicalPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (target.Equals(root, comparison) || target.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new UnauthorizedAccessException("The agent's private profile cannot be changed by apply_patch.");
    }

    private static IReadOnlyList<PatchOperation> Parse(string patch, string workspace)
    {
        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length < 2 || lines[0] != "*** Begin Patch")
            throw new ArgumentException("Patch must begin with '*** Begin Patch'.");
        var end = Array.FindLastIndex(lines, line => line == "*** End Patch");
        if (end < 1 || lines[(end + 1)..].Any(line => line.Length > 0))
            throw new ArgumentException("Patch must end with '*** End Patch'.");

        var operations = new List<PatchOperation>();
        var i = 1;
        while (i < end)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
            if (TryHeader(lines[i], "*** Add File: ", out var addPath))
            {
                i++;
                var content = new List<string>();
                while (i < end && !lines[i].StartsWith("*** ", StringComparison.Ordinal))
                {
                    if (!lines[i].StartsWith('+')) throw new ArgumentException($"Add File lines must start with '+': {lines[i]}");
                    content.Add(lines[i][1..]); i++;
                }
                operations.Add(new(PatchKind.Add, ResolvePatchPath(addPath, workspace), null, content, []));
                continue;
            }
            if (TryHeader(lines[i], "*** Delete File: ", out var deletePath))
            {
                operations.Add(new(PatchKind.Delete, ResolvePatchPath(deletePath, workspace), null, [], []));
                i++; continue;
            }
            if (TryHeader(lines[i], "*** Update File: ", out var updatePath))
            {
                i++;
                string? moveTo = null;
                if (i < end && TryHeader(lines[i], "*** Move to: ", out var target))
                {
                    moveTo = ResolvePatchPath(target, workspace); i++;
                }
                var hunks = new List<PatchHunk>();
                while (i < end && !lines[i].StartsWith("*** ", StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(lines[i])) { i++; continue; }
                    if (!lines[i].StartsWith("@@", StringComparison.Ordinal))
                        throw new ArgumentException($"Expected an @@ hunk in Update File, found: {lines[i]}");
                    var anchor = lines[i][2..].Trim(); i++;
                    var body = new List<string>();
                    while (i < end && !lines[i].StartsWith("@@", StringComparison.Ordinal) && !lines[i].StartsWith("*** ", StringComparison.Ordinal))
                    {
                        if (lines[i] == "\\ No newline at end of file") { i++; continue; }
                        if (lines[i].Length == 0 || lines[i][0] is not (' ' or '+' or '-'))
                            throw new ArgumentException($"Patch hunk lines must start with space, '+' or '-': {lines[i]}");
                        body.Add(lines[i]); i++;
                    }
                    if (body.Count == 0) throw new ArgumentException("Update hunk cannot be empty.");
                    hunks.Add(new(anchor, body));
                }
                if (hunks.Count == 0) throw new ArgumentException("Update File requires at least one @@ hunk.");
                operations.Add(new(moveTo is null ? PatchKind.Update : PatchKind.Move,
                    ResolvePatchPath(updatePath, workspace), moveTo, [], hunks));
                continue;
            }
            throw new ArgumentException($"Unknown patch directive: {lines[i]}");
        }
        return operations;
    }

    private static bool TryHeader(string line, string prefix, out string value)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) { value = ""; return false; }
        value = line[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Missing path after {prefix.Trim()}.");
        return true;
    }

    private static string ResolvePatchPath(string raw, string workspace)
    {
        if (Path.IsPathRooted(raw)) throw new ArgumentException("apply_patch paths must be relative to the selected workspace.");
        var root = Path.GetFullPath(WorkspaceDirectories.ResolvePath(null, workspace));
        var full = Path.GetFullPath(raw.Replace('/', Path.DirectorySeparatorChar), root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.Equals(root, comparison) && !full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new UnauthorizedAccessException("apply_patch path escapes the selected workspace: " + raw);
        return full;
    }

    private static StagedChange Stage(PatchOperation operation)
    {
        return operation.Kind switch
        {
            PatchKind.Add => StageAdd(operation),
            PatchKind.Delete => StageDelete(operation),
            PatchKind.Update or PatchKind.Move => StageUpdate(operation),
            _ => throw new InvalidOperationException("Unsupported patch operation.")
        };
    }

    private static StagedChange StageAdd(PatchOperation operation)
    {
        if (File.Exists(operation.SourcePath)) throw new IOException("Add File target already exists: " + operation.SourcePath);
        var text = string.Join('\n', operation.AddedLines) + (operation.AddedLines.Count > 0 ? "\n" : "");
        return new(operation.SourcePath, null, Encode(text, bom: false), PatchKind.Add,
            $"A {Relative(operation.SourcePath)}");
    }

    private static StagedChange StageDelete(PatchOperation operation)
    {
        if (!File.Exists(operation.SourcePath)) throw new FileNotFoundException("Delete File target does not exist.", operation.SourcePath);
        return new(operation.SourcePath, null, null, PatchKind.Delete,
            $"D {Relative(operation.SourcePath)}");
    }

    private static StagedChange StageUpdate(PatchOperation operation)
    {
        if (!File.Exists(operation.SourcePath)) throw new FileNotFoundException("Update File target does not exist.", operation.SourcePath);
        if (operation.TargetPath is not null && !PathComparer.Equals(operation.SourcePath, operation.TargetPath) && File.Exists(operation.TargetPath))
            throw new IOException("Move target already exists: " + operation.TargetPath);

        var bytes = File.ReadAllBytes(operation.SourcePath);
        var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var content = DecodeUtf8(bytes, bom);
        var updated = ApplyHunks(content, operation.Hunks);
        var destination = operation.TargetPath ?? operation.SourcePath;
        var summary = operation.Kind == PatchKind.Move
            ? $"R {Relative(operation.SourcePath)} -> {Relative(destination)}"
            : $"M {Relative(operation.SourcePath)}";
        return new(operation.SourcePath, destination, Encode(updated, bom), operation.Kind, summary);
    }

    private static string ApplyHunks(string content, IReadOnlyList<PatchHunk> hunks)
    {
        var eol = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var finalNewline = content.EndsWith("\n", StringComparison.Ordinal);
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (finalNewline && lines.Count > 0) lines.RemoveAt(lines.Count - 1);
        var cursor = 0;

        foreach (var hunk in hunks)
        {
            var oldLines = hunk.Body.Where(line => line[0] is ' ' or '-').Select(line => line[1..]).ToArray();
            var newLines = hunk.Body.Where(line => line[0] is ' ' or '+').Select(line => line[1..]).ToArray();
            var searchStart = cursor;
            if (!string.IsNullOrWhiteSpace(hunk.Anchor))
            {
                var anchor = FindAnchor(lines, hunk.Anchor, cursor);
                if (anchor < 0) throw new InvalidDataException("Patch anchor was not found: " + hunk.Anchor);
                searchStart = anchor;
            }
            var index = FindSequence(lines, oldLines, searchStart);
            if (index < 0 && searchStart > 0) index = FindSequence(lines, oldLines, 0);
            if (index < 0) throw new InvalidDataException("Patch hunk context did not match the current file.");
            lines.RemoveRange(index, oldLines.Length);
            lines.InsertRange(index, newLines);
            cursor = index + newLines.Length;
        }

        var result = string.Join(eol, lines);
        return finalNewline ? result + eol : result;
    }

    private static int FindAnchor(IReadOnlyList<string> lines, string anchor, int start)
    {
        for (var i = Math.Max(0, start); i < lines.Count; i++)
            if (lines[i].Contains(anchor, StringComparison.Ordinal)) return i;
        for (var i = 0; i < Math.Min(start, lines.Count); i++)
            if (lines[i].Contains(anchor, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static int FindSequence(IReadOnlyList<string> lines, IReadOnlyList<string> sequence, int start)
    {
        if (sequence.Count == 0) return Math.Clamp(start, 0, lines.Count);
        for (var i = Math.Clamp(start, 0, lines.Count); i + sequence.Count <= lines.Count; i++)
            if (Matches(lines, sequence, i, static (a, b) => a == b)) return i;
        for (var i = Math.Clamp(start, 0, lines.Count); i + sequence.Count <= lines.Count; i++)
            if (Matches(lines, sequence, i, static (a, b) => a.TrimEnd() == b.TrimEnd())) return i;
        return -1;
    }

    private static bool Matches(IReadOnlyList<string> lines, IReadOnlyList<string> sequence, int at,
        Func<string, string, bool> compare)
    {
        for (var i = 0; i < sequence.Count; i++) if (!compare(lines[at + i], sequence[i])) return false;
        return true;
    }

    private static void ApplyAtomically(IReadOnlyList<StagedChange> changes, CancellationToken ct)
    {
        var touched = changes.SelectMany(change => change.AllPaths).Distinct(PathComparer).ToArray();
        var backups = touched.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null, PathComparer);
        var temps = new List<string>();
        try
        {
            foreach (var change in changes)
            {
                ct.ThrowIfCancellationRequested();
                if (change.Bytes is not null)
                {
                    var destination = change.DestinationPath ?? change.SourcePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var temp = destination + ".jarvis-" + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(temp, change.Bytes); temps.Add(temp);
                    File.Move(temp, destination, overwrite: true); temps.Remove(temp);
                }
                if (change.Kind is PatchKind.Delete or PatchKind.Move &&
                    (change.DestinationPath is null || !PathComparer.Equals(change.SourcePath, change.DestinationPath)) && File.Exists(change.SourcePath))
                    File.Delete(change.SourcePath);
            }
        }
        catch
        {
            foreach (var pair in backups)
            {
                try
                {
                    if (pair.Value is null) { if (File.Exists(pair.Key)) File.Delete(pair.Key); }
                    else { Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!); File.WriteAllBytes(pair.Key, pair.Value); }
                }
                catch (Exception) { }
            }
            throw;
        }
        finally
        {
            foreach (var temp in temps) try { File.Delete(temp); } catch (Exception) { }
        }
    }

    private static byte[] Encode(string text, bool bom) => bom
        ? [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(text)]
        : Encoding.UTF8.GetBytes(text);

    private static string DecodeUtf8(byte[] bytes, bool bom)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes, bom ? Encoding.UTF8.Preamble.Length : 0, bytes.Length - (bom ? Encoding.UTF8.Preamble.Length : 0)); }
        catch (DecoderFallbackException) { throw new InvalidDataException("apply_patch currently supports UTF-8 text files only."); }
    }

    private static string Relative(string path) => Path.GetRelativePath(Environment.CurrentDirectory, path).Replace('\\', '/');

    private enum PatchKind { Add, Update, Move, Delete }
    private sealed record PatchHunk(string Anchor, IReadOnlyList<string> Body);
    private sealed record PatchOperation(PatchKind Kind, string SourcePath, string? TargetPath,
        IReadOnlyList<string> AddedLines, IReadOnlyList<PatchHunk> Hunks);
    private sealed record StagedChange(string SourcePath, string? DestinationPath, byte[]? Bytes, PatchKind Kind, string Summary)
    {
        public IEnumerable<string> AllPaths => DestinationPath is null || PathComparer.Equals(SourcePath, DestinationPath)
            ? [SourcePath] : [SourcePath, DestinationPath];
    }
}
