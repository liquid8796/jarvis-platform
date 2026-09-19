using System.Diagnostics;
using System.Text;

namespace Jarvis.Agent.Core.DeveloperTools;

/// <summary>Owned argv execution behind the guarded developer.test tool; never joins arguments into a shell command.</summary>
public static class BoundedDeveloperTestRunner
{
    public const int MaxLogBytes = 4 * 1024 * 1024;
    private sealed record LogResult(string Tail, bool Truncated);
    private sealed record Stamp(long Length, long ModifiedTicks);

    public static async Task<DeveloperTestRunResult> RunAsync(DeveloperTestRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ArtifactDirectory)) throw new ArgumentException("A private test artifact directory is required.");
        Directory.CreateDirectory(request.ArtifactDirectory);
        var started = DateTimeOffset.UtcNow;
        var working = Directory.Exists(request.Project) ? request.Project : Path.GetDirectoryName(request.Project)!;
        var extension = request.ReportFormat switch { "trx" => ".trx", "junit" => ".xml", _ => ".json" };
        var trxBundle = request.Framework == "dotnet" && request.Argv is null && request.ReportFile is null;
        var reportPath = request.ReportFile ?? (request.Framework == "go" ? "-" : Path.Combine(request.ArtifactDirectory, "raw-report" + extension));
        var argv = BuildArgv(request, reportPath);
        var before = reportPath == "-" ? null : FileStamp(reportPath);
        var stdoutPath = Path.Combine(request.ArtifactDirectory, "stdout.txt");
        var stderrPath = Path.Combine(request.ArtifactDirectory, "stderr.txt");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        var start = new ProcessStartInfo(argv[0])
        {
            WorkingDirectory = working, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true
        };
        foreach (var argument in argv.Skip(1)) start.ArgumentList.Add(argument);
        if (request.Environment is not null) foreach (var item in request.Environment) start.Environment[item.Key] = item.Value;
        using var owned = new OwnedProcessLease();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start the test runner.");
        owned.Attach(process);
        process.StandardInput.Close();
        var stdoutTask = DrainAsync(process.StandardOutput, stdoutPath, "stdout", request.ReportOutput, stop.Token);
        var stderrTask = DrainAsync(process.StandardError, stderrPath, "stderr", request.ReportOutput, stop.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            owned.Stop(); // A finished test command may not leave descendants or inherited log pipes alive.
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            owned.Stop();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            owned.Stop();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : new LogResult(await TailAsync(stdoutPath).ConfigureAwait(false), true);
        var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : new LogResult(await TailAsync(stderrPath).ConfigureAwait(false), true);
        string? content = null;
        string? reportError = timedOut ? "Test process exceeded its timeout and its owned process tree was terminated." : null;
        if (!timedOut)
        {
            try
            {
                if (trxBundle)
                {
                    var reports = Directory.Exists(Path.Combine(request.ArtifactDirectory, "raw-results"))
                        ? Directory.GetFiles(Path.Combine(request.ArtifactDirectory, "raw-results"), "*.trx", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray() : [];
                    if (reports.Length is < 1 or > 128) throw new InvalidDataException("dotnet test did not produce a bounded set of TRX reports.");
                    var bundle = new System.Xml.Linq.XElement("TestRuns");
                    long totalBytes = 0;
                    foreach (var report in reports)
                    {
                        RejectLinks(report);
                        totalBytes += new FileInfo(report).Length;
                        if (totalBytes > StructuredTestReportParser.MaxReportBytes) throw new InvalidDataException("Combined TRX reports exceed 8 MiB.");
                        using var xml = System.Xml.XmlReader.Create(report, new System.Xml.XmlReaderSettings
                        { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = StructuredTestReportParser.MaxReportBytes });
                        var root = System.Xml.Linq.XDocument.Load(xml).Root ?? throw new InvalidDataException("Empty TRX report.");
                        if (root.Name.LocalName != "TestRun") throw new InvalidDataException("Invalid TRX report root.");
                        bundle.Add(root);
                    }
                    content = bundle.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
                }
                else if (reportPath == "-")
                {
                    if (stdout.Truncated) throw new InvalidDataException("Report on stdout exceeded the bounded log size.");
                    content = await File.ReadAllTextAsync(stdoutPath, new UTF8Encoding(false, true), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    RejectLinks(reportPath);
                    var after = FileStamp(reportPath) ?? throw new InvalidDataException("The test runner did not produce the required report file.");
                    if (before is not null && after == before) throw new InvalidDataException("The report file was not refreshed by this test invocation.");
                    if (after.Length > StructuredTestReportParser.MaxReportBytes) throw new InvalidDataException("The test report exceeds 8 MiB.");
                    using var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                    content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    if (FileStamp(reportPath) != after) throw new InvalidDataException("The report changed while it was being captured.");
                }
                if (content is not null && Encoding.UTF8.GetByteCount(content) > StructuredTestReportParser.MaxReportBytes)
                    throw new InvalidDataException("The normalized test report exceeds 8 MiB.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException or DecoderFallbackException)
            { content = null; reportError = ex.Message; }
        }
        return new(timedOut ? -1 : process.ExitCode, stdout.Tail, stderr.Tail)
        {
            ReportContent = content, ReportError = reportError, StdoutPath = stdoutPath, StderrPath = stderrPath,
            LogsTruncated = stdout.Truncated || stderr.Truncated, Argv = argv, StartedAt = started, FinishedAt = DateTimeOffset.UtcNow
        };
    }

    private static string[] BuildArgv(DeveloperTestRunRequest request, string reportPath)
    {
        string[] argv;
        if (request.Argv is { Count: > 0 })
        {
            argv = request.Argv.Select(arg => arg.Replace("{report}", reportPath, StringComparison.Ordinal)
                .Replace("{results}", request.ArtifactDirectory, StringComparison.Ordinal)).ToArray();
            if (request.ReportFile is null && reportPath != "-" && !request.Argv.Any(arg => arg.Contains("{report}", StringComparison.Ordinal) || arg.Contains("{results}", StringComparison.Ordinal)))
                throw new ArgumentException("Explicit argv must name reportFile or use {report}/{results} for its fresh report output.");
        }
        else if (request.Framework == "dotnet")
        {
            if (request.ReportFormat != "trx" || reportPath == "-") throw new ArgumentException("Default dotnet execution requires a TRX file report.");
            if (request.ReportFile is null)
                argv = ["dotnet", "test", request.Project, "--nologo", "--logger", "trx", "--results-directory", Path.Combine(request.ArtifactDirectory, "raw-results")];
            else
            {
                if (!request.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Explicit dotnet reportFile requires one .csproj; omit reportFile to collect every solution/project TRX report.");
                argv = ["dotnet", "test", request.Project, "--nologo", "--logger", "trx;LogFileName=" + Path.GetFileName(reportPath), "--results-directory", Path.GetDirectoryName(reportPath)!];
            }
            if (!string.IsNullOrWhiteSpace(request.Filter)) argv = [.. argv, "--filter", request.Filter];
        }
        else if (request.Framework == "python")
        {
            if (request.ReportFormat != "junit" || reportPath == "-") throw new ArgumentException("Default pytest execution requires a JUnit file report.");
            argv = ["python", "-m", "pytest", request.Project, "--junitxml=" + reportPath];
            if (!string.IsNullOrWhiteSpace(request.Filter)) argv = [.. argv, "-k", request.Filter];
        }
        else if (request.Framework == "go")
        {
            if (request.ReportFormat != "go-json" || reportPath != "-") throw new ArgumentException("Default go execution uses go-json on stdout.");
            argv = ["go", "test", "-json", Directory.Exists(request.Project) ? "./..." : request.Project];
            if (!string.IsNullOrWhiteSpace(request.Filter)) argv = [.. argv, "-run", request.Filter];
        }
        else throw new ArgumentException("Explicit argv is required for this test framework.");
        if (argv.Length is < 1 or > 64 || argv.Any(arg => arg.Length > 8192 || arg.Contains('\0')) || argv.Sum(arg => arg.Length + 3) > 32768 || string.IsNullOrWhiteSpace(argv[0]))
            throw new ArgumentException("Invalid bounded test command.");
        return argv;
    }

    private static async Task<LogResult> DrainAsync(StreamReader reader, string path, string stream,
        Func<string, string, Task>? output, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        var buffer = new byte[8192];
        var chars = new char[reader.CurrentEncoding.GetMaxCharCount(buffer.Length)];
        var decoder = reader.CurrentEncoding.GetDecoder();
        var tail = new StringBuilder();
        var bytesWritten = 0;
        var truncated = false;
        var truncationReported = false;
        while (true)
        {
            var count = await reader.BaseStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            var charCount = decoder.GetChars(buffer.AsSpan(0, count), chars.AsSpan(), flush: false);
            var chunk = new string(chars, 0, charCount);
            tail.Append(chunk);
            if (tail.Length > 64000) tail.Remove(0, tail.Length - 64000);
            var length = Math.Min(count, MaxLogBytes - bytesWritten);
            if (length > 0) { await file.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false); bytesWritten += length; }
            if (length < count) truncated = true;
            if (output is not null && length > 0) await output(stream, chunk).ConfigureAwait(false);
            if (output is not null && truncated && !truncationReported)
            {
                truncationReported = true;
                await output(stream, "\n[jarvis: output exceeded the 4 MiB artifact/stream budget; remaining output is drained without retaining it]\n").ConfigureAwait(false);
            }
        }
        return new(tail.ToString(), truncated);
    }

    private static Stamp? FileStamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new(file.Length, file.LastWriteTimeUtc.Ticks) : null;
    }
    private static void RejectLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Test report path contains a linked path.");
    }
    private static async Task<string> TailAsync(string path)
    {
        if (!File.Exists(path)) return "";
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        return text.Length <= 64000 ? text : text[^64000..];
    }
}
