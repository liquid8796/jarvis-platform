using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Agent.BrowserHost;

namespace Jarvis.Agent.Windows.Tests;

public sealed class BrowserHostLifecycleTests
{
    [Fact]
    public async Task Relay_Exits_WhenBrowserServiceDisconnectsWhileChromeInputIsIdle()
    {
        var pipeName = "JarvisBrowserHostLifecycle-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var stdin = new BlockingReadStream();
        using var stdout = new MemoryStream();

        var relay = Task.Run(() => Program.Relay(stdin, stdout, pipeName));
        await server.WaitForConnectionAsync();
        await server.DisposeAsync();

        try
        {
            await relay.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            stdin.Release();
            await relay.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly ManualResetEventSlim _release = new(false);

        public void Release() => _release.Set();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { _release.Wait(); return 0; }
        public override int Read(Span<byte> buffer) { _release.Wait(); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
                _release.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
