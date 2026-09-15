using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Mcp;

/// <summary>Why a headers helper produced no headers.</summary>
public enum McpHeadersHelperFailure
{
    /// <summary>The command did not run, exited non-zero, or printed nothing.</summary>
    ExecFailed,

    /// <summary>Its output was not JSON.</summary>
    ParseFailed,

    /// <summary>Its output was JSON, but not an object.</summary>
    NonObject,

    /// <summary>One of the object's values was not a string.</summary>
    NonStringValue,

    /// <summary>Nobody approved running it (this build's own gate, not the reference's).</summary>
    NotTrusted,
}

/// <summary>The helper's answer: headers, or the reason there are none.</summary>
public sealed record McpHeadersHelperResult(
    IReadOnlyDictionary<string, string>? Headers, McpHeadersHelperFailure? Failure)
{
    public bool Ok => Headers is not null;
}

/// <summary>
/// The <c>headersHelper</c> a remote MCP server entry may carry: a command that
/// prints a JSON object of HTTP headers, run to mint a short-lived credential
/// for that server.
/// </summary>
/// <remarks>
/// Measured in CLI 2.1.257: the runner is its <c>F_t</c> (byte 184790177) — the
/// command goes through a shell with a <b>10s</b> timeout and a <b>1 MB</b>
/// output cap, a non-zero exit or empty stdout is <c>exec_failed</c>, unparseable
/// stdout is <c>parse_failed</c>, a non-object is <c>non_object</c>, and any
/// non-string value is <c>non_string_value</c>. Its four sentences are that
/// switch's own (<c>Wr</c>, at 208834800), and the environment it passes is
/// <c>Ur</c>'s: <c>CLAUDE_CODE_MCP_SERVER_NAME</c> and
/// <c>CLAUDE_CODE_MCP_SERVER_URL</c>. The helper's headers overlay the entry's
/// static ones (its <c>Sat</c>).
///
/// The reference's own trust gate is <c>isRepoResidentConfig</c>: a server
/// declared in a repository's settings needs that workspace to have been
/// trusted, and without it the helper is skipped rather than run. This build
/// asks instead — once per server per session, through the host — because a
/// configured command that mints a credential is the same kind of decision a
/// hook command is, and this app's MCP config is not scoped the way the
/// reference's settings tiers are. Declared as an addition in
/// Deltas/reference-surface-deltas.tsv.
/// </remarks>
public static class McpHeadersHelper
{
    /// <summary>The reference's <c>_n</c>: how long the command may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The reference's <c>wn</c>: how much output it may print.</summary>
    public const int MaxOutputChars = 1_000_000;

    /// <summary>The environment the reference gives the command.</summary>
    public const string ServerNameVariable = "CLAUDE_CODE_MCP_SERVER_NAME";

    public const string ServerUrlVariable = "CLAUDE_CODE_MCP_SERVER_URL";

    /// <summary>The reference's four failure sentences, verbatim.</summary>
    public static string Message(string serverName, McpHeadersHelperFailure failure) => failure switch
    {
        McpHeadersHelperFailure.ExecFailed =>
            $"headersHelper for MCP server '{serverName}' did not return a valid value",
        McpHeadersHelperFailure.ParseFailed =>
            $"headersHelper for MCP server '{serverName}' did not return valid JSON",
        McpHeadersHelperFailure.NonObject =>
            $"headersHelper for MCP server '{serverName}' must return a JSON object with string key-value pairs",
        McpHeadersHelperFailure.NonStringValue =>
            $"headersHelper for MCP server '{serverName}' returned a non-string header value",
        // This build's own gate, so this sentence is its own.
        _ => $"headersHelper for MCP server '{serverName}' was not run: nobody approved the command.",
    };

    /// <summary>Runs the helper and reads its headers back.</summary>
    public static async Task<McpHeadersHelperResult> RunAsync(
        McpServerConfig config, string workingDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.HeadersHelper))
        {
            return new McpHeadersHelperResult(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null);
        }

        var output = await RunCommandAsync(config, workingDirectory, cancellationToken);
        if (output is null)
        {
            return new McpHeadersHelperResult(null, McpHeadersHelperFailure.ExecFailed);
        }

        return Parse(output);
    }

    /// <summary>
    /// The reference's parse half, separated so it can be tested without a
    /// process: JSON, an object, and every value a string.
    /// </summary>
    internal static McpHeadersHelperResult Parse(string stdout)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(stdout.Trim());
        }
        catch (System.Text.Json.JsonException)
        {
            return new McpHeadersHelperResult(null, McpHeadersHelperFailure.ParseFailed);
        }

        if (parsed is not JsonObject entries)
        {
            return new McpHeadersHelperResult(null, McpHeadersHelperFailure.NonObject);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            if (value is not JsonValue json || !json.TryGetValue(out string? text))
            {
                return new McpHeadersHelperResult(null, McpHeadersHelperFailure.NonStringValue);
            }

            headers[key] = text;
        }

        return new McpHeadersHelperResult(headers, null);
    }

    private static async Task<string?> RunCommandAsync(
        McpServerConfig config, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment[ServerNameVariable] = config.Name;
        startInfo.Environment[ServerUrlVariable] = config.Url ?? "";
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(config.HeadersHelper!);
        }
        else
        {
            startInfo.FileName = "/bin/bash";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(config.HeadersHelper!);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
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
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }

        _ = await stderrTask;
        var stdout = await stdoutTask;
        if (process.ExitCode != 0 || string.IsNullOrEmpty(stdout) || stdout.Length > MaxOutputChars)
        {
            return null;
        }

        return stdout;
    }
}
