using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// spawn_task's cwd guard — the reference's <c>Xr</c>. The spawned session
/// inherits this path, so a share that goes away or a path that still has to be
/// resolved would fail later and somewhere the model cannot see.
/// </summary>
public sealed class SpawnTaskCwdTests
{
    [Theory]
    [InlineData(@"C:\Projects\app")]
    [InlineData("/home/user/app")]
    [InlineData(@"D:\a-b.c\d")]
    public void A_plain_absolute_path_is_accepted(string cwd) =>
        Assert.Null(CcdSessionTools.CwdRefusal(cwd));

    [Theory]
    [InlineData(@"\\server\share\app")]
    [InlineData("//server/share/app")]
    public void A_unc_share_is_refused_as_one(string cwd) =>
        Assert.Equal(CcdSessionTools.CwdIsUnc, CcdSessionTools.CwdRefusal(cwd));

    [Fact]
    public void An_automount_root_gets_its_own_sentence() =>
        Assert.Equal(CcdSessionTools.CwdUnderAutomountRoot, CcdSessionTools.CwdRefusal("/net/host/share"));

    [Theory]
    [InlineData(@"C:\Projects\.\app")]
    [InlineData(@"C:\Projects\..\app")]
    [InlineData("/home/user/../app")]
    public void A_dot_segment_is_refused(string cwd) =>
        Assert.Equal(CcdSessionTools.CwdHasDotSegments, CcdSessionTools.CwdRefusal(cwd));

    [Fact]
    public void A_dot_inside_a_name_is_not_a_dot_segment() =>
        Assert.Null(CcdSessionTools.CwdRefusal(@"C:\Projects\my.app\src"));
}
