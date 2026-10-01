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
        var vm = new SessionsViewModel(() => entries, () => true, _ => { }, _ => { }, _ => true);
        vm.Refresh(); vm.Selected = vm.Items[0]; var selected = vm.Selected;
        entries=[a with { Activity = new() { RunningCalls=2, QueuedCalls=3 } }, b];
        vm.Refresh();
        Assert.Same(selected, vm.Selected); Assert.Equal(2, vm.RunningCount); Assert.Equal(3, vm.QueuedCount);
        Assert.Equal("Running", vm.Selected!.Status);
    }
    [Fact]
    public void Stop_and_delete_target_only_the_selected_session_and_delete_requires_confirmation()
    {
        var a=Session("A"); var b=Session("B");
        var stopped=new List<AgentSessionIdentity>(); var deleted=new List<AgentSessionIdentity>();
        var confirm=false;
        var vm=new SessionsViewModel(() => [a,b], () => true, stopped.Add, deleted.Add, _ => confirm);
        vm.Refresh(); vm.Selected=vm.Items.Single(s=>s.SessionId==b.Identity.SessionId);
        vm.StopCommand.Execute(null);
        Assert.Equal(b.Identity, Assert.Single(stopped));
        vm.DeleteCommand.Execute(null); Assert.Empty(deleted);
        confirm=true; vm.DeleteCommand.Execute(null);
        Assert.Equal(b.Identity, Assert.Single(deleted));
        Assert.DoesNotContain(a.Identity, stopped);
        Assert.DoesNotContain(a.Identity, deleted);
    }
    [Fact]
    public void Bulk_selection_includes_closed_sessions_stops_only_open_rows_and_deletes_every_checked_row()
    {
        var a=Session("A"); var b=Session("B"); var c=Session("C");
        var closed=c with { Session=c.Session with { ClosedAt=DateTimeOffset.UtcNow } };
        var stopped=new List<AgentSessionIdentity>(); var deleted=new List<AgentSessionIdentity>();
        IReadOnlyList<SessionRowViewModel>? confirmed=null;
        var confirmCalls=0;
        var vm=new SessionsViewModel(() => [a,b,closed], () => true, stopped.Add, deleted.Add, rows =>
        {
            confirmCalls++;
            confirmed=rows.ToArray();
            return true;
        });
        vm.Refresh(); vm.ShowClosed=true;
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(3, vm.BulkSelectedCount);
        Assert.True(vm.Items.Single(row=>row.SessionId==closed.Identity.SessionId).IsBulkSelected);

        vm.Items.Single(row=>row.SessionId==b.Identity.SessionId).IsBulkSelected=false;
        vm.StopSelectedSessionsCommand.Execute(null);
        Assert.Equal(a.Identity, Assert.Single(stopped));
        Assert.Equal(1, vm.BulkSelectedCount);
        Assert.True(vm.Items.Single(row=>row.SessionId==closed.Identity.SessionId).IsBulkSelected);

        stopped.Clear();
        vm.Items.Single(row=>row.SessionId==a.Identity.SessionId).IsBulkSelected=true;
        vm.Items.Single(row=>row.SessionId==b.Identity.SessionId).IsBulkSelected=true;
        vm.DeleteSelectedSessionsCommand.Execute(null);
        Assert.Equal(1, confirmCalls);
        Assert.NotNull(confirmed); Assert.Equal(3, confirmed!.Count);
        Assert.Contains(a.Identity, deleted); Assert.Contains(b.Identity, deleted); Assert.Contains(closed.Identity, deleted);
        Assert.Equal(0, vm.BulkSelectedCount);
    }
    [Fact]
    public void Filtering_out_a_checked_session_clears_its_bulk_selection()
    {
        var a=Session("A"); var b=Session("B");
        var vm=new SessionsViewModel(() => [a,b], () => true, _=>{}, _=>{}, _=>true);
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
        var vm = new SessionsViewModel(() => [], () => false,
            _ => throw new Exception("Must not stop"), _ => throw new Exception("Must not delete"), _=>true);
        vm.Refresh();
        Assert.True(vm.IsEmpty); Assert.Contains("Connect", vm.EmptyMessage);
        Assert.False(vm.StopCommand.CanExecute(null)); Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.SelectAllCommand.CanExecute(null)); Assert.False(vm.StopSelectedSessionsCommand.CanExecute(null));
        Assert.False(vm.DeleteSelectedSessionsCommand.CanExecute(null));
    }
    [Fact]
    public void Filter_and_closed_sessions_have_distinct_empty_state()
    {
        var a=Session("A"); var closed=a with { Session=a.Session with {ClosedAt=DateTimeOffset.UtcNow} };
        var deleted=new List<AgentSessionIdentity>();
        var vm=new SessionsViewModel(() => [closed], () => true, _=>{}, deleted.Add, _=>true);
        vm.Refresh(); Assert.Empty(vm.Items);
        vm.ShowClosed=true; Assert.Single(vm.Items);
        vm.Selected=vm.Items[0]; Assert.False(vm.StopCommand.CanExecute(null)); Assert.True(vm.DeleteCommand.CanExecute(null));
        vm.Items[0].IsBulkSelected=true; Assert.True(vm.Items[0].IsBulkSelected);
        Assert.False(vm.StopSelectedSessionsCommand.CanExecute(null));
        Assert.True(vm.DeleteSelectedSessionsCommand.CanExecute(null));
        vm.DeleteSelectedSessionsCommand.Execute(null); Assert.Equal(closed.Identity, Assert.Single(deleted));
        vm.Search="absent"; Assert.Empty(vm.Items); Assert.Contains("filter", vm.EmptyMessage, StringComparison.OrdinalIgnoreCase);
    }
}
