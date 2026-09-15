using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// Shared runner for the structured git tools (ported from claw-code's Git* tool
/// family): read-only git queries as first-class tools, so the model gets
/// parseable output without shell-permission escalations.
/// </summary>
internal static class GitRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static async Task<ToolResult> RunAsync(
        IReadOnlyList<string> arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = context.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Never let a query invoke a pager or block on interactive credential prompts.
        startInfo.ArgumentList.Add("--no-pager");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return ToolResult.Error("Failed to start git.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ToolResult.Error($"git could not be started: {ex.Message}. Is git installed and on PATH?");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(Timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            if (cancellationToken.IsCancellationRequested)
                throw;
            return ToolResult.Error($"git {arguments[0]} timed out after {Timeout.TotalSeconds:0}s.");
        }

        var stdout = (await stdoutTask).TrimEnd();
        var stderr = (await stderrTask).TrimEnd();
        if (process.ExitCode != 0)
        {
            var error = stderr.Length > 0 ? stderr : stdout;
            return ToolResult.Error($"git {arguments[0]} failed (exit {process.ExitCode}): " +
                (error.Length > 0 ? error : "no output"));
        }
        var output = stdout.Length > 0 ? stdout : "(no output)";
        return ToolResult.Success(context.Truncate(output, $"git {arguments[0]} output"));
    }
}

public sealed class GitStatusTool : ITool
{
    public string Name => "git_status";

    public string Description =>
        "Shows the git working-tree status (branch, staged/unstaged/untracked files) in short format. " +
        "Use this instead of running git through the shell.";

    public JsonObject InputSchema => SchemaBuilder.Object([]);

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) => "GitStatus()";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
        GitRunner.RunAsync(["status", "--short", "--branch"], context, cancellationToken);
}

public sealed class GitDiffTool : ITool
{
    public string Name => "git_diff";

    public string Description =>
        "Shows a git diff. By default the unstaged working-tree changes; pass 'target' for a revision or range " +
        "(e.g. HEAD~1, main...feature) and/or 'path' to narrow to one file or directory. " +
        "Use this instead of running git through the shell.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("target", SchemaBuilder.String("Revision or range to diff (default: working tree vs index)")),
            ("path", SchemaBuilder.String("Limit the diff to this file or directory")),
            ("staged", SchemaBuilder.Boolean("Diff the staged changes instead of the working tree")),
            ("stat", SchemaBuilder.Boolean("Only show the per-file change summary")),
        ]);

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"GitDiff({JsonArgs.GetString(arguments, "target") ?? (JsonArgs.GetBool(arguments, "staged") ? "--staged" : "working tree")})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var args = new List<string> { "diff" };
        if (JsonArgs.GetBool(arguments, "staged"))
            args.Add("--staged");
        if (JsonArgs.GetBool(arguments, "stat"))
            args.Add("--stat");
        if (JsonArgs.GetString(arguments, "target") is { Length: > 0 } target)
            args.Add(target);
        if (JsonArgs.GetString(arguments, "path") is { Length: > 0 } path)
        {
            args.Add("--");
            args.Add(path);
        }
        return GitRunner.RunAsync(args, context, cancellationToken);
    }
}

public sealed class GitLogTool : ITool
{
    private const int DefaultCount = 10;

    public string Name => "git_log";

    public string Description =>
        "Shows recent commits (one line each, with refs). Pass 'count' to change how many and 'path' to see " +
        "only commits touching a file. Use this instead of running git through the shell.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("count", SchemaBuilder.Integer($"Number of commits to show (default {DefaultCount})")),
            ("path", SchemaBuilder.String("Only commits touching this file or directory")),
        ]);

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"GitLog({JsonArgs.GetInt(arguments, "count") ?? DefaultCount})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        int count = Math.Clamp(JsonArgs.GetInt(arguments, "count") ?? DefaultCount, 1, 200);
        var args = new List<string> { "log", "--oneline", "--decorate", "-n", count.ToString() };
        if (JsonArgs.GetString(arguments, "path") is { Length: > 0 } path)
        {
            args.Add("--");
            args.Add(path);
        }
        return GitRunner.RunAsync(args, context, cancellationToken);
    }
}

public sealed class GitShowTool : ITool
{
    public string Name => "git_show";

    public string Description =>
        "Shows one commit: message, stats and patch. 'ref' defaults to HEAD. " +
        "Use this instead of running git through the shell.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("ref", SchemaBuilder.String("Commit hash, branch, tag or revision expression (default HEAD)")),
            ("stat", SchemaBuilder.Boolean("Only show the change summary, not the full patch")),
        ]);

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"GitShow({JsonArgs.GetString(arguments, "ref") ?? "HEAD"})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var args = new List<string> { "show" };
        if (JsonArgs.GetBool(arguments, "stat"))
            args.Add("--stat");
        args.Add(JsonArgs.GetString(arguments, "ref") is { Length: > 0 } reference ? reference : "HEAD");
        return GitRunner.RunAsync(args, context, cancellationToken);
    }
}

public sealed class GitBlameTool : ITool
{
    public string Name => "git_blame";

    public string Description =>
        "Shows who last changed each line of a file. Optional 'start_line'/'end_line' narrow the range. " +
        "Use this instead of running git through the shell.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("file_path", SchemaBuilder.String("File to blame (absolute or working-directory-relative)")),
            ("start_line", SchemaBuilder.Integer("First line of the range (1-based)")),
            ("end_line", SchemaBuilder.Integer("Last line of the range")),
        ],
        "file_path");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"GitBlame({JsonArgs.GetString(arguments, "file_path") ?? "?"})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = JsonArgs.GetString(arguments, "file_path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ToolResult.Error("file_path is required."));
        var args = new List<string> { "blame" };
        int? start = JsonArgs.GetInt(arguments, "start_line");
        int? end = JsonArgs.GetInt(arguments, "end_line");
        if (start is > 0)
            args.Add($"-L{start},{(end is > 0 && end >= start ? end.ToString() : start.ToString())}");
        args.Add("--");
        args.Add(context.ResolvePath(path));
        return GitRunner.RunAsync(args, context, cancellationToken);
    }
}
