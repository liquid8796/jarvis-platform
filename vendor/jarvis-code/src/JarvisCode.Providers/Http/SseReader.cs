using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using JarvisCode.Core.Utilities;

namespace JarvisCode.Providers.Http;

/// <summary>
/// Minimal server-sent-events parser: dispatches an event at each blank line,
/// joining multiple data lines with '\n' and ignoring comment lines, per the
/// SSE specification.
/// </summary>
public static class SseReader
{
    /// <summary>The last data line of an OpenAI-compatible stream.</summary>
    private const string DoneSentinel = "[DONE]";

    /// <summary>
    /// How often a long stream reports progress. A stream that dies mid-answer
    /// leaves no closing line, so this is what pins when it stopped.
    /// </summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    private static int _nextStreamId;

    public static async IAsyncEnumerable<SseEvent> ReadEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        string source = "sse")
    {
        var id = Interlocked.Increment(ref _nextStreamId);
        var clock = Stopwatch.StartNew();
        var lastProgress = TimeSpan.Zero;
        var events = 0;
        string? lastEventName = null;
        var lastWasDone = false;
        var reachedEnd = false;

        // Counting and timing happen here so the two dispatch points below stay
        // a single `yield return` each.
        SseEvent Dispatch(string? name, string payload)
        {
            events++;
            lastEventName = name;
            lastWasDone = payload == DoneSentinel;
            if (DiagnosticLog.IsEnabled && clock.Elapsed - lastProgress >= ProgressInterval)
            {
                lastProgress = clock.Elapsed;
                DiagnosticLog.Write($"sse#{id} {source}: {events} events, {clock.Elapsed.TotalSeconds:0}s so far");
            }

            return new SseEvent(name, payload);
        }

        DiagnosticLog.Write($"sse#{id} {source}: open");
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? eventName = null;
            var data = new StringBuilder();
            bool hasData = false;

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    if (hasData)
                        yield return Dispatch(eventName, data.ToString());
                    eventName = null;
                    data.Clear();
                    hasData = false;
                    continue;
                }
                if (line.StartsWith(':'))
                    continue;

                int colon = line.IndexOf(':');
                string field = colon < 0 ? line : line[..colon];
                string value = colon < 0 ? "" : line[(colon + 1)..];
                if (value.StartsWith(' '))
                    value = value[1..];

                switch (field)
                {
                    case "event":
                        eventName = value;
                        break;
                    case "data":
                        if (hasData)
                            data.Append('\n');
                        data.Append(value);
                        hasData = true;
                        break;
                }
            }

            reachedEnd = true;
            if (hasData)
                yield return Dispatch(eventName, data.ToString());
        }
        finally
        {
            // reason=eof with done=false and no terminating event name is a stream the
            // server cut short; reason=disposed means the consumer stopped reading.
            DiagnosticLog.Write(
                $"sse#{id} {source}: closed after {events} events, {clock.Elapsed.TotalSeconds:0.0}s, " +
                $"last={lastEventName ?? "(unnamed)"} done={lastWasDone} " +
                $"reason={(reachedEnd ? "eof" : "disposed")}");
        }
    }
}
