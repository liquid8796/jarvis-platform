using System.Diagnostics;
using System.IO;

namespace JarvisCode.Cli;

/// <summary>A CLI-created worktree stays on disk when it contains work or new commits.</summary>
internal sealed class CliWorkspace(string original, string path, string branch, string originalHead) : IAsyncDisposable
{
    public string Path { get; } = path;
    public string Branch { get; } = branch;

    public static async Task<CliWorkspace> CreateAsync(string cwd, string? name, CancellationToken cancellationToken)
    {
        var root = await GitAsync(cwd, cancellationToken, "rev-parse", "--show-toplevel");
        if (root.Code != 0) throw new CliError("--worktree requires a Git repository.");
        var slug = name ?? "session-" + Guid.NewGuid().ToString("N")[..10];
        if (slug.Length == 0 || slug is "." or ".." || slug.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new CliError("Worktree names must contain only letters, numbers, '-' and '_'.");
        var head = await GitAsync(cwd, cancellationToken, "rev-parse", "HEAD");
        if (head.Code != 0) throw new CliError("Commit the repository's initial revision before creating a worktree.");
        var target = System.IO.Path.Combine(root.Output.Trim(), ".jarvis", "worktrees", slug);
        var branch = "worktree-" + slug;
        if (Directory.Exists(target)) throw new CliError("Worktree directory already exists: " + target);
        var result = await GitAsync(cwd, cancellationToken, "worktree", "add", "-b", branch, target, "HEAD");
        if (result.Code != 0) throw new CliError("Could not create worktree: " + result.Error.Trim());
        return new CliWorkspace(cwd, target, branch, head.Output.Trim());
    }

    public async ValueTask DisposeAsync()
    {
        if (!Directory.Exists(Path)) return;
        var status = await GitAsync(Path, CancellationToken.None, "status", "--porcelain", "--untracked-files=all");
        var head = await GitAsync(Path, CancellationToken.None, "rev-parse", "HEAD");
        if (status.Code != 0 || head.Code != 0 || status.Output.Trim().Length > 0 || head.Output.Trim() != originalHead)
        { Console.Error.WriteLine("Worktree retained at " + Path + " (branch " + Branch + ")."); return; }
        var removed = await GitAsync(original, CancellationToken.None, "worktree", "remove", Path);
        if (removed.Code == 0) await GitAsync(original, CancellationToken.None, "branch", "-d", Branch);
        else Console.Error.WriteLine("Worktree retained at " + Path + ": " + removed.Error.Trim());
    }

    internal static async Task<(int Code, string Output, string Error)> GitAsync(string cwd,
        CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new CliError("Git could not start.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        return (process.ExitCode, await stdout, await stderr);
    }
}
