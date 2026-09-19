using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.DeveloperTools;
using Jarvis.Protocol;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core.Tests;

/// <summary>Actual local I/O corpus, including failures that an exit-code-only checker misses.</summary>
public sealed class DeveloperVerificationCorpusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-verification-corpus-" + Guid.NewGuid().ToString("N"));
    private readonly DeveloperVerifyTool _tool;
    public DeveloperVerificationCorpusTests()
    {
        Directory.CreateDirectory(_root);
        _tool = new DeveloperVerifyTool(Path.Combine(_root, "private-reports"));
    }
    private AgentExecutionContext Context => new(_root, "corpus-call", "js_" + new string('a', 32))
    { OwnerId = "fixture-owner", AgentDeviceId = "fixture-device" };

    private async Task<JsonElement> Verify(object arguments, string status)
    {
        var reply = await _tool.ExecuteAsync(WireJson.Element(arguments), Context, default);
        var result = JsonSerializer.Deserialize<JsonElement>(reply.Text);
        Assert.Equal(status, result.GetProperty("status").GetString());
        Assert.Equal(status != "passed", reply.IsError);
        Assert.Equal(status == "passed", result.GetProperty("passed").GetBoolean());
        var report = result.GetProperty("reportPath").GetString()!;
        Assert.StartsWith(Path.Combine(_root, "private-reports") + Path.DirectorySeparatorChar, report);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(report))).ToLowerInvariant();
        Assert.Equal(hash, result.GetProperty("reportSha256").GetString());
        var stored = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(report));
        Assert.Equal(result.GetProperty("runId").GetString(), stored.GetProperty("runId").GetString());
        Assert.Equal(status, stored.GetProperty("status").GetString());
        return result;
    }

    [Fact]
    public void Registered_tool_is_sensitive_mutating_and_uses_the_production_schema()
    {
        var descriptor = Assert.Single(AgentCoreHostTools.Descriptors.Where(item => item.Id == "developer.verify"));
        Assert.Equal("developer__verify", descriptor.Name);
        Assert.True(descriptor.Sensitive); Assert.False(descriptor.ReadOnly);
        Assert.Equal(DeveloperVerifySchema.Create().GetRawText(), descriptor.InputSchema.GetRawText());
    }

    [Fact]
    public async Task Http_real_status_body_schema_and_auth_are_verified_independently()
    {
        await using var server = new FixtureHttpServer();
        var healthy = await Verify(new
        {
            kind = "http", url = server.Url("/api"), expectedStatus = 200,
            assertions = new[] { new { path = "/answer", op = "equals", expected = 42 } },
            schema = new { type = "object", required = new[] { "answer" }, properties = new { answer = new { type = "integer" } }, additionalProperties = false }
        }, "passed");
        Assert.True(healthy.GetProperty("assertionCount").GetInt32() >= 3);
        await Verify(new { kind = "http", url = server.Url("/wrong"), expectedStatus = 200,
            assertions = new[] { new { path = "/answer", op = "equals", expected = 42 } } }, "failed");
        await Verify(new { kind = "http", url = server.Url("/tenant"), expectedStatus = 401,
            assertions = new[] { new { path = "/error", op = "equals", expected = "unauthorized" } } }, "passed");
        var authenticated = await Verify(new { kind = "http", url = server.Url("/tenant"), expectedStatus = 200,
            headers = new { Authorization = "Bearer corpus-fixture" },
            assertions = new[] { new { path = "/tenant", op = "equals", expected = "tenant-a" } } }, "passed");
        await Verify(new { kind = "http", url = server.Url("/tenant"), expectedStatus = 200,
            headers = new { Authorization = "Bearer corpus-fixture" },
            assertions = new[] { new { path = "/tenant", op = "equals", expected = "tenant-b" } } }, "failed");
        Assert.DoesNotContain("corpus-fixture", authenticated.GetRawText());
    }

    [Fact]
    public async Task Readiness_is_polled_but_mutating_request_is_sent_once()
    {
        await using var server = new FixtureHttpServer();
        var result = await Verify(new { kind = "http", url = server.Url("/mutation"), method = "POST",
            body = new { value = "fixture" }, expectedStatus = 200,
            readiness = new { url = server.Url("/ready"), expectedStatus = 200, timeoutMs = 3000 },
            assertions = new[] { new { path = "/calls", op = "equals", expected = 1 } } }, "passed");
        Assert.Equal(3, result.GetProperty("observations").GetProperty("readinessAttempts").GetInt32());
        Assert.Equal(1, server.Mutations);
    }

    [Fact]
    public async Task Redirects_are_not_followed_and_remote_access_is_explicit()
    {
        await using var server = new FixtureHttpServer();
        await Verify(new { kind = "http", url = server.Url("/redirect"), expectedStatus = 200 }, "failed");
        Assert.Equal(0, server.ApiCalls);
        await Verify(new { kind = "http", url = "https://example.invalid/private", expectedStatus = 200 }, "blocked");
        await Verify(new { kind = "http", url = "http://example.invalid/private", allowRemote = true, expectedStatus = 200 }, "blocked");
    }

    [Fact]
    public async Task Bounded_http_timeout_and_body_limits_never_become_success()
    {
        await using var server = new FixtureHttpServer();
        var clock = Stopwatch.StartNew();
        await Verify(new { kind = "http", url = server.Url("/api"), expectedStatus = 200,
            readiness = new { url = server.Url("/never-ready"), timeoutMs = 200 } }, "blocked");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        await Verify(new { kind = "http", url = server.Url("/large"), expectedStatus = 200 }, "not_supported");
    }

    [Fact]
    public async Task Data_ml_numeric_tolerance_and_required_schema_detect_wrong_results()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "metric.json"), "{\"loss\":0.10001,\"count\":3,\"shape\":[2,4]}");
        await Verify(new { kind = "json", path = "metric.json",
            assertions = new object[] { new { path = "/loss", op = "near", expected = 0.1, tolerance = 0.0001 }, new { path = "/shape", op = "equals", expected = new[] { 2, 4 } } } }, "passed");
        await Verify(new { kind = "json", path = "metric.json",
            assertions = new[] { new { path = "/loss", op = "near", expected = 0.2, tolerance = 0.0001 } } }, "failed");
        await Verify(new { kind = "json", path = "metric.json", schema = new { type = "object", required = new[] { "accuracy" } } }, "failed");
        await Verify(new { kind = "json", path = "metric.json", schema = new { type = "object", properties = new { missing = new { pattern = "unsupported-even-when-missing" } } } }, "not_supported");
        await File.WriteAllTextAsync(Path.Combine(_root, "duplicate.json"), "{\"answer\":0,\"answer\":42}");
        await Verify(new { kind = "json", path = "duplicate.json", assertions = new[] { new { path = "/answer", op = "equals", expected = 42 } } }, "failed");
    }

    [Fact]
    public async Task Actual_cli_exit_zero_with_wrong_output_is_rejected_by_output_assertion()
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        { WorkingDirectory = _root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (OperatingSystem.IsWindows()) { start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("echo wrong-result"); }
        else { start.ArgumentList.Add("-c"); start.ArgumentList.Add("printf wrong-result"); }
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        await File.WriteAllTextAsync(Path.Combine(_root, "cli-output.txt"), output);
        await Verify(new { kind = "file", path = "cli-output.txt", textContains = "expected-result" }, "failed");
        await Verify(new { kind = "file", path = "cli-output.txt", textContains = "wrong-result" }, "passed");
    }

    [Fact]
    public async Task Native_package_archive_requires_resources_and_matches_hash()
    {
        var package = Path.Combine(_root, "package.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using var output = new StreamWriter(archive.CreateEntry("lib/app.dll").Open()); output.Write("fixture DLL marker");
        }
        await Verify(new { kind = "file", path = "package.zip", archiveMembers = new[] { "lib/app.dll" } }, "passed");
        await Verify(new { kind = "file", path = "package.zip", archiveMembers = new[] { "lib/app.dll", "assets/runtime.dat" } }, "failed");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package))).ToLowerInvariant();
        await Verify(new { kind = "file", path = "package.zip", sha256 = hash }, "passed");
        await Verify(new { kind = "file", path = "package.zip", sha256 = new string('0', 64) }, "failed");
    }

    [Fact]
    public async Task Infrastructure_health_200_does_not_hide_wrong_deployed_version()
    {
        await using var server = new FixtureHttpServer();
        await Verify(new { kind = "http", url = server.Url("/version"), expectedStatus = 200,
            assertions = new[] { new { path = "/version", op = "equals", expected = "2.0.0" } } }, "failed");
        await Verify(new { kind = "http", url = server.Url("/version"), expectedStatus = 200,
            assertions = new[] { new { path = "/version", op = "equals", expected = "1.0.0" } } }, "passed");
    }

    [Fact]
    public async Task Sqlite_readonly_query_detects_idempotency_invariant_and_rejects_writes()
    {
        var path = Path.Combine(_root, "fixture.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE jobs (job_key TEXT NOT NULL); INSERT INTO jobs VALUES ('same-request');";
            await command.ExecuteNonQueryAsync();
        }
        var probe = new { kind = "sqlite", path = "fixture.db", query = "SELECT count(*) AS count FROM jobs WHERE job_key = $key", parameters = new Dictionary<string, object> { ["$key"] = "same-request" },
            assertions = new[] { new { path = "/rows/0/count", op = "equals", expected = 1 } } };
        await Verify(probe, "passed");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO jobs VALUES ('same-request')"; await command.ExecuteNonQueryAsync();
        }
        await Verify(probe, "failed");
        await Verify(new { kind = "sqlite", path = "fixture.db", query = "DELETE FROM jobs; SELECT 1", assertions = new[] { new { path = "/rowCount", op = "equals", expected = 0 } } }, "not_supported");
        await Verify(new { kind = "sqlite", path = "fixture.db", query = "SELECT count(*) AS count FROM jobs", assertions = new[] { new { path = "/rows/0/count", op = "equals", expected = 2 } } }, "passed");
    }

    [Fact]
    public async Task Sqlite_cpu_bound_query_can_be_interrupted_with_a_real_deadline()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "large.db"), Pooling = false }.ToString()))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE n(value INTEGER); WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM seq WHERE x<500) INSERT INTO n SELECT x FROM seq";
            await command.ExecuteNonQueryAsync();
        }
        var clock = Stopwatch.StartNew();
        await Verify(new { kind = "sqlite", path = "large.db", query = "SELECT sum(a.value*b.value*c.value*d.value) AS total FROM n a,n b,n c,n d", timeoutMs = 100,
            assertions = new[] { new { path = "/rowCount", op = "equals", expected = 1 } } }, "blocked");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Workspace_boundary_empty_claims_and_unsupported_profiles_fail_closed()
    {
        await Verify(new { kind = "file", path = Path.Combine(Path.GetDirectoryName(_root)!, "outside-file.txt"), exists = false }, "blocked");
        await Verify(new { kind = "file", path = "missing.txt" }, "blocked");
        await Verify(new { kind = "file", path = "missing.txt", exists = false }, "passed");
        await Verify(new { kind = "native_ui", passed = true }, "not_supported");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _tool.ExecuteAsync(WireJson.Element(new { kind = "file", path = "missing.txt", exists = false }), Context, cancelled.Token));
    }

    private sealed class FixtureHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _requests = [];
        private readonly Task _loop;
        private int _readyCalls;
        public int Mutations;
        public int ApiCalls;
        public FixtureHttpServer() { _listener.Start(); _loop = Accept(); }
        public string Url(string path) => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + path;
        private async Task Accept()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var request = Serve(client); lock (_requests) _requests.Add(request);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                    var firstLine = await reader.ReadLineAsync(_stop.Token);
                    if (string.IsNullOrEmpty(firstLine)) return;
                    var request = firstLine.Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                    { var split = line.IndexOf(':'); if (split > 0) headers[line[..split]] = line[(split + 1)..].Trim(); }
                    if (headers.TryGetValue("Content-Length", out var length) && int.TryParse(length, out var bodyLength))
                    { var body = new char[bodyLength]; await reader.ReadBlockAsync(body, _stop.Token); }
                    var status = 200; var bodyText = "{}"; var extra = "";
                    switch (request[1])
                    {
                        case "/api": Interlocked.Increment(ref ApiCalls); bodyText = "{\"answer\":42}"; break;
                        case "/wrong": bodyText = "{\"answer\":43}"; break;
                        case "/tenant":
                            if (headers.GetValueOrDefault("Authorization") == "Bearer corpus-fixture") bodyText = "{\"tenant\":\"tenant-a\"}";
                            else { status = 401; bodyText = "{\"error\":\"unauthorized\"}"; }
                            break;
                        case "/ready": status = Interlocked.Increment(ref _readyCalls) < 3 ? 503 : 200; break;
                        case "/never-ready": status = 503; break;
                        case "/mutation": bodyText = "{\"calls\":" + Interlocked.Increment(ref Mutations) + "}"; break;
                        case "/version": bodyText = "{\"version\":\"1.0.0\"}"; break;
                        case "/large": bodyText = new string('x', 1024 * 1024 + 1); break;
                        case "/redirect": status = 302; extra = "Location: " + Url("/api") + "\r\n"; break;
                        default: status = 404; break;
                    }
                    var bytes = Encoding.UTF8.GetBytes(bodyText);
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n{extra}\r\n");
                    await stream.WriteAsync(response, _stop.Token); await stream.WriteAsync(bytes, _stop.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop(); await _loop;
            Task[] pending; lock (_requests) pending = _requests.ToArray();
            await Task.WhenAll(pending); _stop.Dispose();
        }
    }

    public void Dispose()
    {
        var expected = Path.GetFullPath(_root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), expected);
        Assert.StartsWith("jarvis-verification-corpus-", Path.GetFileName(expected));
        Directory.Delete(expected, recursive: true);
    }
}
