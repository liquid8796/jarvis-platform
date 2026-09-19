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

public sealed record DeveloperTestRunRequest(string Project, string? Filter, int TimeoutSeconds)
{
    public string Framework { get; init; } = "dotnet";
    public IReadOnlyList<string>? Argv { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public string ReportFormat { get; init; } = "trx";
    public string? ReportFile { get; init; }
    public string ArtifactDirectory { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, string, Task>? ReportOutput { get; init; }
}
public sealed record DeveloperTestRunResult(int ExitCode, string Stdout, string Stderr)
{
    public string? ReportContent { get; init; }
    public string? ReportError { get; init; }
    public string? StdoutPath { get; init; }
    public string? StderrPath { get; init; }
    public bool LogsTruncated { get; init; }
    public IReadOnlyList<string> Argv { get; init; } = [];
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
}
public sealed record StructuredTestSummary(int ExitCode, int Passed, int Failed, int Skipped, int Total, string Output, bool Truncated)
{
    public int SchemaVersion { get; init; } = 1;
    public string CallId { get; init; } = "";
    public string ScopeId { get; init; } = "";
    public string State { get; init; } = "unrecognized";
    public int Executed { get; init; }
    public bool ReportParsed { get; init; }
    public string Framework { get; init; } = "";
    public string ReportFormat { get; init; } = "";
    public string RunId { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public IReadOnlyList<string> Argv { get; init; } = [];
    public string? ReportPath { get; init; }
    public string? ReportSha256 { get; init; }
    public string? StdoutPath { get; init; }
    public string? StderrPath { get; init; }
    public string? StdoutSha256 { get; init; }
    public string? StderrSha256 { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
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
    private readonly string _artifactRoot;
    public DeveloperTestTool() : this(BoundedDeveloperTestRunner.RunAsync) { }
    public DeveloperTestTool(DeveloperTestRunner runner, string? artifactRoot = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _artifactRoot = Path.GetFullPath(artifactRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAgent", "qa-artifacts"));
    }

    public ToolDescriptor Descriptor { get; } = new(
        "developer.test", "developer__test", "developer",
        "Run bounded tests and validate a fresh machine-readable report. Default dotnet emits TRX; python emits pytest JUnit; go emits go-test JSON. For node/native/custom supply argv and reportFormat. {report}/{results} placeholders are substituted inside individual argv tokens without a shell. Zero, skipped-only, missing, inconsistent or unrecognized test results never pass. Private report/stdout/stderr artifacts and hashes are returned.",
        WireJson.Element(new
        {
            type = "object",
            properties = new
            {
                project = new { type = "string", minLength = 1, maxLength = 1024 },
                filter = new { type = "string", minLength = 1, maxLength = 1000, description = "Filter for default dotnet/python/go commands; include custom framework filters directly in argv." },
                timeoutSeconds = new { type = "integer", minimum = 1, maximum = 1800, @default = 600 },
                framework = new { type = "string", @enum = new[] { "dotnet", "python", "node", "go", "native", "custom" } },
                argv = new { type = "array", minItems = 1, maxItems = 64, items = new { type = "string", maxLength = 4096 } },
                environment = new { type = "object", maxProperties = 128, propertyNames = new { pattern = "^[A-Za-z_][A-Za-z0-9_]*$" },
                    additionalProperties = new { type = "string", maxLength = 32768 } },
                environmentFromProcess = new { type = "object", maxProperties = 128, propertyNames = new { pattern = "^[A-Za-z_][A-Za-z0-9_]*$" },
                    additionalProperties = new { type = "string", maxLength = 200, pattern = "^[A-Za-z_][A-Za-z0-9_]*$" },
                    description = "Map child environment names to existing host environment names. Values are resolved in memory and never added to argv or result metadata; missing names fail explicitly." },
                reportFormat = new { type = "string", @enum = new[] { "trx", "junit", "jest", "vitest", "go-json" } },
                reportFile = new { type = "string", minLength = 1, maxLength = 2048, description = "Optional report output path within the workspace; '-' parses stdout. Otherwise use {report} in argv for a unique private output file." }
            },
            additionalProperties = false
        }),
        ReadOnly: false,
        Sensitive: true);

    public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
    {
        context.SessionCancellation.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.SessionCancellation);
        cancellationToken = lifetime.Token;
        var invocationStarted = DateTimeOffset.UtcNow;
        var projectArg = arguments.TryGetProperty("project", out var projectNode) ? projectNode.GetString() : null;
        var filter = arguments.TryGetProperty("filter", out var filterNode) ? filterNode.GetString() : null;
        var timeout = arguments.TryGetProperty("timeoutSeconds", out var timeoutNode) ? timeoutNode.GetInt32() : 600;
        if (filter?.Length > 1000 || timeout is < 1 or > 1800) throw new ArgumentException("Invalid developer test bounds.");
        var project = DeveloperPathPolicy.Resolve(context, projectArg);
        var framework = arguments.TryGetProperty("framework", out var frameworkNode) ? frameworkNode.GetString() ?? "" : "dotnet";
        if (framework is not ("dotnet" or "python" or "node" or "go" or "native" or "custom")) throw new ArgumentException("Unsupported test framework.");
        var format = arguments.TryGetProperty("reportFormat", out var formatNode) ? formatNode.GetString() ?? "" : framework switch
        { "dotnet" => "trx", "python" => "junit", "go" => "go-json", _ => "" };
        if (format is not ("trx" or "junit" or "jest" or "vitest" or "go-json")) throw new ArgumentException("An explicit supported reportFormat is required.");
        var argv = arguments.TryGetProperty("argv", out var argvNode) ? argvNode.Deserialize<string[]>() : null;
        if (argv is not null && (argv.Length is < 1 or > 64 || string.IsNullOrWhiteSpace(argv[0]) || argv.Any(arg => arg is null || arg.Length > 4096 || arg.Contains('\0')) || argv.Sum(arg => arg.Length) > 32768))
            throw new ArgumentException("Test argv exceeds bounded limits.");
        if (argv is not null && !string.IsNullOrWhiteSpace(filter))
            throw new ArgumentException("With explicit argv, include the framework filter in argv; filter is only applied by the default framework commands.");
        if (argv is null && framework is "node" or "native" or "custom") throw new ArgumentException("This framework requires explicit argv.");
        var environment = ResolveEnvironment(arguments);
        var reportFile = arguments.TryGetProperty("reportFile", out var reportNode) ? reportNode.GetString() : null;
        if (reportFile is not null && reportFile != "-") reportFile = ResolveReportOutput(context, reportFile);
        var runId = Guid.NewGuid().ToString("N");
        var scope = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            (context.OwnerId ?? "") + "\n" + (context.AgentDeviceId ?? "") + "\n" + context.IsolationScopeId))).ToLowerInvariant();
        var directory = Path.Combine(_artifactRoot, scope, runId);
        RejectLinks(directory);
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        var result = await _runner(new(project, filter, timeout)
        {
            Framework = framework, Argv = argv, Environment = environment, ReportFormat = format, ReportFile = reportFile,
            ArtifactDirectory = directory, ReportOutput = context.ReportOutput
        }, cancellationToken).ConfigureAwait(false);
        var combined = string.IsNullOrEmpty(result.Stderr) ? result.Stdout : result.Stdout + "\n--- stderr ---\n" + result.Stderr;
        var summary = StructuredTestReportParser.Parse(format, result.ReportContent ?? "", result.ExitCode, combined) with
        {
            Framework = framework, RunId = runId, CallId = context.CallId, ScopeId = context.IsolationScopeId,
            WorkingDirectory = Directory.Exists(project) ? project : Path.GetDirectoryName(project)!,
            Argv = result.Argv.Count > 0 ? result.Argv : argv ?? [],
            StartedAt = result.StartedAt == default ? invocationStarted : result.StartedAt,
            FinishedAt = result.FinishedAt == default ? DateTimeOffset.UtcNow : result.FinishedAt
        };
        if (result.ReportError is { Length: > 0 } reportError)
            summary = summary with { State = result.ExitCode == 0 ? "invalid_report" : "failed", ReportParsed = false, Diagnostics = summary.Diagnostics.Append(reportError).ToArray() };
        var stdoutPath = result.StdoutPath ?? Path.Combine(directory, "stdout.txt");
        var stderrPath = result.StderrPath ?? Path.Combine(directory, "stderr.txt");
        if (result.StdoutPath is null) await File.WriteAllTextAsync(stdoutPath, result.Stdout, cancellationToken).ConfigureAwait(false);
        if (result.StderrPath is null) await File.WriteAllTextAsync(stderrPath, result.Stderr, cancellationToken).ConfigureAwait(false);
        string? reportPath = null;
        if (result.ReportContent is { } report && Encoding.UTF8.GetByteCount(report) <= StructuredTestReportParser.MaxReportBytes)
        {
            reportPath = Path.Combine(directory, "report.txt");
            await File.WriteAllTextAsync(reportPath, report, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        summary = summary with
        {
            ReportPath = reportPath, ReportSha256 = reportPath is null ? null : FileHash(reportPath),
            StdoutPath = stdoutPath, StderrPath = stderrPath, StdoutSha256 = FileHash(stdoutPath), StderrSha256 = FileHash(stderrPath),
            Truncated = summary.Truncated || result.LogsTruncated
        };
        return new ToolReply(JsonSerializer.Serialize(summary, WireJson.Options), IsError: summary.State != "passed");
    }

    private static string FileHash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant(); }

    private static IReadOnlyDictionary<string, string>? ResolveEnvironment(JsonElement arguments)
    {
        var values = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var field in new[] { "environment", "environmentFromProcess" })
        {
            if (!arguments.TryGetProperty(field, out var source)) continue;
            if (source.ValueKind != JsonValueKind.Object) throw new ArgumentException(field + " must be an object.");
            foreach (var item in source.EnumerateObject())
            {
                if (values.Count >= 128 || !EnvironmentName(item.Name) || item.Value.ValueKind != JsonValueKind.String || values.ContainsKey(item.Name))
                    throw new ArgumentException("Invalid, duplicate or excessive test environment names.");
                var value = item.Value.GetString()!;
                if (field == "environmentFromProcess")
                {
                    if (!EnvironmentName(value)) throw new ArgumentException("Invalid source environment name.");
                    value = System.Environment.GetEnvironmentVariable(value) ?? throw new InvalidOperationException("Required host environment variable is missing: " + value);
                }
                if (value.Length > 32768 || value.Contains('\0')) throw new ArgumentException("Test environment value exceeds process-tool bounds.");
                values.Add(item.Name, value);
            }
        }
        return values.Count == 0 ? null : values;
    }

    private static bool EnvironmentName(string name) => name.Length is > 0 and <= 200 &&
        Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    private static void RejectLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Private test artifacts must not contain linked paths.");
    }

    private static string ResolveReportOutput(AgentExecutionContext context, string path)
    {
        if (path.Length is < 1 or > 2048) throw new ArgumentException("Invalid reportFile.");
        var full = Path.GetFullPath(path, context.Workspace);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!new WorkspaceDirectories(context.Workspace, context.AdditionalDirectories).Directories.Any(root =>
            full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)))
            throw new UnauthorizedAccessException("Test report must remain within selected workspace directories.");
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Test report path must not contain linked paths.");
        return full;
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
