using Jarvis.Agent.Core.Execution;

namespace Jarvis.Core.Tests;

public sealed class VerificationResourceGroupTests
{
    [Fact]
    public async Task Owned_fixture_and_probe_cooperate_without_admitting_other_tasks_or_owners()
    {
        using var resources = new ExecutionResourceCoordinator();
        var service = await resources.AcquireGroupAsync("owner", "qa-run", ["*"], true, CancellationToken.None);
        var unrelated = resources.AcquireAsync("owner", ["fs|C:/project"], true, CancellationToken.None);
        var impersonator = resources.AcquireGroupAsync("other-owner", "qa-run", ["*"], true, CancellationToken.None);
        Assert.False(unrelated.IsCompleted);
        Assert.False(impersonator.IsCompleted);
        // The queued unrelated call must not deadlock a probe needed to finish the fixture.
        using (await resources.AcquireGroupAsync("owner", "qa-run", ["*"], true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))) { }
        service.Dispose();
        (await unrelated.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
        (await impersonator.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }
}
