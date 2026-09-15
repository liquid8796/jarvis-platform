using System.IO;
using System.IO.Compression;
using System.Net.Http;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The diagnostic report, against the reference desktop app 1.40609.1.0: its
/// five contents rows, its size formatter, its file name, its forty-line
/// preview, and the scrubbing its own note promises.
/// </summary>
public sealed class DiagnosticReportTests
{
    private static DiagnosticContext Context(string root) => new(
        "1.2.3",
        "install-id",
        root,
        null,
        Path.Combine(root, "settings.json"),
        [],
        [],
        []);

    [Fact]
    public void The_modal_lists_the_references_five_rows_in_its_order() =>
        Assert.Equal(
            [
                "App version, OS, and system info",
                "Your managed configuration (secrets redacted)",
                "Network reachability for inference and MCP hosts",
                "Recent error and warning log lines",
                "Crash report filenames",
            ],
            DiagnosticReportText.ContentRows);

    [Theory]
    [InlineData(0, "0B")]
    [InlineData(999, "999B")]
    [InlineData(1000, "1kB")]
    [InlineData(1234, "1.2kB")]
    [InlineData(1_500_000, "1.5MB")]
    [InlineData(2_000_000_000, "2GB")]
    public void The_preview_size_is_the_references_formatter(long bytes, string expected) =>
        Assert.Equal(expected, DiagnosticReportText.FormatBytes(bytes));

    [Fact]
    public void The_file_name_carries_eight_characters_of_the_bundle_id_and_the_hour() =>
        Assert.Equal(
            "jarvis-diagnostic-0a1b2c3d-20260902-13.zip",
            DiagnosticReport.FileName(
                "0a1b2c3d-4e5f-6789-abcd-ef0123456789",
                new DateTimeOffset(2026, 9, 2, 13, 45, 0, TimeSpan.Zero)));

    [Fact]
    public async Task The_bundle_holds_one_entry_per_section_and_a_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "settings.json"), """{"apiKey":"sk-ant-secretsecret","model":"x"}""");
            using var http = new HttpClient();
            var bundle = await DiagnosticReport.BuildAsync(Context(root), http);

            using var archive = new ZipArchive(new MemoryStream(bundle.Zip), ZipArchiveMode.Read);
            var names = archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(
                ["dmp-names.txt", "main-log.txt", "managed-config.txt", "manifest.json",
                 "reachability.txt", "system-info.txt"],
                names);

            var config = bundle.Sections.Single(s => s.Id == DiagnosticReport.ConfigSection).Text;
            Assert.DoesNotContain("secretsecret", config, StringComparison.Ordinal);
            Assert.Contains("<redacted>", config, StringComparison.Ordinal);
            Assert.True(bundle.PreviewLines.Count <= DiagnosticReport.PreviewLineCap);
            Assert.All(bundle.PreviewLines, line => Assert.StartsWith("[", line, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reachability_says_so_when_nothing_is_configured()
    {
        using var http = new HttpClient();
        Assert.Equal(
            "(no inference or MCP hosts are configured)",
            await DiagnosticReport.ReachabilityAsync(
                Context(Path.GetTempPath()), http, CancellationToken.None));
    }
}

/// <summary>
/// The scrubber's placeholders are the reference's own, and it replaces the four
/// kinds the modal's note names.
/// </summary>
public sealed class LineScrubberTests
{
    private readonly LineScrubber _scrubber = new();

    [Fact]
    public void An_email_address_becomes_the_references_placeholder() =>
        Assert.Equal("from <email> to <email>", _scrubber.Line("from a.b+c@example.com to x@y.co.uk"));

    [Fact]
    public void An_ip_address_becomes_a_placeholder_but_a_clock_time_does_not()
    {
        Assert.Equal("peer <ip>", _scrubber.Line("peer 192.168.1.14"));
        Assert.Equal("12:34:56 done", _scrubber.Line("12:34:56 done"));

        // The reference's own pattern needs two "hex:" groups before the tail, so
        // the "fe80::" prefix is left where it is; this is its behaviour, not a gap.
        Assert.Equal("v6 fe80::<ip>", _scrubber.Line("v6 fe80::1ff:fe23:4567:890a"));
    }

    [Fact]
    public void Tokens_become_the_references_placeholders()
    {
        Assert.Equal("key=<token>", _scrubber.Line("key=sk-ant-api03-abcdefghijklmno"));
        Assert.Equal("Authorization: Bearer <token>", _scrubber.Line("Authorization: Bearer abcdefgh12345678"));
        Assert.Equal("t=<jwt>", _scrubber.Line("t=eyJhbGciOiJI.eyJzdWIiOiIx.SflKxwRJSMeK"));
        Assert.Equal("aws <token>", _scrubber.Line("aws AKIAIOSFODNN7EXAMPLE"));
    }

    [Fact]
    public void Paths_lose_the_user_the_drive_letter_and_the_share()
    {
        Assert.Equal(@"<drv>:\Users\<user>\notes.txt", _scrubber.Line(@"C:\Users\alice\notes.txt"));
        Assert.Equal("/home/<user>/notes.txt", _scrubber.Line("/home/alice/notes.txt"));
        Assert.Equal("<unc>", _scrubber.Line(@"\\server\share"));
    }

    [Fact]
    public void A_url_loses_its_user_info() =>
        Assert.Equal("https://<userinfo>@example.com/x", _scrubber.Line("https://bob:hunter2@example.com/x"));

    [Fact]
    public void Every_line_of_a_section_is_scrubbed() =>
        Assert.Equal("a <email>\nb <ip>", _scrubber.Apply("a me@example.com\r\nb 10.0.0.1"));
}
