using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>What a credential helper run came to (the reference's <c>b</c> states).</summary>
public enum HelperState
{
    Success,
    Warning,
    Failed,
}

/// <summary>One credential-helper run, in the shape the reference's result card reads.</summary>
public sealed record HelperRun(
    HelperState State,
    string Headline,
    string Detail,
    int? ExitCode,
    int StdoutBytes,
    int HeaderCount,
    double ElapsedSeconds,
    string StderrRedacted,
    string? Credential);

/// <summary>
/// The credential helper scripts the reference's third-party inference window can
/// run instead of storing a key: a script that prints a credential on stdout —
/// either a bare token or a JSON object of auth headers — run under a timeout.
/// The outcomes and their sentences are the reference's own (ion-dist
/// <c>c71860c77-DuPx-LoQ.js</c>: <c>Gxk66QH6Qu</c> … <c>TcNnUVP8Mw</c> for the
/// headlines and <c>Ruse+1F0VI</c> … <c>o76PKBGVE1</c> for the detail lines).
/// </summary>
public static class CredentialHelpers
{
    /// <summary>The reference's default helper timeout, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 10;

    public const string HeadlineValid = "Helper returned a valid credential";
    public const string HeadlineWarnings = "Credential parsed, with warnings";
    public const string HeadlineCouldNotRun = "Couldn’t run the helper";
    public const string HeadlineTimedOut = "Helper timed out";
    public const string HeadlineTerminated = "Helper was terminated before exit";
    public const string HeadlineNoOutput = "Helper produced no output";
    public const string HeadlineCancelled = "Helper run cancelled";
    public const string HeadlineUnusable = "Helper ran, but its output isn’t a usable credential";
    public const string HeadlineError = "Error running the helper";

    public static string HeadlineExitCode(int code) => $"Helper exited with code {code}";

    public static string DetailJson(int headers) =>
        $"JSON output with {headers} auth header{(headers == 1 ? "" : "s")}, parsed cleanly.";

    public const string DetailBareToken = "Bare token output, parsed cleanly.";

    public const string DetailStderr =
        "The credential parsed, but the script wrote to stderr. Review the output below.";

    public const string DetailCancelled = "The script was cancelled before it produced a credential.";

    public const string DetailNoStdout =
        "The script exited cleanly but wrote nothing to stdout. Check that it prints the credential.";

    public const string DetailTerminated = "Script terminated before producing a credential.";

    /// <summary>The reference's elapsed labels: "{sec} s" while it ran, "{sec} s (limit)" when the timeout stopped it.</summary>
    public static string Elapsed(double seconds, bool hitLimit) =>
        hitLimit ? $"{seconds:0.#} s (limit)" : $"{seconds:0.#} s";

    public static string Running(double seconds) => $"Running… {seconds:0.#} s";

    /// <summary>
    /// Reads a helper's stdout: a JSON object whose values are auth headers, or a
    /// bare token on one line. Returns the credential and how many headers it named.
    /// </summary>
    public static (string? Credential, int Headers) Parse(string stdout)
    {
        var text = stdout.Trim();
        if (text.Length == 0)
        {
            return (null, 0);
        }

        if (text.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject json)
                {
                    var headers = json["headers"] as JsonObject ?? json;
                    var count = headers.Count;
                    var credential = (string?)headers.FirstOrDefault().Value
                                     ?? (string?)json["token"]
                                     ?? (string?)json["api_key"];
                    return credential is { Length: > 0 } ? (credential, count) : (null, count);
                }
            }
            catch (JsonException)
            {
                return (null, 0);
            }

            return (null, 0);
        }

        var line = text.Split('\n')[0].Trim();
        return line.Length > 0 ? (line, 0) : (null, 0);
    }

    /// <summary>Runs the helper and classifies the result the way the reference's card reads it.</summary>
    public static async Task<HelperRun> RunAsync(
        string script,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-NoLogo");
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add(script);

            using var process = Process.Start(info);
            if (process is null)
            {
                return new HelperRun(HelperState.Failed, HeadlineCouldNotRun, "", null, 0, 0,
                    started.Elapsed.TotalSeconds, "", null);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)));
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                return cancellationToken.IsCancellationRequested
                    ? new HelperRun(HelperState.Failed, HeadlineCancelled, DetailCancelled, null, 0, 0,
                        started.Elapsed.TotalSeconds, "", null)
                    : new HelperRun(HelperState.Failed, HeadlineTimedOut, DetailTerminated, null, 0, 0,
                        started.Elapsed.TotalSeconds, "", null);
            }

            var output = await stdout;
            var errors = await stderr;
            var elapsed = started.Elapsed.TotalSeconds;

            if (process.ExitCode != 0)
            {
                return new HelperRun(HelperState.Failed, HeadlineExitCode(process.ExitCode), errors.Trim(),
                    process.ExitCode, output.Length, 0, elapsed, errors.Trim(), null);
            }

            if (output.Trim().Length == 0)
            {
                return new HelperRun(HelperState.Failed, HeadlineNoOutput, DetailNoStdout,
                    process.ExitCode, 0, 0, elapsed, errors.Trim(), null);
            }

            var (credential, headers) = Parse(output);
            if (credential is null)
            {
                return new HelperRun(HelperState.Failed, HeadlineUnusable, "",
                    process.ExitCode, output.Length, headers, elapsed, errors.Trim(), null);
            }

            var detail = headers > 0 ? DetailJson(headers) : DetailBareToken;
            return errors.Trim().Length > 0
                ? new HelperRun(HelperState.Warning, HeadlineWarnings, DetailStderr,
                    process.ExitCode, output.Length, headers, elapsed, errors.Trim(), credential)
                : new HelperRun(HelperState.Success, HeadlineValid, detail,
                    process.ExitCode, output.Length, headers, elapsed, "", credential);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            return new HelperRun(HelperState.Failed, HeadlineError, ex.Message, null, 0, 0,
                started.Elapsed.TotalSeconds, "", null);
        }
    }
}
