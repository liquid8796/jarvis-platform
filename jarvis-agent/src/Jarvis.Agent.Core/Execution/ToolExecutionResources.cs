using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Execution;

public sealed record ToolResourceClaims(IReadOnlyList<string> Resources, bool Exclusive);

public static class ToolExecutionResources
{
    public static ToolResourceClaims For(ToolDescriptor tool, JsonElement arguments, AgentExecutionContext context)
    {
        var id = tool.Id.ToLowerInvariant();
        if (AgentSessionRules.IsControlTool(tool.Id)) return new([], false);
        if (id is "process.write_stdin" or "process.resize_pty")
            return new(["job|" + Text(arguments, "jobId")], true);
        if (id is "process.start" or "process.spawn" || tool.Category == "shell")
            return new(["*"], true); // Arbitrary commands are not assumed to remain inside cwd.
        if (tool.Category is "session" or "workspace" or "thread" or "workflow")
            return new(["coordination|" + tool.Category + "|" + context.IsolationScopeId], !tool.ReadOnly);
        if (tool.Category == "computer") return new(["desktop"], true);
        if (tool.Category == "browser")
        {
            var resources = new List<string> { "browser|" + context.IsolationScopeId };
            if (MayChangeDesktop(tool.Id)) resources.Add("desktop");
            return new(resources, true);
        }
        if (tool.Category == "visualize") return new(["desktop"], true);
        if (tool.Category is "filesystem" or "git")
        {
            var explicitDirectory = Text(arguments, "workingDirectory");
            var cwd = string.IsNullOrWhiteSpace(explicitDirectory) ? context.Workspace
                : WorkspaceDirectories.ResolvePath(explicitDirectory, context.Workspace);
            if (tool.Category == "git") return new(GitClaims(WorkspaceDirectories.ResolvePath(null, cwd)), true);
            var raw = Text(arguments, "file_path") ?? Text(arguments, "notebook_path") ?? Text(arguments, "path") ??
                Text(arguments, "filePath") ?? Text(arguments, "directory");
            var path = WorkspaceDirectories.ResolvePath(raw, cwd);
            return new(["fs|" + CanonicalPath(path)], !tool.ReadOnly || tool.Sensitive);
        }
        return new(["*"], !tool.ReadOnly || tool.Sensitive);
    }

    public static bool MayChangeDesktop(string toolId) => toolId is
        "browser.navigate" or "browser.resize_window" or "browser.tabs_create_mcp" or "browser.tabs_context_mcp" or
        "browser.tabs_close_mcp" or "browser.computer" or "browser.javascript_tool" or "browser.browser_batch" or
        "browser.form_input" or "browser.file_upload" or "browser.upload_image";

    public static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("Cannot resolve a resource link safely.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static IReadOnlyList<string> GitClaims(string directory)
    {
        var folder = new DirectoryInfo(directory);
        while (folder is not null)
        {
            var metadata = Path.Combine(folder.FullName, ".git");
            if (Directory.Exists(metadata)) return ["fs|" + CanonicalPath(folder.FullName), "fs|" + CanonicalPath(metadata)];
            if (File.Exists(metadata))
            {
                if (new FileInfo(metadata).Length > 4096) throw new IOException("Git worktree metadata is oversized.");
                var line = File.ReadAllText(metadata).Trim();
                if (!line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid Git worktree metadata.");
                var gitDirectory = Path.GetFullPath(line[7..].Trim(), folder.FullName);
                var commonPath = Path.Combine(gitDirectory, "commondir");
                var claims = new List<string> { "fs|" + CanonicalPath(folder.FullName), "fs|" + CanonicalPath(gitDirectory) };
                if (File.Exists(commonPath))
                {
                    if (new FileInfo(commonPath).Length > 4096) throw new IOException("Git common-directory metadata is oversized.");
                    claims.Add("fs|" + CanonicalPath(Path.GetFullPath(File.ReadAllText(commonPath).Trim(), gitDirectory)));
                }
                return claims;
            }
            folder = folder.Parent;
        }
        return ["fs|" + CanonicalPath(directory)];
    }
    private static string? Text(JsonElement arguments, string name) => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() : null;
}
