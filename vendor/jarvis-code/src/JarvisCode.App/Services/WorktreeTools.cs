using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The model-invoked side of worktree isolation (the reference EnterWorktree /
/// ExitWorktree): enter creates a git worktree next to the repo and repoints
/// the session there; exit repoints the session back and optionally removes
/// the worktree. The context bar's toggle chip stays the user-driven way in.
/// </summary>
public static class WorktreeTools
{
    public const string WorktreesFolder = ".jarvis-worktrees";

    /// <param name="currentDirectory">Reads the session's live working directory.</param>
    /// <param name="setDirectory">Repoints the session (marshals to the UI thread itself).</param>
    /// <param name="fireHook">Optional worktree_create / worktree_remove hook sink.</param>
    /// <param name="settings">
    /// Settings › Jarvis Code › Local sessions: "Worktree location" decides where a
    /// worktree is created (the reference's <c>chillingSlothLocation</c> — beside the
    /// project, or a folder the user picked) and "Branch prefix" names the branch
    /// (its <c>ccBranchPrefix</c>). Null keeps the built-in defaults.
    /// </param>
    public static IReadOnlyList<ITool> Create(
        Func<string> currentDirectory,
        Action<string> setDirectory,
        Action<JarvisCode.Core.Hooks.HookEvent, JsonObject>? fireHook = null,
        UiSettingsStore? settings = null) =>
        [
            new EnterWorktreeTool(currentDirectory, setDirectory, fireHook, settings),
            new ExitWorktreeTool(currentDirectory, setDirectory, fireHook),
        ];

    /// <summary>Where a repository's worktrees go: beside it, or under the folder the user picked.</summary>
    public static string WorktreeRoot(string repoRoot, UiSettingsStore? settings)
    {
        var location = settings?.Current.WorktreeLocation;
        return location is { Length: > 0 } && location != "default" && Directory.Exists(location)
            ? location
            : Path.Combine(Path.GetDirectoryName(repoRoot) ?? repoRoot, WorktreesFolder);
    }

    /// <summary>The prefix a created branch takes; empty settings keep this build's own name.</summary>
    public static string BranchPrefix(UiSettingsStore? settings)
    {
        var prefix = settings?.Current.BranchPrefix;
        return prefix is { Length: > 0 } ? prefix : UiSettings.DefaultBranchPrefix;
    }

    /// <summary>The branch checked out in a worktree, or "" when detached.</summary>
    private static string BranchOf(string worktree)
    {
        var (exit, output) = RunGit(worktree, "rev-parse --abbrev-ref HEAD");
        var branch = exit == 0 ? output.Trim() : "";
        return branch == "HEAD" ? "" : branch;
    }

    internal static bool IsWorktreePath(string path) =>
        path.Contains(WorktreesFolder, StringComparison.OrdinalIgnoreCase);

    internal static (int ExitCode, string Output) RunGit(string workingDirectory, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }

    private sealed class EnterWorktreeTool(
        Func<string> currentDirectory,
        Action<string> setDirectory,
        Action<JarvisCode.Core.Hooks.HookEvent, JsonObject>? fireHook,
        UiSettingsStore? settings = null) : ITool
    {
        public string Name => "EnterWorktree";

        public string Description =>
            "Moves this session into an isolated git worktree (its own checkout on its own branch), so your " +
            "changes cannot collide with work in the main checkout. Use it before risky or parallel work when " +
            "the user asks for isolation. The session's working directory repoints to the worktree: for the " +
            "rest of this turn use absolute paths under the returned directory; from the next turn it is the cwd. " +
            "Leave and clean up with ExitWorktree.";

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] =
                        "Optional name for a new worktree. Each \"/\"-separated segment may contain only letters, " +
                        "digits, dots, underscores, and dashes; max 64 chars total. A random name is generated if " +
                        "not provided. Mutually exclusive with `path`.",
                },
                ["path"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] =
                        "Path to an existing worktree to switch into instead of creating a new one. Must appear in " +
                        "`git worktree list` for the current repo. Mutually exclusive with `name`.",
                },
            },
        };

        public bool IsReadOnly => false;

        public string DescribeCall(JsonObject arguments) =>
            $"EnterWorktree({JsonArgs.GetString(arguments, "path") ?? JsonArgs.GetString(arguments, "name") ?? "auto"})";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var cwd = currentDirectory();
            if (IsWorktreePath(cwd))
                return Task.FromResult(ToolResult.Error(
                    $"The session is already in a worktree ({cwd}). ExitWorktree leaves it first."));

            var (rootExit, rootOut) = RunGit(cwd, "rev-parse --show-toplevel");
            if (rootExit != 0)
                return Task.FromResult(ToolResult.Error($"Not a git repository: {rootOut.Trim()}"));

            var repoRoot = Path.GetFullPath(rootOut.Trim().Replace('/', '\\'));
            var repoName = Path.GetFileName(repoRoot);
            var rawName = JsonArgs.GetString(arguments, "name");
            var rawPath = JsonArgs.GetString(arguments, "path");
            if (!string.IsNullOrWhiteSpace(rawName) && !string.IsNullOrWhiteSpace(rawPath))
                return Task.FromResult(ToolResult.Error("`name` and `path` are mutually exclusive: pass one or the other."));

            if (!string.IsNullOrWhiteSpace(rawPath))
            {
                // The reference's `path`: switch into a worktree git already knows about.
                var target = Path.GetFullPath(rawPath.Trim(), cwd);
                var (listExit, listOut) = RunGit(repoRoot, "worktree list --porcelain");
                var known = listExit == 0
                    ? listOut.Split('\n')
                        .Where(static l => l.StartsWith("worktree ", StringComparison.Ordinal))
                        .Select(l => Path.GetFullPath(l["worktree ".Length..].Trim().Replace('/', '\\')))
                        .ToList()
                    : [];
                if (!known.Any(k => string.Equals(k.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    return Task.FromResult(ToolResult.Error(
                        $"{target} is not a worktree of this repository (`git worktree list` does not show it)."));

                setDirectory(target);
                fireHook?.Invoke(JarvisCode.Core.Hooks.HookEvent.WorktreeCreate,
                    new JsonObject { ["path"] = target, ["reentered"] = true });
                return Task.FromResult(ToolResult.Success(
                    $"Switched into the worktree at {target}. Use absolute paths under it for the rest of this " +
                    "turn; from the next turn it is the working directory."));
            }

            if (!string.IsNullOrWhiteSpace(rawName) && !IsValidWorktreeName(rawName.Trim()))
                return Task.FromResult(ToolResult.Error(
                    $"Invalid worktree name \"{rawName}\": each \"/\"-separated segment may contain only letters, " +
                    "digits, dots, underscores, and dashes, and the whole name is at most 64 characters."));

            var suffix = string.IsNullOrWhiteSpace(rawName)
                ? (context.SessionId is { Length: > 8 } id ? id[^8..] : context.SessionId ?? "wt")
                : rawName.Trim().Replace('/', '-');
            var worktreePath = Path.Combine(WorktreeRoot(repoRoot, settings), $"{repoName}-{suffix}");

            if (Directory.Exists(worktreePath))
            {
                setDirectory(worktreePath);
                fireHook?.Invoke(JarvisCode.Core.Hooks.HookEvent.WorktreeCreate,
                    new JsonObject { ["path"] = worktreePath, ["reentered"] = true });
                return Task.FromResult(ToolResult.Success(
                    $"Re-entered the existing worktree at {worktreePath}. Use absolute paths under it for the " +
                    "rest of this turn; from the next turn it is the working directory."));
            }

            var branch = $"{BranchPrefix(settings)}/{suffix}";
            var (exit, output) = RunGit(repoRoot, $"worktree add \"{worktreePath}\" -b {branch}");
            if (exit != 0)
                return Task.FromResult(ToolResult.Error($"git worktree add failed: {output.Trim()}"));

            setDirectory(worktreePath);
            fireHook?.Invoke(JarvisCode.Core.Hooks.HookEvent.WorktreeCreate,
                new JsonObject { ["path"] = worktreePath, ["branch"] = branch, ["reentered"] = false });
            return Task.FromResult(ToolResult.Success(
                $"Created worktree {worktreePath} on branch {branch} and repointed the session there. " +
                "Use absolute paths under that directory for the rest of this turn; from the next turn it is " +
                "the working directory. Leave with ExitWorktree when the isolated work is done."));
        }
    }

    /// <summary>
    /// The reference's rule for a worktree name: "/"-separated segments of
    /// letters, digits, dots, underscores and dashes, at most 64 characters in all.
    /// </summary>
    internal static bool IsValidWorktreeName(string name) =>
        name.Length is > 0 and <= 64 &&
        name.Split('/').All(static segment =>
            segment.Length > 0 && segment.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));

    private sealed class ExitWorktreeTool(
        Func<string> currentDirectory,
        Action<string> setDirectory,
        Action<JarvisCode.Core.Hooks.HookEvent, JsonObject>? fireHook) : ITool
    {
        public string Name => "ExitWorktree";

        public string Description =>
            "Leaves the session's git worktree and repoints the session back at the main checkout. Commit or " +
            "push anything you want to keep first — pass discard:true to also delete the worktree (uncommitted " +
            "changes in it are lost); without it the worktree stays on disk and EnterWorktree can return to it.";

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("keep", "remove"),
                    ["description"] = "\"keep\" leaves the worktree and branch on disk; \"remove\" deletes both.",
                },
                ["discard_changes"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] =
                        "Required true when action is \"remove\" and the worktree has uncommitted files or unmerged " +
                        "commits. The tool will refuse and list them otherwise.",
                },
            },
            ["required"] = new JsonArray("action"),
        };

        public bool IsReadOnly => false;

        public string DescribeCall(JsonObject arguments) => $"ExitWorktree({ActionOf(arguments)})";

        /// <summary>The reference's `action`; this tool's older `discard:true` still reads as "remove".</summary>
        private static string ActionOf(JsonObject arguments) =>
            JsonArgs.GetString(arguments, "action")?.Trim().ToLowerInvariant()
            ?? (JsonArgs.GetBool(arguments, "discard") ? "remove" : "keep");

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var cwd = currentDirectory();
            if (!IsWorktreePath(cwd))
                return Task.FromResult(ToolResult.Error("The session is not in a worktree."));

            var action = ActionOf(arguments);
            if (action is not ("keep" or "remove"))
                return Task.FromResult(ToolResult.Error($"Unknown action \"{action}\": use \"keep\" or \"remove\"."));

            var (exit, commonDir) = RunGit(cwd, "rev-parse --git-common-dir");
            if (exit != 0)
                return Task.FromResult(ToolResult.Error($"Could not resolve the main checkout: {commonDir.Trim()}"));
            var origin = Path.GetDirectoryName(
                Path.GetFullPath(commonDir.Trim().Replace('/', '\\'), cwd));
            if (origin is null)
                return Task.FromResult(ToolResult.Error("Could not resolve the main checkout."));

            if (action == "remove" && !(JsonArgs.GetBool(arguments, "discard_changes") || JsonArgs.GetBool(arguments, "discard")))
            {
                // The reference refuses to remove work it would lose, and lists it.
                var (statusExit, status) = RunGit(cwd, "status --porcelain");
                var dirty = statusExit == 0 ? status.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
                var (logExit, log) = RunGit(origin, $"log --oneline HEAD..\"{BranchOf(cwd)}\"");
                var unmerged = logExit == 0 ? log.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
                if (dirty.Length > 0 || unmerged.Length > 0)
                {
                    var report = new System.Text.StringBuilder();
                    report.Append("Refusing to remove the worktree: it has work that would be lost. Pass ");
                    report.Append("discard_changes: true to remove it anyway, or commit and merge first.");
                    if (dirty.Length > 0)
                        report.Append("\n\nUncommitted files:\n").Append(string.Join('\n', dirty));
                    if (unmerged.Length > 0)
                        report.Append("\n\nUnmerged commits:\n").Append(string.Join('\n', unmerged));
                    return Task.FromResult(ToolResult.Error(report.ToString()));
                }
            }

            setDirectory(origin);
            if (action == "remove")
            {
                var branch = BranchOf(cwd);
                var (removeExit, removeOut) = RunGit(origin, $"worktree remove \"{cwd}\" --force");
                if (removeExit == 0)
                {
                    // "remove" deletes the branch with the worktree, as the reference says.
                    if (!string.IsNullOrEmpty(branch))
                        RunGit(origin, $"branch -D \"{branch}\"");
                    fireHook?.Invoke(JarvisCode.Core.Hooks.HookEvent.WorktreeRemove,
                        new JsonObject { ["path"] = cwd });
                }

                return Task.FromResult(ToolResult.Success(removeExit == 0
                    ? $"Left the worktree and removed it and its branch. The session is back at {origin}."
                    : $"Left the worktree (the session is back at {origin}), but removing it failed: {removeOut.Trim()}"));
            }

            return Task.FromResult(ToolResult.Success(
                $"Left the worktree; the session is back at {origin}. The worktree at {cwd} and its branch are kept — " +
                "EnterWorktree with its path returns to it, or ExitWorktree with action \"remove\" deletes it."));
        }
    }
}
