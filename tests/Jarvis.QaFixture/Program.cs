using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

if (args.Length < 2) return 2;
var mode = args[0];
var statePath = Path.GetFullPath(args[1]);
var working = Path.GetDirectoryName(statePath)!;
var runtime = Path.Combine(working, ".jarvis-qa");
Directory.CreateDirectory(runtime);
FixtureState State() => JsonSerializer.Deserialize<FixtureState>(File.ReadAllText(statePath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
void Save(FixtureState state) => File.WriteAllText(statePath, JsonSerializer.Serialize(state));
switch (mode)
{
    case "cli":
        Console.WriteLine("CLI_STARTED"); Console.Out.Flush();
        await Task.Delay(500);
        Console.WriteLine("VALUE=" + State().Value);
        return 0;
    case "fix":
        var state = State(); Save(state with { Value = 42, Deduplicate = true });
        await File.AppendAllTextAsync(Path.Combine(runtime, "fix-invocations.txt"), "fix\n");
        Console.WriteLine("FIXED"); return 0;
    case "mutate":
        var old = State(); Save(old with { Value = old.Value + 1 });
        Console.WriteLine("MUTATED"); return 0;
    case "selftest":
        if (args.Length != 3) return 2;
        Console.WriteLine("SELFTEST_STARTED"); Console.Out.Flush(); await Task.Delay(200);
        var observed = State();
        var assertions = new[] { (Name: "value_is_42", Passed: observed.Value == 42), (Name: "queue_is_idempotent", Passed: observed.Deduplicate) };
        var suite = new XElement("testsuite", new XAttribute("name", "real-fixture-tests"), new XAttribute("tests", assertions.Length),
            new XAttribute("failures", assertions.Count(check => !check.Passed)), new XAttribute("errors", 0), new XAttribute("skipped", 0),
            assertions.Select(check => new XElement("testcase", new XAttribute("name", check.Name),
                check.Passed ? null : new XElement("failure", new XAttribute("message", "Observed fixture state violates " + check.Name)))));
        var report = Path.GetFullPath(args[2]); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        new XDocument(suite).Save(report);
        foreach (var check in assertions) Console.WriteLine((check.Passed ? "PASS " : "FAIL ") + check.Name);
        return assertions.All(check => check.Passed) ? 0 : 1;
    case "package":
        var package = Path.Combine(runtime, "fixture.zip");
        if (File.Exists(package)) File.Delete(package);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(typeof(FixtureState).Assembly.Location, "lib/fixture.dll");
            if (State().Deduplicate)
            { using var output = new StreamWriter(archive.CreateEntry("assets/runtime.json").Open()); output.Write("{\"ready\":true}"); }
        }
        Console.WriteLine("PACKAGED"); return 0;
    case "serve":
        if (args.Length != 3 || !int.TryParse(args[2], out var port)) return 2;
        var database = Path.Combine(runtime, "jobs.db");
        using (var connection = Open(database))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE IF EXISTS jobs; CREATE TABLE jobs (request_key TEXT NOT NULL)";
            command.ExecuteNonQuery();
        }
        // Actual loopback HTTP without HTTP.sys URLACL registration or user-machine setup.
        var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        Console.WriteLine("SERVICE_READY " + port); Console.Out.Flush();
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync();
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                var first = await reader.ReadLineAsync(); if (first is null) continue;
                var parts = first.Split(' '); if (parts.Length < 2) continue;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync() is { Length: > 0 } header)
                { var colon = header.IndexOf(':'); if (colon > 0) headers[header[..colon]] = header[(colon + 1)..].Trim(); }
                var body = "";
                if (headers.TryGetValue("Content-Length", out var rawLength) && int.TryParse(rawLength, out var length))
                {
                    if (length > 65536) continue;
                    var chars = new char[length]; var read = await reader.ReadBlockAsync(chars); body = new string(chars, 0, read);
                }
                var status = 200; object result;
                switch (parts[1])
                {
                    case "/health": result = new { ready = true, version = "fixture-1" }; break;
                    case "/value": result = new { value = State().Value }; break;
                    case "/auth":
                        if (headers.GetValueOrDefault("Authorization") == "Bearer fixture-authorized") result = new { tenant = "allowed" };
                        else { status = 401; result = new { error = "unauthorized" }; }
                        break;
                    case "/enqueue" when parts[0] == "POST":
                        using (var payload = JsonDocument.Parse(body))
                        using (var connection = Open(database))
                        {
                            var key = payload.RootElement.GetProperty("key").GetString()!;
                            using var transaction = connection.BeginTransaction();
                            using var count = connection.CreateCommand(); count.Transaction = transaction;
                            count.CommandText = "SELECT count(*) FROM jobs WHERE request_key=$key"; count.Parameters.AddWithValue("$key", key);
                            var existing = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
                            if (!State().Deduplicate || existing == 0)
                            {
                                using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                                insert.CommandText = "INSERT INTO jobs VALUES ($key)"; insert.Parameters.AddWithValue("$key", key); insert.ExecuteNonQuery();
                            }
                            var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
                            transaction.Commit(); result = new { accepted = true, total };
                        }
                        break;
                    default: status = 404; result = new { error = "not_found" }; break;
                }
                var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
                var prefix = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(prefix); await stream.WriteAsync(bytes);
                Console.WriteLine("REQUEST " + parts[0] + " " + parts[1] + " " + status); Console.Out.Flush();
            }
            catch (IOException) { }
        }
    default: return 2;
}

static SqliteConnection Open(string path)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    connection.Open(); return connection;
}
internal sealed record FixtureState(int Value, bool Deduplicate);
