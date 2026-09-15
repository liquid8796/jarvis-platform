using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class BrowserPaneCdpCancellationTests
{
    [Fact]
    public async Task CancellationAfterKeyDownStillDispatchesKeyUp()
    {
        using var cancellation = new CancellationTokenSource();
        var cdp = new CancelAfterDownCdp(cancellation, "keyDown");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BrowserPaneCdp.PressKeyAsync(cdp, "ArrowRight", 0, null, cancellation.Token));

        Assert.Equal(new[] { "keyDown", "keyUp" }, cdp.Types);
        Assert.False(cdp.ReleaseWasCancelled);
    }

    [Fact]
    public async Task CancellationAfterMouseDownStillDispatchesMouseUp()
    {
        using var cancellation = new CancellationTokenSource();
        var cdp = new CancelAfterDownCdp(cancellation, "mousePressed");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BrowserPaneCdp.MouseClickAsync(cdp, 10, 20, "left", 1, 0, cancellation.Token));

        Assert.Equal(new[] { "mousePressed", "mouseReleased" }, cdp.Types);
        Assert.False(cdp.ReleaseWasCancelled);
    }

    private sealed class CancelAfterDownCdp(CancellationTokenSource cancellation, string cancelOn) : IPaneCdp
    {
        public List<string> Types { get; } = [];
        public bool ReleaseWasCancelled { get; private set; }

        public Task<JsonObject> SendAsync(
            string method,
            JsonObject? parameters,
            CancellationToken cancellationToken = default)
        {
            var type = parameters?["type"]?.GetValue<string>() ?? method;
            Types.Add(type);
            if (type == cancelOn)
            {
                cancellation.Cancel();
            }
            else if (type is "keyUp" or "mouseReleased")
            {
                ReleaseWasCancelled = cancellationToken.IsCancellationRequested;
            }

            return Task.FromResult(new JsonObject());
        }
    }
}
