using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// What the reference's win32 <c>prepareForAction</c> takes off the screen
/// before a session acts, and the note it leaves above the next screenshot.
/// </summary>
public class ComputerUseHideTests
{
    private static UiSettings Granted(string app)
    {
        var settings = new UiSettings { ComputerUseRequireGrants = true };
        var set = ComputerUseSessionGrants.For(settings, "s");
        set.Apps.Add(app);
        return settings;
    }

    [Fact]
    public void A_granted_application_is_left_alone()
        => Assert.False(ComputerUseHide.MayHide(Granted("notepad"), "s", "notepad", "JarvisCode.App"));

    [Fact]
    public void An_ungranted_application_is_hidden()
        => Assert.True(ComputerUseHide.MayHide(Granted("notepad"), "s", "chrome", "JarvisCode.App"));

    [Fact]
    public void The_grant_is_read_for_this_session_only()
        => Assert.True(ComputerUseHide.MayHide(Granted("notepad"), "other", "notepad", "JarvisCode.App"));

    [Fact]
    public void The_shell_is_never_hidden()
        => Assert.False(ComputerUseHide.MayHide(Granted("notepad"), "s", "explorer.exe", "JarvisCode.App"));

    [Theory]
    [InlineData("dwm")]
    [InlineData("winlogon")]
    [InlineData("ApplicationFrameHost")]
    [InlineData("StartMenuExperienceHost")]
    public void A_windows_system_process_is_never_hidden(string app)
        => Assert.False(ComputerUseHide.MayHide(Granted("notepad"), "s", app, "JarvisCode.App"));

    [Fact]
    public void This_application_is_never_hidden()
        => Assert.False(ComputerUseHide.MayHide(Granted("notepad"), "s", "JarvisCode.App", "JarvisCode.App"));

    [Fact]
    public void Nothing_hidden_leaves_no_note()
        => Assert.Null(ComputerUseHide.HiddenNote([], ["notepad"]));

    [Fact]
    public void One_known_application_reads_in_the_singular()
    {
        var note = ComputerUseHide.HiddenNote(["chrome"], ["chrome", "notepad"])!;

        Assert.StartsWith("\"chrome\" was open and got hidden before this screenshot", note);
        Assert.Contains("call request_access to add it.", note);
    }

    [Fact]
    public void Two_known_applications_read_in_the_plural()
    {
        var note = ComputerUseHide.HiddenNote(["chrome", "code"], ["chrome", "code"])!;

        Assert.Contains("\"chrome\", \"code\" were open", note);
        Assert.Contains("add them.", note);
    }

    [Fact]
    public void A_process_the_index_does_not_know_gets_the_other_branch()
    {
        var note = ComputerUseHide.HiddenNote(["soffice.bin"], ["notepad"])!;

        Assert.Contains("This process owns the visible window", note);
        Assert.Contains("Pass the exact basename above to request_access.", note);
        Assert.DoesNotContain("also hidden", note);
    }

    [Fact]
    public void Both_branches_together_say_also()
    {
        var note = ComputerUseHide.HiddenNote(["chrome", "soffice.bin"], ["chrome"])!;

        Assert.Contains("\"chrome\" was open", note);
        Assert.Contains("\"soffice.bin\" was also hidden.", note);
    }

    [Fact]
    public void Preview_and_hide_stand_down_when_grants_are_not_required()
    {
        var off = new UiSettings { ComputerUseRequireGrants = false };

        Assert.Empty(ComputerUseHide.PreviewHideSet(off, "s"));
        Assert.Empty(ComputerUseHide.HideUngranted(off, "s"));
    }

    [Fact]
    public void The_sub_gate_takes_hiding_away_without_taking_the_preview()
    {
        var settings = Granted("notepad");
        settings.ComputerUseHideBeforeAction = false;

        Assert.Empty(ComputerUseHide.HideUngranted(settings, "s"));
    }
}
