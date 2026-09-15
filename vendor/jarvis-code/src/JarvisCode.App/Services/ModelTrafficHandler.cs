using System.IO;
using System.Net.Http;

namespace JarvisCode.App.Services;

/// <summary>
/// Records every call the provider adapters make into a <see cref="ModelTrafficLog"/>.
/// It sits on the HTTP client the providers are built with rather than inside any one
/// adapter, so it costs the <c>JarvisCode.Providers</c> assembly nothing and sees the
/// real wire — including the retries and key rotations <c>ProviderHttp</c> performs, each
/// of which is a call of its own.
/// </summary>
public sealed class ModelTrafficHandler(ModelTrafficLog log, HttpMessageHandler inner)
    : DelegatingHandler(inner)
{
    /// <summary>
    /// A request body larger than this is sent without being recorded. Provider bodies
    /// are built as strings in memory, so the cap only guards against something unusual
    /// being buffered a second time.
    /// </summary>
    private const int MaxBufferedRequestBytes = 64 * 1024 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var entry = await StartEntryAsync(request, cancellationToken);
        if (entry is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            entry.Fail(ex);
            log.Notify();
            throw;
        }

        entry.RecordResponse(response);
        log.Notify();

        if (response.Content is not { } content)
        {
            entry.CompleteBody(reachedEnd: true);
            log.Notify();
            return response;
        }

        try
        {
            // Teed as the caller reads rather than buffered here: these responses are SSE
            // streams the agent loop consumes token by token, and reading one to the end
            // first would turn every answer into a single late blob.
            var stream = await content.ReadAsStreamAsync(cancellationToken);
            var teed = new StreamContent(new ModelTrafficStream(stream, content, entry, log));
            foreach (var (name, values) in content.Headers)
            {
                teed.Headers.TryAddWithoutValidation(name, values);
            }

            response.Content = teed;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpRequestException)
        {
            // The body could not be opened for teeing. The caller still gets the untouched
            // response; only the recording of it is lost, and it says so.
            entry.Fail(ex);
            log.Notify();
        }

        return response;
    }

    private async Task<ModelTrafficEntry?> StartEntryAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var body = Array.Empty<byte>();
            if (request.Content is { } content)
            {
                // Buffers content that is not already re-readable, so reading it for the
                // log cannot consume what is about to be sent.
                await content.LoadIntoBufferAsync(MaxBufferedRequestBytes, cancellationToken);
                body = await content.ReadAsByteArrayAsync(cancellationToken);
            }

            return log.Start(
                request.Method.Method,
                request.RequestUri,
                ModelTrafficRedaction.Headers(request.Headers, request.Content?.Headers),
                body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Capture is a diagnostic: a failure to record must never fail the model call
            // it was recording.
            return null;
        }
    }
}

/// <summary>
/// Passes a response body through to the provider unchanged while copying it into the
/// log. Reaching the end marks the call completed; being disposed first marks it
/// interrupted, which is what a stopped turn looks like from down here.
/// </summary>
internal sealed class ModelTrafficStream(
    Stream inner, HttpContent owner, ModelTrafficEntry entry, ModelTrafficLog log) : Stream
{
    private bool _reachedEnd;
    private bool _settled;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Record(buffer.AsSpan(offset, read), read);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);
        Record(buffer.Span[..read], read);
        return read;
    }

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Settle();
            inner.Dispose();

            // The original content is disposed too: HttpResponseMessage no longer holds it
            // once its Content was swapped, and it owns the connection's own bookkeeping.
            owner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Settle();
        await inner.DisposeAsync();
        owner.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Record(ReadOnlySpan<byte> bytes, int read)
    {
        if (read > 0)
        {
            entry.AppendResponse(bytes);
            return;
        }

        _reachedEnd = true;
        Settle();
    }

    private void Settle()
    {
        if (_settled)
        {
            return;
        }

        _settled = true;
        entry.CompleteBody(_reachedEnd);
        log.Notify();
    }
}
