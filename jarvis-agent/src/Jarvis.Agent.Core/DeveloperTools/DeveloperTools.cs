using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.DeveloperTools;

internal static class DeveloperPathPolicy
{
    public static string Resolve(AgentExecutionContext context, string? path, bool requireFile = false, bool requireDirectory = false)
    {
        var allowed = new WorkspaceDirectories(context.Workspace, context.AdditionalDirectories);
        var candidate = string.IsNullOrWhiteSpace(path) ? allowed.Primary : Path.GetFullPath(path, allowed.Primary);
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var inside = allowed.Directories.Any(root =>
            candidate.Equals(root, comparer) || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparer) ||
            candidate.StartsWith(root + Path.AltDirectorySeparatorChar, comparer));
        if (!inside) throw new UnauthorizedAccessException("Developer tool path must stay inside the selected workspace directories.");
        if (requireFile && !File.Exists(candidate)) throw new FileNotFoundException("Developer tool file was not found.", candidate);
        if (requireDirectory && !Directory.Exists(candidate)) throw new DirectoryNotFoundException("Developer tool directory was not found: " + candidate);
        if (!requireFile && !requireDirectory && !File.Exists(candidate) && !Directory.Exists(candidate))
            throw new FileNotFoundException("Developer tool path was not found.", candidate);
        return candidate;
    }
}

public sealed class DeveloperSymbolSearchTool : IAgentTool
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".svn", ".hg", "bin", "obj", "node_modules", ".idea", ".vs" };
    private const int MaxFiles = 2_000;

    public ToolDescriptor Descriptor { get; } = new(
        "developer.symbol_search", "developer__symbol_search", "developer",
        "Search source/text files for a symbol or exact text inside selected workspace directories. Skips VCS/build/dependency directories and returns bounded file/line matches.",
        WireJson.Element(new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", minLength = 1, maxLength = 200 },
                path = new { type = "string", minLength = 1, maxLength = 1024 },
                maxResults = new { type = "integer", minimum = 1, maximum = 100, @default = 50 }
            },
            required = new[] { "query" },
            additionalProperties = false
        }),
        ReadOnly: true);

    public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
    {
        var query = arguments.GetProperty("query").GetString()!;
        var path = arguments.TryGetProperty("path", out var pathNode) ? pathNode.GetString() : null;
        var max = arguments.TryGetProperty("maxResults", out var maxNode) ? maxNode.GetInt32() : 50;
        if (query.Length is < 1 or > 200 || max is < 1 or > 100) throw new ArgumentException("Invalid symbol search bounds.");
        var root = DeveloperPathPolicy.Resolve(context, path, requireDirectory: true);
        var matches = new List<object>(max);
        var truncated = false;
        var scanned = 0;
        foreach (var file in EnumerateFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned > MaxFiles) { truncated = true; break; }
            try
            {
                if (new FileInfo(file).Length > 2 * 1024 * 1024) continue;
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lineNumber++;
                    if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    if (matches.Count >= max) { truncated = true; break; }
                    var relative = Path.GetRelativePath(root, file);
                    matches.Add(new { file = relative, line = lineNumber, text = line.Length <= 500 ? line : line[..500] });
                }
                if (truncated && matches.Count >= max) break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { }
        }
        var payload = JsonSerializer.Serialize(new { query, root, scannedFiles = scanned, truncated, matches }, WireJson.Options);
        return Task.FromResult(new ToolReply(payload));
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            IEnumerable<string> dirs;
            IEnumerable<string> files;
            try
            {
                dirs = Directory.EnumerateDirectories(directory).ToArray();
                files = Directory.EnumerateFiles(directory).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var child in dirs)
                if (!ExcludedDirectories.Contains(Path.GetFileName(child))) stack.Push(child);
        }
    }
}

public sealed record DeveloperTestRunRequest(string Project, string? Filter, int TimeoutSeconds);
public sealed record DeveloperTestRunResult(int ExitCode, string Stdout, string Stderr);
public sealed record StructuredTestSummary(int ExitCode, int Passed, int Failed, int Skipped, int Total, string Output, bool Truncated);
public delegate Task<DeveloperTestRunResult> DeveloperTestRunner(DeveloperTestRunRequest request, CancellationToken cancellationToken);

public static partial class DotnetTestSummaryParser
{
    [GeneratedRegex(@"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryRegex();

    public static StructuredTestSummary Parse(string output, int exitCode, int maxOutputChars = 64_000)
    {
        if (maxOutputChars < 1) throw new ArgumentOutOfRangeException(nameof(maxOutputChars));
        output ??= "";
        var matches = SummaryRegex().Matches(output);
        var failed = 0; var passed = 0; var skipped = 0; var total = 0;
        foreach (Match match in matches)
        {
            failed += int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            passed += int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            skipped += int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            total += int.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        var truncated = output.Length > maxOutputChars;
        var bounded = truncated ? output[^maxOutputChars..] : output;
        return new(exitCode, passed, failed, skipped, total, bounded, truncated);
    }
}

public sealed class DeveloperTestTool : IAgentTool
{
    private readonly DeveloperTestRunner _runner;
    public DeveloperTestTool() : this(RunDotnetAsync) { }
    public DeveloperTestTool(DeveloperTestRunner runner) => _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public ToolDescriptor Descriptor { get; } = new(
        "developer.test", "developer__test", "developer",
        "Run dotnet test for a project/directory inside the selected workspace and return bounded structured passed/failed/skipped/total counts plus output. This tool may build/write project artifacts and is therefore mutating and sensitive.",
        WireJson.Element(new
        {
            type = "object",
            properties = new
            {
                project = new { type = "string", minLength = 1, maxLength = 1024 },
                filter = new { type = "string", minLength = 1, maxLength = 1000 },
                timeoutSeconds = new { type = "integer", minimum = 1, maximum = 1800, @default = 600 }
            },
            additionalProperties = false
        }),
        ReadOnly: false,
        Sensitive: true);

    public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
    {
        var projectArg = arguments.TryGetProperty("project", out var projectNode) ? projectNode.GetString() : null;
        var filter = arguments.TryGetProperty("filter", out var filterNode) ? filterNode.GetString() : null;
        var timeout = arguments.TryGetProperty("timeoutSeconds", out var timeoutNode) ? timeoutNode.GetInt32() : 600;
        if (filter?.Length > 1000 || timeout is < 1 or > 1800) throw new ArgumentException("Invalid developer test bounds.");
        var project = DeveloperPathPolicy.Resolve(context, projectArg);
        var result = await _runner(new(project, filter, timeout), cancellationToken).ConfigureAwait(false);
        var combined = string.IsNullOrEmpty(result.Stderr) ? result.Stdout : result.Stdout + "\n--- stderr ---\n" + result.Stderr;
        var summary = DotnetTestSummaryParser.Parse(combined, result.ExitCode);
        return new ToolReply(JsonSerializer.Serialize(summary, WireJson.Options), IsError: result.ExitCode != 0);
    }

    private static async Task<DeveloperTestRunResult> RunDotnetAsync(DeveloperTestRunRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Directory.Exists(request.Project) ? request.Project : Path.GetDirectoryName(request.Project)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("test"); start.ArgumentList.Add(request.Project); start.ArgumentList.Add("--nologo");
        if (!string.IsNullOrWhiteSpace(request.Filter)) { start.ArgumentList.Add("--filter"); start.ArgumentList.Add(request.Filter); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start dotnet test.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}

public sealed record DapAdapterLaunchRequest(string AdapterPath, IReadOnlyList<string> Arguments, string WorkingDirectory);
public delegate Task<IDapOwnedProcess> DapProcessFactory(DapAdapterLaunchRequest request, CancellationToken cancellationToken);

public interface IDapOwnedProcess : IAsyncDisposable
{
    int Id { get; }
    bool HasExited { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

public sealed class DapAdapterLauncher
{
    private readonly DapProcessFactory _factory;
    public DapAdapterLauncher() : this(StartProcessAsync) { }
    public DapAdapterLauncher(DapProcessFactory factory) => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<DapAdapterSession> LaunchAsync(DapAdapterLaunchRequest request, WorkspaceDirectories workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspace);
        var adapter = Path.GetFullPath(request.AdapterPath);
        if (!Path.IsPathRooted(request.AdapterPath) || !File.Exists(adapter)) throw new FileNotFoundException("DAP adapter executable must be an existing absolute local file.", adapter);
        if (request.Arguments.Count > 64 || request.Arguments.Any(arg => arg is null || arg.Length > 2048)) throw new ArgumentException("DAP adapter arguments exceed bounded limits.");
        var context = new AgentExecutionContext(workspace.Primary, "dap", "local") { AdditionalDirectories = workspace.Additional };
        var working = DeveloperPathPolicy.Resolve(context, request.WorkingDirectory, requireDirectory: true);
        var normalized = request with { AdapterPath = adapter, WorkingDirectory = working, Arguments = request.Arguments.ToArray() };
        var process = await _factory(normalized, cancellationToken).ConfigureAwait(false);
        return new DapAdapterSession(process);
    }

    private static Task<IDapOwnedProcess> StartProcessAsync(DapAdapterLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(request.AdapterPath)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start DAP adapter.");
        return Task.FromResult<IDapOwnedProcess>(new ProcessDapOwnedProcess(process));
    }
}

public sealed class DapAdapterSession(IDapOwnedProcess process) : IAsyncDisposable
{
    private int _stopped;
    public int ProcessId => process.Id;
    public bool HasExited => process.HasExited;
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => process.WaitForExitAsync(cancellationToken);
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        if (!process.HasExited) process.Kill();
        await process.DisposeAsync().ConfigureAwait(false);
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}

internal sealed class ProcessDapOwnedProcess(Process process) : IDapOwnedProcess
{
    public int Id => process.Id;
    public bool HasExited { get { try { return process.HasExited; } catch { return true; } } }
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
    public void Kill() { if (!HasExited) process.Kill(entireProcessTree: true); }
    public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }
}
