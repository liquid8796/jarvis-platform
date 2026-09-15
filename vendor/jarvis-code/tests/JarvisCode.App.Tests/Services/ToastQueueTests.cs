using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The toast manager's rules, which are the reference's: a six-and-a-half-second
/// default with a six-second floor, a card with an action that never times out,
/// dedupe by text, and a stack that peeks 14px per card.
/// </summary>
public class ToastQueueTests
{
    [Fact]
    public void ADefaultToastCarriesTheReferenceTimeout()
    {
        var queue = new ToastQueue();
        queue.AddSuccess("Path copied to clipboard.");

        Assert.Equal(6500, queue.Toasts[0].TimeoutMs);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(2000, 6000)]
    [InlineData(6000, 6000)]
    [InlineData(9000, 9000)]
    public void ARequestedTimeoutIsRaisedToTheFloor(int requested, int expected) =>
        Assert.Equal(expected, ToastQueue.ClampTimeout(requested));

    [Fact]
    public void AToastWithAnActionNeverTimesItselfOut()
    {
        var queue = new ToastQueue();
        queue.AddWithAction("Unpinned Reviews", "Undo", () => { });

        Assert.Equal(0, queue.Toasts[0].TimeoutMs);
    }

    [Fact]
    public void TheSameTextCountsUpInsteadOfStacking()
    {
        var queue = new ToastQueue();
        queue.AddError("Couldn’t archive this session. Try again.");
        queue.AddError("Couldn’t archive this session. Try again.");
        queue.AddError("Couldn’t archive this session. Try again.");

        Assert.Single(queue.Toasts);
        Assert.Equal("Couldn’t archive this session. Try again. (×3)", queue.Toasts[0].Title);
    }

    [Fact]
    public void AKeyedToastReplacesTheLiveOneCarryingThatKey()
    {
        var queue = new ToastQueue();
        queue.AddSuccess("Creating worktree…", uniqueKey: "worktree");
        queue.AddSuccess("Checking out worktree files… (large repos may take a while)", uniqueKey: "worktree");

        Assert.Single(queue.Toasts);
        Assert.Equal("Checking out worktree files… (large repos may take a while)", queue.Toasts[0].Title);
    }

    [Fact]
    public void TheNewestToastIsTheOneInFront()
    {
        var queue = new ToastQueue();
        queue.AddSuccess("first");
        queue.AddSuccess("second");

        Assert.Equal("second", queue.Front!.Title);
    }

    [Fact]
    public void ClosingRemovesOnlyThatToast()
    {
        var queue = new ToastQueue();
        var first = queue.AddSuccess("first");
        queue.AddSuccess("second");

        queue.Close(first);

        Assert.Single(queue.Toasts);
        Assert.Equal("second", queue.Toasts[0].Title);
    }

    [Fact]
    public void TheStackPeeksAndShrinksTheReferenceAmount()
    {
        Assert.Equal(0, ToastQueue.OffsetFor(0));
        Assert.Equal(-14, ToastQueue.OffsetFor(1));
        Assert.Equal(-28, ToastQueue.OffsetFor(2));
        Assert.Equal(1.0, ToastQueue.ScaleFor(0));
        Assert.Equal(0.96, ToastQueue.ScaleFor(1), 3);
        Assert.True(ToastQueue.IsVisible(2));
        Assert.False(ToastQueue.IsVisible(3));
    }

    [Fact]
    public void OnlyADangerToastIsHighPriority()
    {
        var queue = new ToastQueue();
        queue.AddDanger("danger");
        queue.AddWarning("warning");

        Assert.True(queue.Toasts[0].IsHighPriority);
        Assert.False(queue.Toasts[1].IsHighPriority);
    }
}
