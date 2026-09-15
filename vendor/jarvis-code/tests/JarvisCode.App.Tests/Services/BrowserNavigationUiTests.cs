using System.IO;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class BrowserNavigationUiTests
{
    [Fact]
    public async Task SuccessfulNavigationDoesNotReportAnError()
    {
        var messages = new List<string>();
        await BrowserNavigationUi.ObserveAsync(() => Task.CompletedTask, messages.Add);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task OnlyProvenSupersededNavigationIsQuiet()
    {
        var messages = new List<string>();
        await BrowserNavigationUi.ObserveAsync(
            () => Task.FromException(new BrowserNavigationSupersededException()), messages.Add);
        Assert.Empty(messages);

        await BrowserNavigationUi.ObserveAsync(
            () => Task.FromException(new InvalidOperationException("ERR_ABORTED (-3) loading 'file:///preview.html'")),
            messages.Add);
        Assert.Contains("interrupted", Assert.Single(messages));
    }

    [Fact]
    public async Task FailedPageReportsLocallyAndItsAwaitedOperationStillFaults()
    {
        var messages = new List<string>();
        var failure = new InvalidOperationException("ERR_FILE_NOT_FOUND (-6) loading 'file:///missing.html'");
        var navigation = Task.FromException(failure);

        await BrowserNavigationUi.ObserveAsync(() => navigation, messages.Add);

        Assert.Contains("ERR_FILE_NOT_FOUND", Assert.Single(messages));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => navigation));
    }

    [Fact]
    public async Task SynchronousStartupFailureIsObservedToo()
    {
        var messages = new List<string>();
        await BrowserNavigationUi.ObserveAsync(() => throw new IOException("engine pipe closed"), messages.Add);
        Assert.Contains("engine pipe closed", Assert.Single(messages));
    }

    [Fact]
    public async Task InterruptedNavigationIsReportedWithoutFaultingTheUiTask()
    {
        var messages = new List<string>();
        await BrowserNavigationUi.ObserveAsync(
            () => Task.FromCanceled(new CancellationToken(canceled: true)), messages.Add);
        Assert.Contains("interrupted", Assert.Single(messages));
    }

    [Fact]
    public async Task SupersededRequestsRemainFailuresForToolCallers()
    {
        var failure = new BrowserNavigationSupersededException();
        Assert.IsAssignableFrom<InvalidOperationException>(failure);
        await Assert.ThrowsAsync<BrowserNavigationSupersededException>(() => Task.FromException(failure));
    }
}
