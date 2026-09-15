using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// Compares two release versions the way the updater needs to: numeric part by
/// numeric part, with a missing part reading as zero, so "1.10.0" is newer than
/// "1.9.3" and "1.2" equals "1.2.0".
/// </summary>
public static partial class ReleaseVersion
{
    [GeneratedRegex(@"\d+(?:\.\d+)*")]
    private static partial Regex NumericRun();

    /// <summary>
    /// The version a release tag names, or null when the tag carries no number.
    /// Tags are written "v1.2.3" as often as "1.2.3", and a release may add a
    /// suffix the comparison has no use for.
    /// </summary>
    public static string? FromTag(string? tag) =>
        tag is null ? null : NumericRun().Match(tag) is { Success: true } match ? match.Value : null;

    /// <summary>Negative, zero or positive, as <see cref="IComparable"/> reads.</summary>
    public static int Compare(string left, string right)
    {
        var a = Parts(left);
        var b = Parts(right);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var result = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    public static bool IsNewerThan(string candidate, string current) =>
        Compare(candidate, current) > 0;

    private static int[] Parts(string version) =>
        [.. (FromTag(version) ?? "0").Split('.').Select(static p => int.TryParse(p, out var n) ? n : 0)];
}

/// <summary>
/// The bodies of the reference's "Couldn't check for updates" dialog. It has two,
/// and picks between them with <c>pSn</c>: a response that is HTML, unparseable
/// JSON or one of a named set of transport failures reads as a network that
/// intercepted the request, and says so; anything else reports the error.
/// </summary>
public static partial class UpdateCheckFailure
{
    public const string Title = "Couldn't check for updates";

    [GeneratedRegex(@"<!DOCTYPE|<html|is not valid JSON", RegexOptions.IgnoreCase)]
    private static partial Regex NotJson();

    [GeneratedRegex("server sent an invalid response", RegexOptions.IgnoreCase)]
    private static partial Regex InvalidResponse();

    [GeneratedRegex(
        @"net::ERR_(EMPTY_RESPONSE|CONNECTION_(REFUSED|RESET)|CERT_|TUNNEL_CONNECTION_FAILED|BLOCKED_BY_)")]
    private static partial Regex BlockedTransport();

    /// <summary>The reference's <c>pSn</c>: does this failure look like an intercepted network?</summary>
    public static bool LooksIntercepted(string message) =>
        NotJson().IsMatch(message) || InvalidResponse().IsMatch(message) ||
        BlockedTransport().IsMatch(message);

    /// <summary>The dialog body for a failure, naming the hosts when the network is the suspect.</summary>
    public static string Detail(string message, string host) =>
        LooksIntercepted(message)
            ? $"Jarvis couldn't reach the update server. If you're on a work network, a firewall or " +
              $"proxy may be blocking {host} — ask your IT team to allow them. Otherwise, check your " +
              $"connection and try again, or download the latest version from {AppUpdateService.ReleasesPage}."
            : $"Failed to check for updates: {message}";
}

/// <summary>
/// The updater's transport: the GitHub releases of this repository, where the
/// reference reads a Squirrel/MSIX feed of its own. It drives
/// <see cref="UpdateStateMachine"/> through exactly the five events the reference's
/// autoUpdater raises, so the states, the menu rows and the sidebar card are the
/// reference's whatever the transport is.
///
/// The schedule is the reference's, measured from its <c>gSn</c> loop: a tick
/// every hour, a check every fourth tick, and — once an update is staged — a tick
/// every ten minutes, which is how it notices a release that replaces the staged
/// one.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    /// <summary>The reference's <c>Oxn</c>: the idle tick.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    /// <summary>The reference's <c>kxn</c>: the tick while an update is already staged.</summary>
    public static readonly TimeSpan StagedTick = TimeSpan.FromMinutes(10);

    /// <summary>The reference's <c>zU</c>: how many ticks pass between two checks.</summary>
    public const int CheckIntervalTicks = 4;

    /// <summary>The repository whose releases are this app's update feed.</summary>
    public const string Repository = "liquid8796/jarvis-code";

    public const string ReleasesApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";
    public const string FeedHost = "api.github.com";

    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly string _stagingRoot;
    private readonly CancellationTokenSource _cts = new();
    private int _ticksSinceCheck;
    private bool _pollingStarted;

    public AppUpdateService(string currentVersion, string stagingRoot, HttpClient? http = null)
    {
        _currentVersion = currentVersion;
        _stagingRoot = stagingRoot;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent", $"JarvisCode/{currentVersion}");
        }
    }

    public UpdateStateMachine States { get; } = new();

    /// <summary>Where the staged build sits once it is downloaded, or null.</summary>
    public string? StagedPath { get; private set; }

    /// <summary>Whether the user switched auto-update off (the reference's policy gate).</summary>
    public bool Disabled { get; set; }

    /// <summary>
    /// Starts the reference's tick loop. The first check runs right away, as the
    /// reference's bootstrap does once the app has settled.
    /// </summary>
    public void StartPolling()
    {
        if (_pollingStarted)
        {
            return;
        }

        _pollingStarted = true;
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        try
        {
            await CheckAsync(manual: false).ConfigureAwait(false);
            while (!_cts.IsCancellationRequested)
            {
                var staged = States.State.Status == UpdateStatus.Ready;
                await Task.Delay(staged ? StagedTick : Tick, _cts.Token).ConfigureAwait(false);
                if (staged)
                {
                    // A staged update re-checks on every short tick, looking for a
                    // release that replaces it.
                    await CheckAsync(manual: false).ConfigureAwait(false);
                    continue;
                }

                if (++_ticksSinceCheck < CheckIntervalTicks)
                {
                    continue;
                }

                _ticksSinceCheck = 0;
                await CheckAsync(manual: false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The app is closing.
        }
    }

    /// <summary>
    /// One check. Returns the state it settled in so a manual check can raise the
    /// right dialog. A check refuses to start while one is already running, which
    /// is the reference's <c>isCheckInFlight</c> guard.
    /// </summary>
    public async Task<UpdateState> CheckAsync(bool manual)
    {
        if (Disabled)
        {
            return States.State;
        }

        if (manual && States.SetManualCheck())
        {
            return States.State;
        }

        if (States.IsCheckInFlight)
        {
            return States.State;
        }

        States.OnCheckingForUpdate();
        try
        {
            var release = await LatestReleaseAsync(_cts.Token).ConfigureAwait(false);
            var version = ReleaseVersion.FromTag(release?.Tag);
            if (version is null || !ReleaseVersion.IsNewerThan(version, _currentVersion))
            {
                States.OnUpdateNotAvailable();
                return States.State;
            }

            States.OnUpdateAvailable();
            StagedPath = await DownloadAsync(release!, version, _cts.Token).ConfigureAwait(false);
            States.OnUpdateDownloaded(version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            States.OnError(ex.Message);
        }

        return States.State;
    }

    /// <summary>What one release of this repository says about itself.</summary>
    /// <param name="Tag">Its tag, which carries the version.</param>
    /// <param name="AssetUrl">The download the app installs, or null when it ships none.</param>
    /// <param name="AssetName">That download's file name.</param>
    public sealed record Release(string? Tag, string? AssetUrl, string? AssetName);

    private async Task<Release?> LatestReleaseAsync(CancellationToken token)
    {
        using var response = await _http
            .GetAsync(ReleasesApi, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // A repository with no releases yet is "no update", not a failure.
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return ParseRelease(body);
    }

    /// <summary>
    /// Reads the fields the updater needs out of a GitHub release. Split out so the
    /// asset choice is testable without a network: a Windows installer wins over an
    /// archive, and an archive over anything else.
    /// </summary>
    public static Release? ParseRelease(string json)
    {
        System.Text.Json.Nodes.JsonNode? node;
        try
        {
            node = System.Text.Json.Nodes.JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"{ex.Message} is not valid JSON", ex);
        }

        if (node is not System.Text.Json.Nodes.JsonObject release)
        {
            return null;
        }

        var tag = release["tag_name"]?.GetValue<string>();
        string? bestUrl = null;
        string? bestName = null;
        var bestRank = int.MaxValue;
        foreach (var asset in release["assets"] as System.Text.Json.Nodes.JsonArray ?? [])
        {
            var name = asset?["name"]?.GetValue<string>();
            var url = asset?["browser_download_url"]?.GetValue<string>();
            if (name is null || url is null)
            {
                continue;
            }

            var rank = AssetRank(name);
            if (rank < bestRank)
            {
                (bestRank, bestUrl, bestName) = (rank, url, name);
            }
        }

        return new Release(tag, bestUrl, bestName);
    }

    private static int AssetRank(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 0
        : name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? 1
        : name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? 2
        : int.MaxValue;

    private async Task<string> DownloadAsync(Release release, string version, CancellationToken token)
    {
        if (release.AssetUrl is null || release.AssetName is null)
        {
            throw new InvalidOperationException(
                $"Release {version} ships no Windows download.");
        }

        var directory = Path.Combine(_stagingRoot, version);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, release.AssetName);

        // A partial file from an interrupted run must never be handed to the
        // installer, so the download lands beside its target and is moved on.
        var partial = target + ".part";
        using (var response = await _http
            .GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var file = File.Create(partial);
            await source.CopyToAsync(file, token).ConfigureAwait(false);
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Hands the staged build to the OS and reports whether it started. The
    /// reference relaunches through Squirrel or the Store; this build has neither,
    /// so an installer asset is simply run, and an archive is unpacked over the
    /// installation by a step that first waits for this process to be gone —
    /// nothing may overwrite an exe that is still mapped.
    /// </summary>
    public bool BeginInstall()
    {
        if (StagedPath is not { } staged || !File.Exists(staged))
        {
            return false;
        }

        if (staged.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return StartArchiveInstaller(staged);
        }

        Start(new System.Diagnostics.ProcessStartInfo(staged) { UseShellExecute = true });
        return true;
    }

    private bool StartArchiveInstaller(string archive)
    {
        var installDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (installDirectory is null || Environment.ProcessPath is null)
        {
            return false;
        }

        var script = Path.Combine(Path.GetDirectoryName(archive)!, "install.ps1");
        File.WriteAllText(script, ArchiveInstallerScript, new System.Text.UTF8Encoding(false));
        Start(new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                "-File", script,
                "-ProcessId", Environment.ProcessId.ToString(),
                "-Archive", archive,
                "-Destination", installDirectory,
                "-Relaunch", Environment.ProcessPath,
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return true;
    }

    private static void Start(System.Diagnostics.ProcessStartInfo info)
    {
        using var process = System.Diagnostics.Process.Start(info);
    }

    /// <summary>
    /// The updater step. It waits for this process to exit before it touches the
    /// installation, because Windows keeps a running image locked, and it relaunches
    /// whatever happens so a failed copy never leaves the user with no app.
    /// </summary>
    private const string ArchiveInstallerScript = """
        param(
          [int]$ProcessId,
          [string]$Archive,
          [string]$Destination,
          [string]$Relaunch
        )
        $ErrorActionPreference = 'Stop'
        try { Wait-Process -Id $ProcessId -Timeout 60 } catch { }
        try {
          Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force
        } catch { }
        Start-Process -FilePath $Relaunch
        """;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
