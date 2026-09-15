using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// The strings the diagnostic-report modal renders, in the reference's own
/// order (desktop 1.40609.1.0, <c>ion-dist/assets/v1/cd53438bb-zMSmatZM.js</c>,
/// whose <c>k</c> is the five-row contents list and whose <c>j</c> is the popup).
/// </summary>
public static class DiagnosticReportText
{
    /// <summary>The Help ▸ Troubleshooting row, and the title while packaging.</summary>
    public const string MenuLabel = "Generate Diagnostic Report";

    /// <summary>The title once there is a bundle to act on.</summary>
    public const string Title = "Diagnostic Report";

    /// <summary>The title after Export to file succeeded.</summary>
    public const string ExportedTitle = "Report exported";

    public const string Collecting = "Collecting diagnostics…";
    public const string CollectingHint = "This usually takes a few seconds.";
    public const string Contains = "This report contains:";

    /// <summary>The reference's <c>k</c>, in its order.</summary>
    public const string SystemRow = "App version, OS, and system info";
    public const string ConfigRow = "Your managed configuration (secrets redacted)";
    public const string ReachabilityRow = "Network reachability for inference and MCP hosts";
    public const string LogsRow = "Recent error and warning log lines";
    public const string CrashRow = "Crash report filenames";

    public const string ScrubNote =
        "Log lines are scrubbed for file paths, email addresses, IP addresses, and API tokens before packaging. Message content is never included.";

    public const string PreviewContents = "Preview contents";
    public const string ExportToFile = "Export to file";
    public const string ShowInExplorer = "Show in Explorer";
    public const string Done = "Done";

    /// <summary>The reference's <c>/teGfAcxuY</c>, whose one value is the saved path.</summary>
    public static string SavedTo(string path) => $"Saved to {path}";

    /// <summary>The five rows the modal lists, in the reference's order.</summary>
    public static IReadOnlyList<string> ContentRows =>
        [SystemRow, ConfigRow, ReachabilityRow, LogsRow, CrashRow];

    /// <summary>
    /// The reference's byte formatter for the preview's size (its
    /// <c>c82ba4ebe</c>): base 1000, <c>narrow</c> unit display, and no decimal
    /// place below a kilobyte.
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB"];
        var exponent = bytes < 1000 ? 0 : Math.Min(units.Length - 1, (int)Math.Floor(Math.Log(bytes) / Math.Log(1000)));
        var value = bytes / Math.Pow(1000, exponent);
        var text = exponent == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
        return text + units[exponent];
    }
}

/// <summary>What the packaging pass produced.</summary>
/// <param name="BundleId">The report's own id; the file name carries its first eight characters.</param>
/// <param name="Zip">The archive bytes.</param>
/// <param name="PreviewLines">The first lines of the collected sections, for the disclosure.</param>
/// <param name="Sections">Each section's id and scrubbed text, in collection order.</param>
public sealed record DiagnosticBundle(
    string BundleId,
    byte[] Zip,
    IReadOnlyList<string> PreviewLines,
    IReadOnlyList<(string Id, string Text)> Sections)
{
    public long SizeBytes => Zip.LongLength;
}

/// <summary>What a section needs to describe this installation.</summary>
/// <param name="AppVersion">The running build.</param>
/// <param name="InstallId">The installation id Help ▸ Copy Installation ID copies.</param>
/// <param name="ProfileRoot">Where this profile's data lives.</param>
/// <param name="LogFile">The diagnostic log this session writes to, if any.</param>
/// <param name="SettingsFile">The settings file whose secrets are redacted.</param>
/// <param name="Hosts">The inference and MCP hosts to probe.</param>
/// <param name="McpServers">Connected server name and tool count, as the manager last reported.</param>
/// <param name="ModelCalls">The recent provider calls the Debug pane holds.</param>
public sealed record DiagnosticContext(
    string AppVersion,
    string InstallId,
    string ProfileRoot,
    string? LogFile,
    string SettingsFile,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<(string Server, int Tools)> McpServers,
    IReadOnlyList<ModelTrafficSnapshot> ModelCalls);

/// <summary>
/// The diagnostic report the reference's Help ▸ Troubleshooting row generates
/// (desktop 1.40609.1.0: the collector <c>EVn</c> and its section table
/// <c>CVn</c> in <c>app.asar</c>'s <c>index.chunk-DnlgCaT3.js</c>, the modal in
/// the ion-dist chunk above).
///
/// The reference collects eighteen sections and shows five bullets describing
/// them; those five bullets are the contract with the user, so this port
/// collects exactly what they promise, from this app's own sources. Its
/// <c>Send to Anthropic</c> half is not built — there is no endpoint here to
/// send to — which is a state the reference itself renders whenever
/// <c>showSendUi</c> is false.
/// </summary>
public static partial class DiagnosticReport
{
    /// <summary>The reference's <c>wVn</c>: how many lines the preview disclosure holds.</summary>
    public const int PreviewLineCap = 40;

    /// <summary>The reference's section ids, in the order the bundle stores them.</summary>
    public const string SystemSection = "system-info";
    public const string ConfigSection = "managed-config";
    public const string ReachabilitySection = "reachability";
    public const string LogsSection = "main-log";
    public const string CrashSection = "dmp-names";

    /// <summary>
    /// The reference's <c>OVn</c>: <c>claude-diagnostic-{first 8 of the bundle
    /// id}-{yyyyMMdd-HH}.zip</c>, with this product's own name in front.
    /// </summary>
    public static string FileName(string bundleId, DateTimeOffset now) =>
        $"jarvis-diagnostic-{bundleId[..Math.Min(8, bundleId.Length)]}-" +
        now.UtcDateTime.ToString("yyyyMMdd-HH", CultureInfo.InvariantCulture) + ".zip";

    /// <summary>Collects, scrubs and zips the report.</summary>
    public static async Task<DiagnosticBundle> BuildAsync(
        DiagnosticContext context,
        HttpClient http,
        Action<string>? onStep = null,
        CancellationToken cancellationToken = default)
    {
        var bundleId = Guid.NewGuid().ToString();
        var scrub = new LineScrubber();
        List<(string Id, string Text)> sections = [];

        onStep?.Invoke(DiagnosticReportText.SystemRow);
        sections.Add((SystemSection, scrub.Apply(SystemInfo(context))));

        onStep?.Invoke(DiagnosticReportText.ConfigRow);
        sections.Add((ConfigSection, scrub.Apply(Configuration(context))));

        onStep?.Invoke(DiagnosticReportText.ReachabilityRow);
        sections.Add((ReachabilitySection,
            scrub.Apply(await ReachabilityAsync(context, http, cancellationToken))));

        onStep?.Invoke(DiagnosticReportText.LogsRow);
        sections.Add((LogsSection, scrub.Apply(Logs(context))));

        onStep?.Invoke(DiagnosticReportText.CrashRow);
        sections.Add((CrashSection, scrub.Apply(CrashDumps())));

        var preview = new List<string>();
        foreach (var (id, text) in sections)
        {
            foreach (var line in text.Split('\n'))
            {
                if (preview.Count >= PreviewLineCap)
                {
                    break;
                }

                if (line.Length > 0)
                {
                    preview.Add($"[{id}] {line}");
                }
            }
        }

        var manifest = JsonSerializer.Serialize(
            new
            {
                bundleId,
                installId = context.InstallId,
                appVersion = context.AppVersion,
                platform = "win32",
                arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                createdAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                sections = sections.Select(static s => s.Id).ToArray(),
            },
            new JsonSerializerOptions { WriteIndented = true });

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (id, text) in sections)
            {
                await WriteEntryAsync(archive, id + ".txt", text, cancellationToken);
            }

            await WriteEntryAsync(archive, "manifest.json", manifest, cancellationToken);
        }

        return new DiagnosticBundle(bundleId, buffer.ToArray(), preview, sections);
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    // ---- the five sections ----

    internal static string SystemInfo(DiagnosticContext context)
    {
        var lines = new List<string>
        {
            $"App version:     {context.AppVersion}",
            $"Install id:      {context.InstallId}",
            $"OS:              {Environment.OSVersion.VersionString}",
            $"Runtime:         {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}",
            $"Architecture:    {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
            $"Processors:      {Environment.ProcessorCount}",
            $"Working set:     {DiagnosticReportText.FormatBytes(Environment.WorkingSet)}",
            $"Render tier:     {RenderTier()}",
            $"Browser engine:  {EngineVersion()}",
            $"Profile:         {context.ProfileRoot}",
        };

        return string.Join('\n', lines);
    }

    private static string RenderTier()
    {
        try
        {
            var tier = System.Windows.Media.RenderCapability.Tier >> 16;
            return tier switch
            {
                0 => "0 (software rendering)",
                1 => "1 (partial hardware acceleration)",
                _ => $"{tier} (hardware accelerated)",
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or TypeInitializationException)
        {
            return "(unavailable)";
        }
    }

    /// <summary>
    /// The engine the app renders with. It is pinned rather than discovered, so
    /// what matters in a report is whether the pinned build is on disk.
    /// </summary>
    private static string EngineVersion() =>
        new ElectronRuntime().IsInstalled
            ? $"Electron {ElectronRuntime.Version} (Chromium {ElectronRuntime.ChromiumVersion})"
            : $"Electron {ElectronRuntime.Version} (not downloaded yet)";

    /// <summary>
    /// The settings file with every credential-shaped value removed. The
    /// reference redacts secrets and keeps endpoint hostnames, which is what
    /// makes the section worth reading.
    /// </summary>
    internal static string Configuration(DiagnosticContext context)
    {
        string json;
        try
        {
            json = File.ReadAllText(context.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(unreadable: {ex.Message})";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                buffer,
                new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                Redact(document.RootElement, writer, null);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException ex)
        {
            return $"(unparseable: {ex.Message})";
        }
    }

    [GeneratedRegex(
        "key|secret|token|password|credential|cookie",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();

    private static void Redact(JsonElement element, Utf8JsonWriter writer, string? name)
    {
        if (name is not null && SecretName().IsMatch(name) && element.ValueKind is not
                (JsonValueKind.Object or JsonValueKind.Array))
        {
            writer.WriteStringValue("<redacted>");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Redact(property.Value, writer, property.Name);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Redact(item, writer, name);
                }

                writer.WriteEndArray();
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    /// <summary>
    /// The reference's <c>bVn</c> rendering: one line per host, a tick with the
    /// round-trip time when it answered and a cross with the reason when it did
    /// not.
    /// </summary>
    internal static async Task<string> ReachabilityAsync(
        DiagnosticContext context, HttpClient http, CancellationToken cancellationToken)
    {
        if (context.Hosts.Count == 0)
        {
            return "(no inference or MCP hosts are configured)";
        }

        // The reference gives the whole probe one 15s budget and runs the hosts
        // together (its LBn under a single AbortController), which is what keeps
        // a dead endpoint from holding the report open host by host.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ProbeBudget);
        var hosts = context.Hosts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
        var probes = hosts.Select(host => ProbeAsync(http, host, budget.Token)).ToList();
        var lines = new List<string>(await Task.WhenAll(probes));

        if (context.McpServers.Count > 0)
        {
            lines.Add("");
            lines.Add("MCP servers:");
            foreach (var (server, tools) in context.McpServers)
            {
                lines.Add($"  {server}: {tools} tool(s)");
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>How long the whole reachability probe may take, the reference's own 15s.</summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(15);

    private static async Task<string> ProbeAsync(HttpClient http, string host, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, $"https://{host}/");
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return $"✓ {host} ({started.ElapsedMilliseconds}ms)";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                       InvalidOperationException or UriFormatException)
        {
            return $"✗ {host} {(ex is TaskCanceledException ? "timeout" : ex.Message)}";
        }
    }

    /// <summary>
    /// The log half: the tail of this session's diagnostic log, then the calls
    /// the Debug pane holds — status, model and duration only, never a body,
    /// because those carry the whole conversation.
    /// </summary>
    internal static string Logs(DiagnosticContext context)
    {
        var text = new StringBuilder();
        text.AppendLine($"== {context.LogFile ?? "(no log file)"} ==");
        if (context.LogFile is { } path && File.Exists(path))
        {
            try
            {
                var lines = File.ReadLines(path).ToList();
                foreach (var line in lines.Skip(Math.Max(0, lines.Count - 200)))
                {
                    text.AppendLine(line);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                text.AppendLine($"(unreadable: {ex.Message})");
            }
        }
        else
        {
            text.AppendLine("(no log file exists yet)");
        }

        text.AppendLine();
        text.AppendLine("== recent model calls ==");
        if (context.ModelCalls.Count == 0)
        {
            text.AppendLine("(none recorded)");
        }
        else
        {
            foreach (var call in context.ModelCalls)
            {
                var status = call.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? call.State.ToString();
                var elapsed = call.Elapsed is { } e ? $"{e.TotalMilliseconds:F0}ms" : "—";
                text.AppendLine(
                    $"{call.StartedAt:HH:mm:ss}  {call.Method} {call.Host}{call.Path}  " +
                    $"{status}  {call.ModelId ?? "?"}  {elapsed}{(call.Error is { } err ? "  " + err : "")}");
            }
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// The reference's <c>xVn</c>: the timestamp and name of each recent crash
    /// dump, or <c>(none)</c>. Windows writes them under LocalAppData\CrashDumps.
    /// </summary>
    internal static string CrashDumps()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
        try
        {
            if (!Directory.Exists(directory))
            {
                return "(none)";
            }

            var files = new DirectoryInfo(directory).GetFiles("*.dmp")
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .Take(20)
                .Select(static f => $"{f.LastWriteTimeUtc:o}  {f.Name}")
                .ToList();
            return files.Count == 0 ? "(none)" : string.Join('\n', files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(unreadable: {ex.Message})";
        }
    }
}

/// <summary>
/// The reference's line scrubber (desktop 1.40609.1.0, its <c>Vu</c> composed of
/// <c>Bu</c>, <c>Hu</c>, <c>Xje</c>, <c>eMe</c> and <c>zu</c>): user info in a
/// URL, then paths, then email addresses, then IP addresses, then the token
/// table. What is carried is the placeholder vocabulary and the patterns the
/// modal's own note promises — file paths, email addresses, IP addresses and API
/// tokens; the reference's remaining path rewrites (its nix profile, volume and
/// partial-download forms) name directories this platform does not have.
/// </summary>
public sealed partial class LineScrubber
{
    private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IPv4();

    [GeneratedRegex(@"\b(?:[A-Fa-f0-9]{1,4}:){2,7}(?::?[A-Fa-f0-9]{1,4}){1,7}\b")]
    private static partial Regex IPv6();

    [GeneratedRegex(@"^\d{1,2}:\d{2}:\d{2}$")]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"://(?!\[)[^\s/:]*(?::[^\s/]{0,8192})?@")]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(@"(?<![/\\])([/\\]+(?:Users|home)[/\\]+)[^/\\\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserDirectory();

    [GeneratedRegex(@"\b([A-Za-z]):([\\/])")]
    private static partial Regex DriveLetter();

    [GeneratedRegex(@"\\\\[^\\]+\\[^\\\s'"",:()]+")]
    private static partial Regex UncPath();

    private static readonly (Regex Pattern, string Replacement)[] Tokens =
    [
        (new Regex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase), "Bearer <token>"),
        (new Regex(@"(:\s*)Basic\s+[A-Za-z0-9+/=]{8,}", RegexOptions.IgnoreCase), "$1Basic <token>"),
        (new Regex(@"\bsk-ant-[A-Za-z0-9._-]{8,}"), "<token>"),
        (new Regex(@"\b[sr]k[-_][A-Za-z0-9_-]{20,}"), "<token>"),
        (new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.IgnoreCase), "<token>"),
        (new Regex(@"\bASIA[0-9A-Z]{16}\b", RegexOptions.IgnoreCase), "<token>"),
        (new Regex(@"\bAIza[0-9A-Za-z_-]{35}(?![0-9A-Za-z_-])", RegexOptions.IgnoreCase), "<token>"),
        (new Regex(@"\bgh[opusr]_[A-Za-z0-9]{36,}"), "<token>"),
        (new Regex(@"\bgithub_pat_[A-Za-z0-9_]{22,}"), "<token>"),
        (new Regex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}"), "<token>"),
        (new Regex(@"\bey[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"), "<jwt>"),
    ];

    /// <summary>Scrubs every line of a section.</summary>
    public string Apply(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Line));

    /// <summary>The reference's <c>Vu</c>, in its order.</summary>
    public string Line(string line)
    {
        var text = UrlUserInfo().Replace(line, "://<userinfo>@");
        text = Paths(text);
        text = Email().Replace(text, "<email>");
        text = IPv4().Replace(text, "<ip>");
        text = IPv6().Replace(text, match => ClockTime().IsMatch(match.Value) ? match.Value : "<ip>");
        foreach (var (pattern, replacement) in Tokens)
        {
            text = pattern.Replace(text, replacement);
        }

        return text;
    }

    private string Paths(string line)
    {
        var text = _home.Length > 0
            ? line.Replace(_home, "<home>", StringComparison.OrdinalIgnoreCase)
            : line;
        text = UserDirectory().Replace(text, "$1<user>");
        text = UncPath().Replace(text, "<unc>");
        return DriveLetter().Replace(text, "<drv>:$2");
    }
}
