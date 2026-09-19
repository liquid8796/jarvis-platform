using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Protocol;
using Microsoft.Data.Sqlite;

namespace Jarvis.Agent.Core.DeveloperTools;

/// <summary>Measured, bounded acceptance probes. The caller's guarded tool dispatcher remains authoritative.</summary>
public sealed class DeveloperVerifyTool : IAgentTool
{
    private const int MaxInputBytes = 4 * 1024 * 1024;
    private const int MaxHttpBytes = 1024 * 1024;
    private readonly string _artifactRoot;
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(1)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public DeveloperVerifyTool(string? artifactRoot = null) => _artifactRoot = Path.GetFullPath(artifactRoot ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAgent", "qa-artifacts"));

    public ToolDescriptor Descriptor { get; } = new("developer.verify", "developer__verify", "developer",
        "Run measured HTTP, JSON/schema/numeric, file/archive, or readonly SQLite acceptance assertions. Workspace file boundaries, bounded I/O and explicit outcomes apply. HTTP defaults to loopback; remote HTTPS requires allowRemote:true. Saves a host-authored report and hash; unsupported checks never pass. HTTP mutations and artifact writes require normal tool permissions.",
        DeveloperVerifySchema.Create(), ReadOnly: false, Sensitive: true);

    public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.SessionCancellation.ThrowIfCancellationRequested();
        var runId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var assertions = new List<VerificationAssertion>();
        var observations = new Dictionary<string, object?>(StringComparer.Ordinal);
        var status = "blocked";
        var kind = "unknown";
        string? error = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.SessionCancellation);
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(arguments.GetRawText()) > 65536)
                throw new ArgumentException("Verification request must be an object no larger than 64 KiB.");
            kind = Text(arguments, "kind", 32);
            var bound = Integer(arguments, "timeoutMs", 10000, 100, 60000);
            timeout.CancelAfter(bound);
            var boundary = new WorkspaceBoundary(context.Workspace, context.AdditionalDirectories);
            switch (kind)
            {
                case "http":
                    Keys(arguments, "kind", "url", "method", "allowRemote", "headers", "body", "expectedStatus", "timeoutMs", "readiness", "assertions", "schema");
                    await VerifyHttp(arguments, assertions, observations, timeout.Token).ConfigureAwait(false);
                    break;
                case "json":
                    Keys(arguments, "kind", "path", "assertions", "schema", "timeoutMs");
                    var jsonPath = boundary.Resolve(Text(arguments, "path", 2048));
                    var jsonBytes = await ReadBoundedFile(jsonPath, MaxInputBytes, timeout.Token).ConfigureAwait(false);
                    observations["path"] = jsonPath; observations["inputSha256"] = Hash(jsonBytes);
                    using (var document = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions { MaxDepth = 64 }))
                        VerificationAssertions.Evaluate(document.RootElement, arguments, assertions);
                    break;
                case "file":
                    Keys(arguments, "kind", "path", "exists", "sha256", "textEquals", "textContains", "archiveMembers", "timeoutMs");
                    await VerifyFile(arguments, boundary, assertions, observations, timeout.Token).ConfigureAwait(false);
                    break;
                case "sqlite":
                    Keys(arguments, "kind", "path", "query", "parameters", "assertions", "schema", "timeoutMs");
                    await VerifySqlite(arguments, boundary, assertions, observations, timeout.Token).ConfigureAwait(false);
                    break;
                default: throw new NotSupportedException("Unsupported verification kind: " + kind);
            }
            if (assertions.Count == 0) throw new ArgumentException("Verification needs at least one measured assertion.");
            status = assertions.All(item => item.Passed) ? "passed" : "failed";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !context.SessionCancellation.IsCancellationRequested)
        { status = "blocked"; error = "Verification timed out; no success is inferred from partial evidence."; }
        catch (NotSupportedException ex) { status = "not_supported"; error = ex.Message; }
        catch (JsonException ex) { status = "failed"; error = "Observed JSON is invalid: " + ex.Message; }
        catch (FileNotFoundException ex) { status = "failed"; error = ex.Message; }
        catch (SqliteException ex) { status = "blocked"; error = "SQLite probe could not complete: " + ex.Message; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or HttpRequestException or FormatException)
        { status = "blocked"; error = ex.Message; }

        cancellationToken.ThrowIfCancellationRequested();
        context.SessionCancellation.ThrowIfCancellationRequested();
        var report = new
        {
            schemaVersion = 1, runId, kind, status, passed = status == "passed", startedAt,
            finishedAt = DateTimeOffset.UtcNow, durationMs = clock.ElapsedMilliseconds,
            assertionCount = assertions.Count, assertions, observations, error,
            callId = context.CallId, scopeId = context.IsolationScopeId
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, WireJson.Options);
        if (bytes.Length > 256 * 1024) return ToolReply.Error("Verification report exceeded its bounded artifact size; no pass was recorded.");
        var scope = Hash(Encoding.UTF8.GetBytes((context.OwnerId ?? "") + "\n" + (context.AgentDeviceId ?? "") + "\n" + context.IsolationScopeId));
        var directory = Path.Combine(_artifactRoot, scope, runId);
        RejectLinks(directory); Directory.CreateDirectory(directory); RejectLinks(directory);
        var reportPath = Path.Combine(directory, "verification.json");
        await File.WriteAllBytesAsync(reportPath, bytes, cancellationToken).ConfigureAwait(false);
        context.SessionCancellation.ThrowIfCancellationRequested();
        var output = JsonSerializer.Serialize(new
        {
            report.schemaVersion, report.runId, report.kind, report.status, report.passed, report.startedAt,
            report.finishedAt, report.durationMs, report.assertionCount, report.assertions, report.observations,
            report.error, report.callId, report.scopeId, reportPath, reportSha256 = Hash(bytes)
        }, WireJson.Options);
        return new ToolReply(output, IsError: status != "passed");
    }

    private static async Task VerifyHttp(JsonElement args, List<VerificationAssertion> results, Dictionary<string, object?> observations, CancellationToken ct)
    {
        var allowRemote = args.TryGetProperty("allowRemote", out var remote) && remote.GetBoolean();
        var uri = Endpoint(Text(args, "url", 4096), allowRemote);
        var method = args.TryGetProperty("method", out _) ? Text(args, "method", 16) : "GET";
        if (method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE")) throw new NotSupportedException("Unsupported HTTP method.");
        var expectedStatus = Integer(args, "expectedStatus", null, 100, 599);
        observations["endpoint"] = uri.GetLeftPart(UriPartial.Path); observations["method"] = method;
        if (args.TryGetProperty("readiness", out var readiness))
        {
            Keys(readiness, "url", "expectedStatus", "timeoutMs");
            var readyUri = readiness.TryGetProperty("url", out _) ? Endpoint(Text(readiness, "url", 4096), allowRemote) : uri;
            if (readyUri.GetLeftPart(UriPartial.Authority) != uri.GetLeftPart(UriPartial.Authority))
                throw new ArgumentException("Readiness endpoint must use the same origin as the actual request.");
            var expectedReady = Integer(readiness, "expectedStatus", 200, 100, 599);
            using var readyStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readyStop.CancelAfter(Integer(readiness, "timeoutMs", 5000, 100, 30000));
            var attempts = 0;
            while (true)
            {
                readyStop.Token.ThrowIfCancellationRequested(); attempts++;
                try
                {
                    using var probe = Request(args, readyUri, "GET", includeBody: false);
                    using var response = await Http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, readyStop.Token).ConfigureAwait(false);
                    if ((int)response.StatusCode == expectedReady) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, readyStop.Token).ConfigureAwait(false);
            }
            observations["readinessAttempts"] = attempts;
            results.Add(new("http:readiness", true, expectedReady, expectedReady));
        }
        using var request = Request(args, uri, method, includeBody: true);
        using var reply = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var statusCode = (int)reply.StatusCode;
        results.Add(new("http:status", statusCode == expectedStatus, expectedStatus, statusCode));
        observations["statusCode"] = statusCode;
        await using var stream = await reply.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var body = await ReadBounded(stream, MaxHttpBytes, ct).ConfigureAwait(false);
        observations["responseBytes"] = body.Length; observations["responseSha256"] = Hash(body);
        if (args.TryGetProperty("assertions", out _) || args.TryGetProperty("schema", out _))
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
            VerificationAssertions.Evaluate(document.RootElement, args, results);
        }
    }

    private static Uri Endpoint(string url, bool allowRemote)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("HTTP probe requires an HTTP(S) endpoint without credentials or fragment.");
        if (!uri.IsLoopback && (!allowRemote || uri.Scheme != "https"))
            throw new UnauthorizedAccessException("Non-loopback probes require explicit allowRemote:true and HTTPS.");
        return uri;
    }

    private static HttpRequestMessage Request(JsonElement args, Uri uri, string method, bool includeBody)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        if (args.TryGetProperty("headers", out var headers))
        {
            if (headers.ValueKind != JsonValueKind.Object || headers.EnumerateObject().Count() > 16) throw new ArgumentException("At most 16 HTTP headers are allowed.");
            foreach (var header in headers.EnumerateObject())
            {
                if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    header.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Transport-controlled HTTP header is not allowed.");
                if (header.Value.ValueKind != JsonValueKind.String || header.Value.GetString()!.Length > 4096 || header.Value.GetString()!.IndexOfAny(['\r', '\n', '\0']) >= 0 || !request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString()))
                    throw new ArgumentException("Invalid request header.");
            }
        }
        if (includeBody && args.TryGetProperty("body", out var body))
        {
            if (method is "GET" or "HEAD") throw new ArgumentException("GET/HEAD verification requests cannot carry a body.");
            request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }
        return request;
    }

    private static async Task VerifyFile(JsonElement args, WorkspaceBoundary boundary, List<VerificationAssertion> results, Dictionary<string, object?> observations, CancellationToken ct)
    {
        var path = boundary.Resolve(Text(args, "path", 2048)); observations["path"] = path;
        var exists = File.Exists(path);
        if (args.TryGetProperty("exists", out var expectedExists)) results.Add(new("file:exists", exists == expectedExists.GetBoolean(), expectedExists.GetBoolean(), exists));
        var needsContents = new[] { "sha256", "textEquals", "textContains", "archiveMembers" }.Any(key => args.TryGetProperty(key, out _));
        if (!needsContents) return;
        if (!exists) { results.Add(new("file:required", false, true, false, "File was not found.")); return; }
        if (new FileInfo(path).Length > 128 * 1024 * 1024) throw new NotSupportedException("File probe exceeds 128 MiB.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var fileHash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();
        observations["inputBytes"] = file.Length; observations["inputSha256"] = fileHash;
        if (args.TryGetProperty("sha256", out var hash))
        {
            var expected = hash.GetString() ?? "";
            if (!Regex.IsMatch(expected, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant)) throw new ArgumentException("sha256 requires 64 hexadecimal characters.");
            results.Add(new("file:sha256", fileHash.Equals(expected, StringComparison.OrdinalIgnoreCase), expected.ToLowerInvariant(), fileHash));
        }
        foreach (var field in new[] { "textEquals", "textContains" })
            if (args.TryGetProperty(field, out var expectation))
            {
                var expected = Text(args, field, 32768, allowEmpty: field == "textEquals");
                file.Position = 0;
                var bytes = await ReadBounded(file, MaxInputBytes, ct).ConfigureAwait(false);
                var actual = new UTF8Encoding(false, true).GetString(bytes);
                var passed = field == "textEquals" ? actual == expected : actual.Contains(expected, StringComparison.Ordinal);
                results.Add(new("file:" + field, passed, expected.Length <= 2048 ? expected : expected[..2048], actual.Length <= 2048 ? actual : actual[..2048] + " [truncated]"));
            }
        if (args.TryGetProperty("archiveMembers", out var members))
        {
            if (members.ValueKind != JsonValueKind.Array || members.GetArrayLength() is < 1 or > 128) throw new ArgumentException("archiveMembers requires 1-128 member names.");
            file.Position = 0;
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > 10000) throw new NotSupportedException("Archive entry count exceeds 10000.");
            var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            observations["archiveEntryCount"] = names.Count;
            foreach (var member in members.EnumerateArray())
            {
                var name = member.GetString();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 512) throw new ArgumentException("Invalid archive member name.");
                results.Add(new("archive:member:" + name, names.Contains(name), true, names.Contains(name)));
            }
        }
    }

    private static async Task VerifySqlite(JsonElement args, WorkspaceBoundary boundary, List<VerificationAssertion> results, Dictionary<string, object?> observations, CancellationToken ct)
    {
        var path = boundary.Resolve(Text(args, "path", 2048));
        if (!File.Exists(path)) throw new FileNotFoundException("SQLite fixture was not found.", path);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) _ = boundary.Resolve(path + suffix);
        if (new FileInfo(path).Length > 128 * 1024 * 1024) throw new NotSupportedException("SQLite fixture exceeds 128 MiB.");
        var query = Text(args, "query", 8192).Trim();
        if (!Regex.IsMatch(query, "\\ASELECT\\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) || query.Contains(';') || query.Contains("--", StringComparison.Ordinal) || query.Contains("/*", StringComparison.Ordinal))
            throw new NotSupportedException("SQLite verification accepts one SELECT without comments or statement separators.");
        observations["path"] = path; observations["querySha256"] = Hash(Encoding.UTF8.GetBytes(query));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        connection.EnableExtensions(false);
        using (var guard = connection.CreateCommand()) { guard.CommandText = "PRAGMA query_only=ON"; await guard.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        using var command = connection.CreateCommand(); command.CommandText = query; command.CommandTimeout = 1;
        if (args.TryGetProperty("parameters", out var parameters))
        {
            if (parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Count() > 32) throw new ArgumentException("SQLite parameters require an object with at most 32 values.");
            foreach (var parameter in parameters.EnumerateObject())
            {
                if (!Regex.IsMatch(parameter.Name, "\\A[$@:]?[A-Za-z_][A-Za-z0-9_]{0,63}\\z", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid SQLite parameter name.");
                object value = parameter.Value.ValueKind switch
                {
                    JsonValueKind.Null => DBNull.Value, JsonValueKind.String => parameter.Value.GetString()!,
                    JsonValueKind.True => 1, JsonValueKind.False => 0,
                    JsonValueKind.Number => parameter.Value.TryGetInt64(out var integer) ? integer : VerificationAssertions.Number(parameter.Value),
                    _ => throw new ArgumentException("SQLite parameters must be scalar values.")
                };
                command.Parameters.AddWithValue(parameter.Name, value);
            }
        }
        using var interrupt = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        var rows = await Task.Run(() =>
        {
            using var reader = command.ExecuteReader();
            if (reader.FieldCount > 64) throw new NotSupportedException("SQLite probe exceeds 64 columns.");
            var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new ArgumentException("SQLite result columns must have unique names; use aliases.");
            var data = new List<Dictionary<string, object?>>(); var bytes = 0;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (data.Count >= 1000) throw new NotSupportedException("SQLite probe exceeds 1000 rows; aggregate or filter in SELECT.");
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    object? value = reader.IsDBNull(index) ? null : reader.GetValue(index);
                    if (value is byte[]) throw new NotSupportedException("SQLite BLOB assertions are not supported; query a bounded representation explicitly.");
                    row[names[index]] = value;
                }
                bytes += JsonSerializer.SerializeToUtf8Bytes(row, WireJson.Options).Length;
                if (bytes > MaxHttpBytes) throw new NotSupportedException("SQLite result exceeds 1 MiB.");
                data.Add(row);
            }
            return data;
        }, ct).ConfigureAwait(false);
        observations["rowCount"] = rows.Count;
        VerificationAssertions.Evaluate(WireJson.Element(new { rowCount = rows.Count, rows }), args, results);
    }

    internal static void Keys(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException("Unknown or duplicate verification field: " + property.Name);
    }
    internal static string Text(JsonElement value, string name, int max, bool allowEmpty = false)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || text.Length > max || (!allowEmpty && string.IsNullOrWhiteSpace(text)))
            throw new ArgumentException("Invalid or missing " + name + ".");
        return text;
    }
    private static int Integer(JsonElement value, string name, int? fallback, int min, int max)
    {
        var number = fallback ?? 0;
        if (value.TryGetProperty(name, out var item))
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out number)) throw new ArgumentException(name + " must be an integer.");
        }
        else if (fallback is null) throw new ArgumentException(name + " is required.");
        if (number < min || number > max) throw new ArgumentException(name + " is outside supported bounds.");
        return number;
    }
    private static async Task<byte[]> ReadBoundedFile(string path, int maximum, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadBounded(stream, maximum, ct).ConfigureAwait(false);
    }
    private static async Task<byte[]> ReadBounded(Stream stream, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false); if (count == 0) break;
            if (output.Length + count > maximum) throw new NotSupportedException("Observed content exceeds the bounded probe size; no truncated-content pass is allowed.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static void RejectLinks(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Verification artifact path contains a symbolic link or junction.");
    }
}
