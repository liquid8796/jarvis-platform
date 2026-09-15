using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class UpdateStateMachineTests
{
    [Fact]
    public void StartsIdleAndNotInFlight()
    {
        var machine = new UpdateStateMachine();

        Assert.Equal(UpdateStatus.Idle, machine.State.Status);
        Assert.False(machine.IsCheckInFlight);
    }

    [Fact]
    public void AutomaticCheckRunsThroughToReady()
    {
        var machine = new UpdateStateMachine();
        var seen = new List<UpdateStatus>();
        machine.Changed += state => seen.Add(state.Status);

        machine.OnCheckingForUpdate();
        Assert.True(machine.IsCheckInFlight);
        Assert.False(machine.State.IsManualCheck);

        machine.OnUpdateAvailable();
        Assert.Equal(UpdateStatus.Downloading, machine.State.Status);
        Assert.True(machine.IsCheckInFlight);

        machine.OnUpdateDownloaded("1.4.2");
        Assert.Equal(new UpdateState(UpdateStatus.Ready, Version: "1.4.2"), machine.State);
        Assert.False(machine.IsCheckInFlight);

        Assert.Equal(
            [UpdateStatus.Checking, UpdateStatus.Downloading, UpdateStatus.Ready],
            seen);
    }

    [Fact]
    public void ManualFlagRidesTheCheckAndTheDownloadThatFollowsIt()
    {
        var machine = new UpdateStateMachine();

        Assert.False(machine.SetManualCheck());
        machine.OnCheckingForUpdate();
        Assert.True(machine.State.IsManualCheck);

        // The reference clears its own flag when the download starts but copies it
        // onto the state, so the notification still knows a person asked.
        machine.OnUpdateAvailable();
        Assert.True(machine.State.IsManualCheck);

        machine.OnUpdateDownloaded("2.0.0");
        Assert.False(machine.State.IsManualCheck);
    }

    [Fact]
    public void ManualCheckDuringAnAutomaticRunRestampsItInPlace()
    {
        var machine = new UpdateStateMachine();
        machine.OnCheckingForUpdate();
        var changes = 0;
        machine.Changed += _ => changes++;

        Assert.False(machine.SetManualCheck());

        Assert.Equal(UpdateStatus.Checking, machine.State.Status);
        Assert.True(machine.State.IsManualCheck);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ManualCheckDuringAManualRunChangesNothing()
    {
        var machine = new UpdateStateMachine();
        machine.SetManualCheck();
        machine.OnCheckingForUpdate();
        var changes = 0;
        machine.Changed += _ => changes++;

        Assert.False(machine.SetManualCheck());

        Assert.Equal(0, changes);
    }

    [Fact]
    public void ManualCheckWithAnUpdateAlreadyStagedAsksToAnnounceItAgain()
    {
        var machine = new UpdateStateMachine();
        machine.OnCheckingForUpdate();
        machine.OnUpdateAvailable();
        machine.OnUpdateDownloaded("3.1.0");

        Assert.True(machine.SetManualCheck());
        Assert.Equal(UpdateStatus.Ready, machine.State.Status);
    }

    [Fact]
    public void NoUpdateReturnsToIdleAndClearsTheManualFlag()
    {
        var machine = new UpdateStateMachine();
        machine.SetManualCheck();
        machine.OnCheckingForUpdate();

        machine.OnUpdateNotAvailable();

        Assert.Equal(UpdateState.Idle, machine.State);

        // The next automatic check must not inherit the answered manual flag.
        machine.OnCheckingForUpdate();
        Assert.False(machine.State.IsManualCheck);
    }

    [Fact]
    public void ErrorEndsTheRunAndKeepsTheMessage()
    {
        var machine = new UpdateStateMachine();
        machine.SetManualCheck();
        machine.OnCheckingForUpdate();

        machine.OnError("net::ERR_CONNECTION_REFUSED");

        Assert.Equal(UpdateStatus.Error, machine.State.Status);
        Assert.Equal("net::ERR_CONNECTION_REFUSED", machine.State.Error);
        Assert.False(machine.IsCheckInFlight);

        machine.OnCheckingForUpdate();
        Assert.False(machine.State.IsManualCheck);
    }
}

public class UpdateMenuLabelsTests
{
    [Fact]
    public void IdleOffersTheCheck() =>
        Assert.Equal(
            [new UpdateMenuRow("Check for Updates…", Enabled: true)],
            UpdateMenuLabels.For(UpdateState.Idle));

    [Fact]
    public void BusyStatesReportProgressAndCannotBeClicked()
    {
        Assert.Equal(
            [new UpdateMenuRow("Checking for Updates…", Enabled: false)],
            UpdateMenuLabels.For(new UpdateState(UpdateStatus.Checking)));
        Assert.Equal(
            [new UpdateMenuRow("Downloading Update…", Enabled: false)],
            UpdateMenuLabels.For(new UpdateState(UpdateStatus.Downloading)));
    }

    [Fact]
    public void ReadyNamesTheVersion() =>
        Assert.Equal(
            [new UpdateMenuRow("Restart to update to 1.9.0", Enabled: true)],
            UpdateMenuLabels.For(new UpdateState(UpdateStatus.Ready, Version: "1.9.0")));

    [Fact]
    public void ErrorKeepsTheCheckAndAddsADisabledNotice() =>
        Assert.Equal(
            [
                new UpdateMenuRow("Check for Updates…", Enabled: true),
                new UpdateMenuRow("Last Update Attempt Failed", Enabled: false),
            ],
            UpdateMenuLabels.For(new UpdateState(UpdateStatus.Error, Error: "boom")));
}

public class UpdateCardTests
{
    [Fact]
    public void IdleDrawsNothing() =>
        Assert.Equal(UpdateCardKind.None, UpdateCard.For(UpdateState.Idle).Kind);

    [Fact]
    public void BusyStatesAreOneLineAndNotClickable()
    {
        var checking = UpdateCard.For(new UpdateState(UpdateStatus.Checking));
        Assert.Equal("Checking for updates...", checking.Title);
        Assert.Null(checking.Detail);
        Assert.False(checking.Clickable);

        var downloading = UpdateCard.For(new UpdateState(UpdateStatus.Downloading));
        Assert.Equal("Downloading update...", downloading.Title);
        Assert.False(downloading.Clickable);
    }

    [Fact]
    public void ReadyOffersTheRelaunchWithTheStagedVersion()
    {
        var card = UpdateCard.For(new UpdateState(UpdateStatus.Ready, Version: "1.2.3"));

        Assert.Equal(UpdateCardKind.Ready, card.Kind);
        Assert.Equal("Relaunch to update", card.Title);
        Assert.Equal("v1.2.3", card.Detail);
        Assert.True(card.Clickable);
    }

    [Fact]
    public void ReadyWithoutAVersionShowsNoSecondLine() =>
        Assert.Null(UpdateCard.For(new UpdateState(UpdateStatus.Ready)).Detail);

    [Fact]
    public void ErrorSaysTheUpdateDidNotComplete()
    {
        var card = UpdateCard.For(new UpdateState(UpdateStatus.Error, Error: "x"));

        Assert.Equal(UpdateCardKind.Failed, card.Kind);
        Assert.Equal("Update didn’t complete", card.Title);
        Assert.Equal("Please quit and reopen Jarvis", card.Detail);
        Assert.True(card.Clickable);
    }
}

public class RelaunchWhileWorkingTests
{
    [Fact]
    public void OneNamedSessionIsNamed() =>
        Assert.Equal(
            "Jarvis is working in Refactor the parser. Relaunching now will interrupt that work.",
            RelaunchWhileWorking.Detail(1, "Refactor the parser"));

    [Fact]
    public void OneUnnamedSessionFallsBackToTheReferencePlaceholder() =>
        Assert.Equal(
            "Jarvis is working in Untitled session. Relaunching now will interrupt that work.",
            RelaunchWhileWorking.Detail(1, ""));

    [Fact]
    public void OneSessionThatCouldNotBeResolvedIsCountedInstead() =>
        Assert.Equal(
            "Jarvis is working in 1 session. Relaunching now will interrupt that work.",
            RelaunchWhileWorking.Detail(1, null));

    [Fact]
    public void SeveralSessionsAreCounted() =>
        Assert.Equal(
            "Jarvis is working in 3 sessions. Relaunching now will interrupt that work.",
            RelaunchWhileWorking.Detail(3, "ignored"));
}
