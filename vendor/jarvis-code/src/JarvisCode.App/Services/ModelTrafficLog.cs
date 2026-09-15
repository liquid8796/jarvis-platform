using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Providers;

namespace JarvisCode.App.Services;

/// <summary>Where one captured call has got to.</summary>
public enum ModelTrafficState
{
    /// <summary>Sent; no response headers yet.</summary>
    InFlight,

    /// <summary>Headers arrived; the body is still streaming in.</summary>
    Streaming,

    /// <summary>The response body was read to the end.</summary>
    Completed,

    /// <summary>The body stream was closed before the end — a stopped turn, usually.</summary>
    Interrupted,

    /// <summary>The send itself threw: network error, timeout, or cancellation.</summary>
    Failed,
}

/// <summary>
/// A frozen view of one captured call, so the UI never reads fields another thread is
/// still writing. Bodies are already truncated and headers already redacted.
/// </summary>
public sealed record ModelTrafficSnapshot(
    int Id,
    DateTimeOffset StartedAt,
    string Method,
    string Url,
    string Host,
    string Path,
    IReadOnlyList<KeyValuePair<string, string>> RequestHeaders,
    string RequestBody,
    bool RequestTruncated,
    long RequestBytes,
    string? ModelId,
    ModelTrafficState State,
    int? StatusCode,
    string? ReasonPhrase,
    IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders,
    string ResponseBody,
    bool ResponseTruncated,
    long ResponseBytes,
    string? ContentType,
    bool ResponseIsBinary,
    TimeSpan? TimeToHeaders,
    TimeSpan? Elapsed,
    string? Error)
{
    public bool IsRunning => State is ModelTrafficState.InFlight or ModelTrafficState.Streaming;

    public bool Failed => State == ModelTrafficState.Failed || StatusCode is >= 400;
}

/// <summary>
/// One HTTP attempt a provider made. A single model call can produce several of these:
/// <c>ProviderHttp</c> rotates keys and retries, and every attempt is captured on its own
/// so a 429-then-retry reads as the two calls it really was.
/// </summary>
public sealed class ModelTrafficEntry
{
    /// <summary>
    /// Bodies are kept head-and-tail rather than head-only: a request's head carries the
    /// model and parameters while its tail carries the newest messages, and both are what
    /// a reader came for.
    /// </summary>
    internal const int HeadLimit = 192 * 1024;

    internal const int TailLimit = 192 * 1024;

    private readonly object _gate = new();
    private readonly BoundedByteCapture _requestBody = new(HeadLimit, TailLimit);
    private readonly BoundedByteCapture _responseBody = new(HeadLimit, TailLimit);
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    private ModelTrafficState _state = ModelTrafficState.InFlight;
    private int? _statusCode;
    private string? _reasonPhrase;
    private IReadOnlyList<KeyValuePair<string, string>> _responseHeaders = [];
    private string? _contentType;
    private bool _responseIsBinary;
    private TimeSpan? _timeToHeaders;
    private TimeSpan? _elapsed;
    private string? _error;

    internal ModelTrafficEntry(
        int id,
        string method,
        Uri? url,
        IReadOnlyList<KeyValuePair<string, string>> requestHeaders,
        byte[] requestBody)
    {
        Id = id;
        StartedAt = DateTimeOffset.Now;
        Method = method;
        Url = ModelTrafficRedaction.SafeUrl(url);
        Host = url?.Host ?? "";
        Path = url?.AbsolutePath ?? "";
        RequestHeaders = requestHeaders;
        _requestBody.Append(requestBody);
        ModelId = ReadModelId(requestBody, Path);
    }

    public int Id { get; }

    public DateTimeOffset StartedAt { get; }

    public string Method { get; }

    public string Url { get; }

    public string Host { get; }

    public string Path { get; }

    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders { get; }

    /// <summary>The model the body or the URL names, when either does.</summary>
    public string? ModelId { get; }

    internal void RecordResponse(HttpResponseMessage response)
    {
        var headers = ModelTrafficRedaction.Headers(response.Headers, response.Content?.Headers);
        var contentType = response.Content?.Headers.ContentType?.MediaType;
        lock (_gate)
        {
            _state = ModelTrafficState.Streaming;
            _statusCode = (int)response.StatusCode;
            _reasonPhrase = response.ReasonPhrase;
            _responseHeaders = headers;
            _contentType = contentType;
            _responseIsBinary = !ModelTrafficRedaction.IsTextMediaType(contentType);
            _timeToHeaders = Stopwatch.GetElapsedTime(_startTimestamp);
        }
    }

    internal void AppendResponse(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            _responseBody.Append(bytes);
        }
    }

    /// <param name="reachedEnd">
    /// True when the reader consumed the stream to EOF; false when it was disposed early,
    /// which is what a stopped turn looks like from here.
    /// </param>
    internal void CompleteBody(bool reachedEnd)
    {
        lock (_gate)
        {
            if (_state is ModelTrafficState.Completed or ModelTrafficState.Interrupted or ModelTrafficState.Failed)
            {
                return;
            }

            _state = reachedEnd ? ModelTrafficState.Completed : ModelTrafficState.Interrupted;
            _elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
        }
    }

    internal void Fail(Exception exception)
    {
        lock (_gate)
        {
            _state = ModelTrafficState.Failed;
            _elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
            _error = exception is OperationCanceledException
                ? "The request was cancelled."
                : $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    public ModelTrafficSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ModelTrafficSnapshot(
                Id,
                StartedAt,
                Method,
                Url,
                Host,
                Path,
                RequestHeaders,
                _requestBody.Text(),
                _requestBody.Truncated,
                _requestBody.TotalBytes,
                ModelId,
                _state,
                _statusCode,
                _reasonPhrase,
                _responseHeaders,
                _responseIsBinary ? _responseBody.HexPreview() : _responseBody.Text(),
                _responseBody.Truncated,
                _responseBody.TotalBytes,
                _contentType,
                _responseIsBinary,
                _timeToHeaders,
                _elapsed ?? (_state is ModelTrafficState.InFlight or ModelTrafficState.Streaming
                    ? Stopwatch.GetElapsedTime(_startTimestamp)
                    : null),
                _error);
        }
    }

    /// <summary>
    /// The model the call is about: the body's own field where a provider sends one,
    /// otherwise the <c>models/{id}</c> segment Gemini and Vertex put in the path.
    /// </summary>
    internal static string? ReadModelId(byte[] requestBody, string path)
    {
        if (requestBody.Length > 0)
        {
            try
            {
                if (JsonNode.Parse(requestBody) is JsonObject body &&
                    body["model"] is JsonValue value &&
                    value.TryGetValue<string>(out var model) &&
                    !string.IsNullOrWhiteSpace(model))
                {
                    return model;
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
            {
                // Not a JSON object body — fall through to the path.
            }
        }

        const string marker = "models/";
        int start = path.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        int end = path.IndexOfAny([':', '/'], start);
        var segment = end < 0 ? path[start..] : path[start..end];
        return segment.Length == 0 ? null : segment;
    }
}

/// <summary>
/// The last few model calls this app made, kept in memory only. Nothing is written to
/// disk: these bodies hold the whole conversation, and a debug pane that quietly
/// persisted it would be a worse leak than the one it exists to diagnose.
/// </summary>
public sealed class ModelTrafficLog
{
    /// <summary>
    /// How many attempts are kept. Small on purpose — each entry can hold two
    /// third-of-a-megabyte bodies, and only the recent ones are ever looked at.
    /// </summary>
    public const int Capacity = 10;

    private readonly object _gate = new();
    private readonly List<ModelTrafficEntry> _entries = [];
    private int _nextId;

    /// <summary>
    /// Raised when an entry is added or changes state — not per received byte, which
    /// would repaint the pane thousands of times per answer.
    /// </summary>
    public event EventHandler? Changed;

    internal ModelTrafficEntry Start(
        string method,
        Uri? url,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        byte[] body)
    {
        ModelTrafficEntry entry;
        lock (_gate)
        {
            entry = new ModelTrafficEntry(++_nextId, method, url, headers, body);
            _entries.Add(entry);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveRange(0, _entries.Count - Capacity);
            }
        }

        Notify();
        return entry;
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ModelTrafficSnapshot> Snapshot()
    {
        ModelTrafficEntry[] entries;
        lock (_gate)
        {
            entries = [.. _entries];
        }

        var snapshots = new ModelTrafficSnapshot[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            snapshots[i] = entries[entries.Length - 1 - i].Snapshot();
        }

        return snapshots;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        Notify();
    }

    internal void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Keeps the first <c>headLimit</c> and the last <c>tailLimit</c> bytes of a stream that
/// may be far larger than either, reporting how much went missing in between.
/// </summary>
internal sealed class BoundedByteCapture(int headLimit, int tailLimit)
{
    private readonly byte[] _head = new byte[headLimit];
    private readonly byte[] _tail = new byte[tailLimit];
    private int _headLength;
    private int _tailStart;
    private int _tailLength;
    private long _total;

    public long TotalBytes => _total;

    public bool Truncated => _total > _headLength + _tailLength;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        _total += bytes.Length;

        if (_headLength < headLimit)
        {
            int take = Math.Min(headLimit - _headLength, bytes.Length);
            bytes[..take].CopyTo(_head.AsSpan(_headLength));
            _headLength += take;
            bytes = bytes[take..];
        }

        if (bytes.Length == 0 || tailLimit == 0)
        {
            return;
        }

        if (bytes.Length >= tailLimit)
        {
            bytes[^tailLimit..].CopyTo(_tail);
            _tailStart = 0;
            _tailLength = tailLimit;
            return;
        }

        int writeAt = (_tailStart + _tailLength) % tailLimit;
        int first = Math.Min(bytes.Length, tailLimit - writeAt);
        bytes[..first].CopyTo(_tail.AsSpan(writeAt));
        if (first < bytes.Length)
        {
            bytes[first..].CopyTo(_tail.AsSpan(0));
        }

        int overflow = _tailLength + bytes.Length - tailLimit;
        if (overflow > 0)
        {
            _tailStart = (_tailStart + overflow) % tailLimit;
            _tailLength = tailLimit;
        }
        else
        {
            _tailLength += bytes.Length;
        }
    }

    public string Text()
    {
        var tail = TailToArray();
        if (!Truncated)
        {
            // Decoded in one pass: a multi-byte character can straddle the head/tail
            // boundary, and decoding the halves separately would corrupt it.
            var all = new byte[_headLength + tail.Length];
            _head.AsSpan(0, _headLength).CopyTo(all);
            tail.CopyTo(all.AsSpan(_headLength));
            return Encoding.UTF8.GetString(all);
        }

        long omitted = _total - _headLength - tail.Length;
        return string.Concat(
            Encoding.UTF8.GetString(_head, 0, _headLength),
            $"\n\n…  {omitted:N0} bytes omitted  …\n\n",
            Encoding.UTF8.GetString(tail));
    }

    /// <summary>What a binary body gets instead of a decode that would be noise.</summary>
    public string HexPreview()
    {
        const int previewBytes = 256;
        int count = Math.Min(_headLength, previewBytes);
        if (count == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            builder.Append(_head[i].ToString("x2"));
            builder.Append(i % 32 == 31 ? '\n' : ' ');
        }

        if (_total > count)
        {
            builder.Append($"\n…  {_total - count:N0} more bytes  …");
        }

        return builder.ToString();
    }

    private byte[] TailToArray()
    {
        var tail = new byte[_tailLength];
        int first = Math.Min(_tailLength, tailLimit - _tailStart);
        _tail.AsSpan(_tailStart, first).CopyTo(tail);
        if (first < _tailLength)
        {
            _tail.AsSpan(0, _tailLength - first).CopyTo(tail.AsSpan(first));
        }

        return tail;
    }
}

/// <summary>
/// Strips credentials before anything reaches the log. Redacting at capture is what makes
/// the pane's Copy buttons safe: the real key never enters the record at all.
/// </summary>
internal static class ModelTrafficRedaction
{
    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "proxy-authorization",
        "x-api-key",
        "api-key",
        "x-goog-api-key",
        "x-amz-security-token",
        "cookie",
        "set-cookie",
    };

    private static readonly HashSet<string> SecretQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "key",
        "api_key",
        "apikey",
        "access_token",
        "token",
        "signature",
        "x-amz-signature",
        "x-amz-credential",
    };

    public static IReadOnlyList<KeyValuePair<string, string>> Headers(
        System.Net.Http.Headers.HttpHeaders? headers,
        System.Net.Http.Headers.HttpHeaders? contentHeaders = null)
    {
        var rows = new List<KeyValuePair<string, string>>();
        Collect(headers, rows);
        Collect(contentHeaders, rows);
        rows.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return rows;
    }

    private static void Collect(
        System.Net.Http.Headers.HttpHeaders? headers, List<KeyValuePair<string, string>> rows)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var (name, values) in headers)
        {
            rows.Add(new(name, Value(name, string.Join(", ", values))));
        }
    }

    /// <summary>
    /// A credential header keeps its scheme and loses its secret — "Bearer ••••••••" says
    /// more about what went out than a wholly blanked line does.
    /// </summary>
    public static string Value(string name, string value)
    {
        if (!SecretHeaders.Contains(name))
        {
            return value;
        }

        int space = value.IndexOf(' ');
        return space > 0
            ? $"{value[..space]} {RequestBodyOverride.RedactedValue}"
            : RequestBodyOverride.RedactedValue;
    }

    /// <summary>The URL with any credential-carrying query parameter blanked.</summary>
    public static string SafeUrl(Uri? url)
    {
        if (url is null)
        {
            return "";
        }

        if (url.Query.Length <= 1)
        {
            return url.ToString();
        }

        var parts = url.Query[1..].Split('&');
        for (int i = 0; i < parts.Length; i++)
        {
            int equals = parts[i].IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(parts[i][..equals]);
            if (SecretQueryKeys.Contains(name))
            {
                parts[i] = $"{parts[i][..equals]}={RequestBodyOverride.RedactedValue}";
            }
        }

        return $"{url.GetLeftPart(UriPartial.Path)}?{string.Join('&', parts)}";
    }

    /// <summary>
    /// Whether a body of this media type is worth decoding as text. Bedrock answers in
    /// <c>vnd.amazon.eventstream</c> binary frames, which as UTF-8 are unreadable noise.
    /// </summary>
    public static bool IsTextMediaType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            // No type at all: a JSON or SSE body with a missing header is far likelier
            // here than a binary one, and a wrong guess only costs formatting.
            return true;
        }

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals("application/x-ndjson", StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}
