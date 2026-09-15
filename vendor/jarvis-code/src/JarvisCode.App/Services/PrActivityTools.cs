using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference app's PR activity subscription, rebuilt on `gh` polling:
/// subscribe_pr_activity watches a pull request and delivers new review/issue
/// comments, failed or timed-out check runs, and close/reopen/merge transitions
/// as task notifications. Like the reference, CI success and new pushes do NOT
/// arrive — poll `gh pr checks N` for those — and neither do merge-conflict
/// transitions.
/// </summary>
public sealed class PrActivityManager : IDisposable
{
    private const int PollSeconds = 60;

    private sealed class Watch
    {
        public required int PrNumber { get; init; }
        public required string WorkingDirectory { get; init; }
        public required Action<string, string, string, string> Deliver { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        public HashSet<long> SeenCommentIds { get; } = [];
        public HashSet<string> ReportedFailedChecks { get; } = [];
        public string? LastState { get; set; }
        public bool Primed { get; set; }
    }

    private readonly Dictionary<int, Watch> _watches = [];
    private readonly object _lock = new();

    public ToolResult Subscribe(int prNumber, string workingDirectory, Action<string, string, string, string> deliver)
    {
        if (prNumber <= 0)
            return ToolResult.Error("pr_number must be a positive pull request number.");

        lock (_lock)
        {
            if (_watches.ContainsKey(prNumber))
                return ToolResult.Success($"Already subscribed to PR #{prNumber}.");
            var watch = new Watch { PrNumber = prNumber, WorkingDirectory = workingDirectory, Deliver = deliver };
            _watches[prNumber] = watch;
            _ = Task.Run(() => PollLoopAsync(watch));
        }

        return ToolResult.Success(
            $"Subscribed to PR #{prNumber}. New review comments, failed or timed-out check runs, and " +
            "close/reopen/merge events will arrive as task notifications. CI success and new pushes do NOT " +
            "arrive — poll `gh pr checks` to learn when checks pass — and merge-conflict transitions do not " +
            "either. Call unsubscribe_pr_activity to stop.");
    }

    public ToolResult Unsubscribe(int prNumber)
    {
        lock (_lock)
        {
            if (!_watches.Remove(prNumber, out var watch))
                return ToolResult.Error($"Not subscribed to PR #{prNumber}.");
            watch.Cts.Cancel();
        }

        return ToolResult.Success($"Unsubscribed from PR #{prNumber}.");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var watch in _watches.Values)
                watch.Cts.Cancel();
            _watches.Clear();
        }
    }

    private async Task PollLoopAsync(Watch watch)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(PollSeconds));
        try
        {
            await PollOnceAsync(watch); // prime the baseline silently
            watch.Primed = true;
            while (await timer.WaitForNextTickAsync(watch.Cts.Token))
            {
                await PollOnceAsync(watch);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PollOnceAsync(Watch watch)
    {
        var view = await GhAsync(watch.WorkingDirectory,
            $"pr view {watch.PrNumber} --json state,comments,reviews,statusCheckRollup");
        if (view is null)
            return;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(view) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (root is null)
            return;

        var events = new List<(string Status, string Summary, string Body)>();

        var state = root["state"]?.GetValue<string>();
        if (state is not null)
        {
            if (watch.Primed && watch.LastState is not null && state != watch.LastState)
            {
                events.Add((state.ToLowerInvariant(),
                    $"PR #{watch.PrNumber} is now {state}",
                    $"The pull request transitioned from {watch.LastState} to {state}."));
            }

            watch.LastState = state;
        }

        foreach (var source in new[] { "comments", "reviews" })
        {
            foreach (var comment in (root[source] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var id = comment["id"] switch
                {
                    JsonValue value when value.TryGetValue<long>(out var number) => number,
                    JsonValue value when value.TryGetValue<string>(out var text) => (long)text.GetHashCode(),
                    _ => 0L,
                };
                var body = comment["body"]?.GetValue<string>() ?? "";
                if (id == 0 || !watch.SeenCommentIds.Add(id) || body.Length == 0)
                    continue;
                if (!watch.Primed)
                    continue;
                var author = (comment["author"] as JsonObject)?["login"]?.GetValue<string>() ?? "someone";
                events.Add(("comment",
                    $"PR #{watch.PrNumber}: new {(source == "reviews" ? "review" : "comment")} from {author}",
                    body));
            }
        }

        foreach (var check in (root["statusCheckRollup"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var name = check["name"]?.GetValue<string>() ?? check["context"]?.GetValue<string>() ?? "check";
            var conclusion = check["conclusion"]?.GetValue<string>() ?? check["state"]?.GetValue<string>() ?? "";
            // Like the reference: only failed or timed-out runs are forwarded.
            if (conclusion is not ("FAILURE" or "TIMED_OUT" or "failure" or "timed_out" or "ERROR" or "error"))
            {
                watch.ReportedFailedChecks.Remove(name);
                continue;
            }

            if (!watch.ReportedFailedChecks.Add(name) || !watch.Primed)
                continue;
            events.Add(("failed",
                $"PR #{watch.PrNumber}: check '{name}' {conclusion.ToLowerInvariant()}",
                $"The check run '{name}' concluded {conclusion}. Inspect it with `gh pr checks {watch.PrNumber}`."));
        }

        foreach (var (status, summary, body) in events)
        {
            watch.Deliver($"pr-{watch.PrNumber}", status, summary, body);
        }
    }

    private static async Task<string?> GhAsync(string workingDirectory, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (process is null)
                return null;
            var output = await process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>The subscribe/unsubscribe tool pair joined into Code turns.</summary>
public static class PrActivityTools
{
    public static IReadOnlyList<ITool> Create(PrActivityManager manager, Action<string, string, string, string> deliver) =>
    [
        new PrTool(
            "subscribe_pr_activity",
            "Subscribe to GitHub PR events for a pull request in this repository (via the gh CLI): new review " +
            "comments, failed or timed-out check runs, and PR close/reopen/merge. Events arrive as task " +
            "notifications. CI success and new pushes do NOT arrive — the watcher only forwards failed or " +
            "timed-out check runs, so poll `gh pr checks N` to learn when checks pass. Merge conflict " +
            "transitions do NOT arrive either — poll `gh pr view N --json mergeable` if tracking conflict " +
            "status. Call these directly — do not delegate subscription management to agents.",
            (args, context) => manager.Subscribe(
                JsonArgs.GetInt(args, "pr_number") ?? 0, context.WorkingDirectory, deliver)),
        new PrTool(
            "unsubscribe_pr_activity",
            "Stop watching a pull request subscribed with subscribe_pr_activity.",
            (args, _) => manager.Unsubscribe(JsonArgs.GetInt(args, "pr_number") ?? 0)),
    ];

    private sealed class PrTool(
        string name, string description, Func<JsonObject, ToolExecutionContext, ToolResult> execute) : ITool
    {
        public string Name => name;

        public string Description => description;

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["pr_number"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "The pull request number in this repository",
                },
            },
            ["required"] = new JsonArray("pr_number"),
        };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) =>
            $"{name}(#{JsonArgs.GetInt(arguments, "pr_number")?.ToString() ?? "?"})";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(execute(arguments, context));
    }
}
