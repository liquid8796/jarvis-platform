using Jarvis.Agent.Desktop.ViewModels;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class SessionsViewModelTests
{
    private static LocalSessionOverview Session(string label)
    {
        var id = new AgentSessionIdentity("owner", "device", AgentSessionRules.NewSessionId());
        return new(id, new(id.SessionId, id.DeviceId, label, "", [], 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, 0), new());
    }
    [Fact]
    public void Refresh_keeps_row_identity_selection_and_reports_live_counts()
    {
        var a=Session("A"); var b=Session("B");
        IReadOnlyList<LocalSessionOverview> entries=[a,b];
        var vm = new SessionsViewModel(() => entries, () => true, (_, _) => {}, _ => true);
        vm.Refresh(); vm.Selected = vm.Items[0]; var selected = vm.Selected;
        entries=[a with { Activity = new() { RunningCalls=2, QueuedCalls=3 } }, b];
        vm.Refresh();
        Assert.Same(selected, vm.Selected); Assert.Equal(2, vm.RunningCount); Assert.Equal(3, vm.QueuedCount);
        Assert.Equal("Running", vm.Selected!.Status);
    }
    [Fact]
    public void Stop_and_close_target_only_the_selected_session_and_close_requires_confirmation()
    {
        var a=Session("A"); var b=Session("B"); var calls=new List<(AgentSessionIdentity,bool)>();
        var confirm=false;
        var vm=new SessionsViewModel(() => [a,b], () => true, (id,close) => calls.Add((id,close)), _ => confirm);
        vm.Refresh(); vm.Selected=vm.Items.Single(s=>s.SessionId==b.Identity.SessionId);
        vm.StopCommand.Execute(null);
        Assert.Equal((b.Identity,false), Assert.Single(calls));
        vm.CloseCommand.Execute(null); Assert.Single(calls);
        confirm=true; vm.CloseCommand.Execute(null);
        Assert.Equal((b.Identity,true), calls[1]);
        Assert.DoesNotContain(calls, c=>c.Item1==a.Identity);
    }
    [Fact]
    public void Disconnected_and_empty_states_do_not_enable_session_mutations()
    {
        var vm = new SessionsViewModel(() => [], () => false, (_,_) => throw new Exception("Must not mutate"), _=>true);
        vm.Refresh();
        Assert.True(vm.IsEmpty); Assert.Contains("Connect", vm.EmptyMessage);
        Assert.False(vm.StopCommand.CanExecute(null)); Assert.False(vm.CloseCommand.CanExecute(null));
    }
    [Fact]
    public void Filter_and_closed_sessions_have_distinct_empty_state()
    {
        var a=Session("A"); var closed=a with { Session=a.Session with {ClosedAt=DateTimeOffset.UtcNow} };
        var vm=new SessionsViewModel(() => [closed], () => true, (_,_)=>{}, _=>true);
        vm.Refresh(); Assert.Empty(vm.Items);
        vm.ShowClosed=true; Assert.Single(vm.Items);
        vm.Selected=vm.Items[0]; Assert.False(vm.StopCommand.CanExecute(null));
        vm.Search="absent"; Assert.Empty(vm.Items); Assert.Contains("filter", vm.EmptyMessage, StringComparison.OrdinalIgnoreCase);
    }
}
