using JarvisCode.Core.BackgroundTasks;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Mcp;

/// <summary>
/// The reference's <c>getMcpAutoBackgroundMs</c> / <c>callMcpToolWithAutoBackground</c>
/// (CLI 2.1.251): an MCP tool call still running after two minutes stops holding
/// the turn open. It is registered as a background task, the model is told where
/// it went, and its result arrives later as a task notification.
///
/// The call itself is not interrupted — it is unlinked from the turn's
/// cancellation first, which is the whole point: the turn ends, the work does
/// not. Stopping it afterwards goes through the task's own stop request.
/// </summary>
public static class McpAutoBackground
{
    /// <summary>The reference's <c>K</c>.</summary>
    public const int DefaultDelayMs = 120_000;

    /// <summary>
    /// The upper clamp on the environment override: the reference's <c>vb</c>,
    /// 2147483647 — the largest delay a JavaScript timer takes, which is also
    /// <see cref="int.MaxValue"/>.
    /// </summary>
    public const int MaxDelayMs = 2_147_483_647;

    /// <summary>The kind a moved MCP call is listed under in the tasks pane.</summary>
    public const string TaskKind = "Mcp";

    /// <summary>
    /// Transports the reference never moves off the turn — its <c>V</c>. An IDE
    /// server's calls are part of an editor interaction, and a backgrounded one
    /// would leave the editor waiting on nobody.
    /// </summary>
    private static readonly HashSet<string> NeverMoved =
        new(StringComparer.Ordinal) { "sse-ide", "ws-ide" };

    /// <summary>The reference's <c>eZ</c>: what the notice calls this call.</summary>
    public static string Describe(string serverName, string toolName)
    {
        var text = $"{Sanitize(serverName)}/{Sanitize(toolName)}";
        return text.Length > 200 ? text[..200] : text;
    }

    private static string Sanitize(string value)
    {
        var chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(chars[i]);
            if (category is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                chars[i] = ' ';
            }
        }

        return new string(chars).Trim();
    }

    /// <summary>
    /// How long a call of this server may hold the turn, in milliseconds; 0
    /// disables the move. Ported rule for rule, including which environment
    /// variable wins.
    /// </summary>
    public static int DelayMs(
        string? serverType,
        bool isNonInteractiveSession,
        Func<string, string?>? environment = null,
        bool backgroundTasksDisabled = false)
    {
        if (NeverMoved.Contains(serverType ?? ""))
        {
            return 0;
        }

        var env = environment ?? Environment.GetEnvironmentVariable;
        // The reference's Ll(): a host that has switched background tasks off,
        // or CLAUDE_CODE_DISABLE_BACKGROUND_TASKS, leaves every call on the turn
        // (new in CLI 2.1.257, between the transport check and the print gate).
        if (backgroundTasksDisabled || !string.IsNullOrEmpty(env("CLAUDE_CODE_DISABLE_BACKGROUND_TASKS")))
        {
            return 0;
        }

        if (isNonInteractiveSession && string.IsNullOrEmpty(env("CLAUDE_AUTO_BACKGROUND_TASKS")))
        {
            return 0;
        }

        var configured = env("CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS");
        if (!string.IsNullOrEmpty(configured))
        {
            return long.TryParse(configured, out var ms)
                ? (int)Math.Min(Math.Max(0, ms), MaxDelayMs)
                : 0;
        }

        return DefaultDelayMs;
    }

    /// <summary>The reference's notice, returned in place of the result the turn stopped waiting for.</summary>
    public static string MovedNotice(string description, int elapsedSeconds, string taskId) =>
        $"MCP tool \"{description}\" is still running after {elapsedSeconds}s. It was moved to the " +
        $"background as task {taskId} and keeps running; you'll receive a notification with the result " +
        $"when it completes. You can keep working in the meantime. To stop it, use TaskStop with " +
        $"task_id \"{taskId}\". Note: it does not survive exiting this session.";

    /// <summary>
    /// Runs one MCP call, moving it to the background if it outlives
    /// <paramref name="delayMs"/>.
    ///
    /// <paramref name="hasPendingElicitation"/> is the reference's own guard: a
    /// call waiting on an answer from the user is not late, it is blocked on a
    /// question, and moving it would strand the question.
    /// </summary>
    public static async Task<ToolResult> RunAsync(
        Func<CancellationToken, Task<ToolResult>> run,
        string description,
        int delayMs,
        BackgroundTaskManager? tasks,
        string? sessionId,
        Func<bool>? hasPendingElicitation,
        CancellationToken cancellationToken)
    {
        if (delayMs <= 0 || tasks is null)
        {
            return await run(cancellationToken).ConfigureAwait(false);
        }

        var started = DateTimeOffset.UtcNow;
        var detached = new CancellationTokenSource();
        var unlink = cancellationToken.Register(static state =>
        {
            try
            {
                ((CancellationTokenSource)state!).Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The call finished and disposed its source first.
            }
        }, detached);

        var call = run(detached.Token);
        bool moved = false;
        try
        {
            while (true)
            {
                using var timer = new CancellationTokenSource();
                var elapsed = await Task.WhenAny(call, Task.Delay(delayMs, timer.Token)).ConfigureAwait(false);
                timer.Cancel();
                if (elapsed == call || cancellationToken.IsCancellationRequested)
                {
                    return await call.ConfigureAwait(false);
                }

                // Still waiting on the user, not on the server.
                if (hasPendingElicitation?.Invoke() != true)
                {
                    moved = true;
                    break;
                }
            }
        }
        finally
        {
            // The turn is done with this call either way: it came back, or it
            // left. What differs is the source — a moved call still needs its
            // own, and disposes it when it finally settles.
            unlink.Dispose();
            if (!moved)
            {
                detached.Dispose();
            }
        }

        var task = tasks.Adopt(
            description, sessionId, description, killRequested: () =>
            {
                try
                {
                    detached.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already settled.
                }
            },
            kind: TaskKind);

        _ = call.ContinueWith(
            finished =>
            {
                var (text, failed) = finished.Status switch
                {
                    TaskStatus.RanToCompletion => (finished.Result.Content, finished.Result.IsError),
                    TaskStatus.Canceled => ("The MCP call was stopped before it finished.", true),
                    _ => (finished.Exception?.GetBaseException().Message ?? "The MCP call failed.", true),
                };
                task.Append(text);
                task.Complete(finished.Status == TaskStatus.Canceled ? null : failed ? 1 : 0);
                detached.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var seconds = (int)Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds);
        return ToolResult.Success(MovedNotice(description, seconds, task.Id));
    }
}
