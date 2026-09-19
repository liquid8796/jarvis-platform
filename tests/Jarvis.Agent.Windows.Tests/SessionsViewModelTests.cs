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
    public void Bulk_selection_selects_visible_open_sessions_and_targets_only_checked_rows()
    {
        var a=Session("A"); var b=Session("B"); var c=Session("C");
        var closed=c with { Session=c.Session with { ClosedAt=DateTimeOffset.UtcNow } };
        var calls=new List<(AgentSessionIdentity,bool)>();
        IReadOnlyList<SessionRowViewModel>? confirmed=null;
        var confirmCalls=0;
        var vm=new SessionsViewModel(() => [a,b,closed], () => true, (id,close) => calls.Add((id,close)), rows =>
        {
            confirmCalls++;
            confirmed=rows.ToArray();
            return true;
        });
        vm.Refresh(); vm.ShowClosed=true;
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(2, vm.BulkSelectedCount);
        Assert.False(vm.Items.Single(row=>row.SessionId==closed.Identity.SessionId).IsBulkSelected);

        vm.Items.Single(row=>row.SessionId==b.Identity.SessionId).IsBulkSelected=false;
        vm.StopSelectedSessionsCommand.Execute(null);
        Assert.Equal((a.Identity,false), Assert.Single(calls));
        Assert.Equal(0, vm.BulkSelectedCount);

        calls.Clear();
        vm.Items.Single(row=>row.SessionId==a.Identity.SessionId).IsBulkSelected=true;
        vm.Items.Single(row=>row.SessionId==b.Identity.SessionId).IsBulkSelected=true;
        vm.CloseSelectedSessionsCommand.Execute(null);
        Assert.Equal(1, confirmCalls);
        Assert.NotNull(confirmed); Assert.Equal(2, confirmed!.Count);
        Assert.Contains((a.Identity,true), calls); Assert.Contains((b.Identity,true), calls);
        Assert.DoesNotContain(calls, call=>call.Item1==closed.Identity);
    }
    [Fact]
    public void Filtering_out_a_checked_session_clears_its_bulk_selection()
    {
        var a=Session("A"); var b=Session("B");
        var vm=new SessionsViewModel(() => [a,b], () => true, (_,_)=>{}, _=>true);
        vm.Refresh();
        var rowB=vm.Items.Single(row=>row.SessionId==b.Identity.SessionId);
        rowB.IsBulkSelected=true;
        Assert.Equal(1, vm.BulkSelectedCount);
        vm.Search=a.Identity.SessionId;
        Assert.False(rowB.IsBulkSelected);
        Assert.Equal(0, vm.BulkSelectedCount);
        Assert.False(vm.StopSelectedSessionsCommand.CanExecute(null));
    }
    [Fact]
    public void Disconnected_and_empty_states_do_not_enable_session_mutations()
    {
        var vm = new SessionsViewModel(() => [], () => false, (_,_) => throw new Exception("Must not mutate"), _=>true);
        vm.Refresh();
        Assert.True(vm.IsEmpty); Assert.Contains("Connect", vm.EmptyMessage);
        Assert.False(vm.StopCommand.CanExecute(null)); Assert.False(vm.CloseCommand.CanExecute(null));
        Assert.False(vm.SelectAllCommand.CanExecute(null)); Assert.False(vm.StopSelectedSessionsCommand.CanExecute(null));
        Assert.False(vm.CloseSelectedSessionsCommand.CanExecute(null));
    }
    [Fact]
    public void Filter_and_closed_sessions_have_distinct_empty_state()
    {
        var a=Session("A"); var closed=a with { Session=a.Session with {ClosedAt=DateTimeOffset.UtcNow} };
        var vm=new SessionsViewModel(() => [closed], () => true, (_,_)=>{}, _=>true);
        vm.Refresh(); Assert.Empty(vm.Items);
        vm.ShowClosed=true; Assert.Single(vm.Items);
        vm.Selected=vm.Items[0]; Assert.False(vm.StopCommand.CanExecute(null));
        vm.Items[0].IsBulkSelected=true; Assert.False(vm.Items[0].IsBulkSelected);
        Assert.False(vm.SelectAllCommand.CanExecute(null));
        vm.Search="absent"; Assert.Empty(vm.Items); Assert.Contains("filter", vm.EmptyMessage, StringComparison.OrdinalIgnoreCase);
    }
}
