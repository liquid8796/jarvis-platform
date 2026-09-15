using System.Collections.Generic;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The wording of the computer-use grant card, as the Code surface draws it
/// (desktop 1.44121.2.0, cd5a31703-DPCARDPv.js).
/// </summary>
public class ComputerUseGrantPromptTests
{
    private static GrantRequest Request(
        IReadOnlyList<GrantRow>? apps = null,
        bool clipboardRead = false,
        bool clipboardWrite = false,
        bool systemKeys = false,
        IReadOnlyList<string>? willHide = null,
        bool autoUnhide = true) =>
        new("", apps ?? [], clipboardRead, clipboardWrite, systemKeys, willHide ?? [], autoUnhide);

    private static GrantRow Row(string app, AppTier tier = AppTier.Full) => new(app, true, false, tier);

    [Fact]
    public void One_app_and_no_flags_names_the_app()
    {
        var title = ComputerUseGrantPrompt.Title(Request([Row("notepad")]));

        Assert.Equal("Allow Jarvis to ", title.Before);
        Assert.Equal("use", title.Bold);
        Assert.Equal(" notepad?", title.After);
    }

    [Fact]
    public void Several_apps_and_no_flags_count_them()
        => Assert.Equal(
            " 3 apps?",
            ComputerUseGrantPrompt.Title(Request([Row("a"), Row("b"), Row("c")])).After);

    [Fact]
    public void An_app_with_a_flag_reads_as_capabilities()
        => Assert.Equal(
            " these capabilities?",
            ComputerUseGrantPrompt.Title(Request([Row("notepad")], clipboardRead: true)).After);

    [Fact]
    public void A_flag_on_its_own_reads_as_capabilities()
        => Assert.Equal(
            " these capabilities?",
            ComputerUseGrantPrompt.Title(Request(systemKeys: true)).After);

    [Fact]
    public void Nothing_at_all_reads_as_your_computer()
    {
        var title = ComputerUseGrantPrompt.Title(Request());

        Assert.Equal("use your computer", title.Bold);
        Assert.Equal("?", title.After);
    }

    [Theory]
    [InlineData("powershell", ComputerUseGrantPrompt.CanRunCommands)]
    [InlineData("pwsh", ComputerUseGrantPrompt.CanRunCommands)]
    [InlineData("cmd", ComputerUseGrantPrompt.CanRunCommands)]
    [InlineData("code", ComputerUseGrantPrompt.CanRunCommands)]
    [InlineData("idea64", ComputerUseGrantPrompt.CanRunCommands)]
    [InlineData("explorer", ComputerUseGrantPrompt.CanAccessFiles)]
    [InlineData("systemsettings", ComputerUseGrantPrompt.CanChangeSettings)]
    public void An_application_that_can_reach_past_itself_is_warned_about(string app, string warning)
        => Assert.Equal(warning, ComputerUseGrantPrompt.SentinelWarning(app));

    [Theory]
    [InlineData("notepad")]
    [InlineData("chrome")]
    [InlineData("acmewriter")]
    public void An_ordinary_application_carries_no_warning(string app)
        => Assert.Null(ComputerUseGrantPrompt.SentinelWarning(app));

    [Fact]
    public void A_path_or_an_extension_still_classifies()
    {
        Assert.Equal(ComputerUseGrantPrompt.CanRunCommands, ComputerUseGrantPrompt.SentinelWarning("powershell.exe"));
        Assert.Equal(
            ComputerUseGrantPrompt.CanAccessFiles,
            ComputerUseGrantPrompt.SentinelWarning(@"C:\Windows\explorer.exe"));
    }

    [Theory]
    [InlineData(AppTier.Read, ComputerUseGrantPrompt.ViewOnly)]
    [InlineData(AppTier.Click, ComputerUseGrantPrompt.ClickOnly)]
    [InlineData(AppTier.Full, ComputerUseGrantPrompt.All)]
    public void Each_tier_has_the_references_label(AppTier tier, string label)
        => Assert.Equal(label, ComputerUseGrantPrompt.TierLabel(tier));

    [Fact]
    public void Nothing_hidden_says_nothing()
        => Assert.Null(ComputerUseGrantPrompt.HideNotice(Request()));

    [Fact]
    public void Hiding_with_auto_unhide_promises_the_windows_back()
        => Assert.Equal(
            ComputerUseGrantPrompt.HiddenThenRestored,
            ComputerUseGrantPrompt.HideNotice(Request(willHide: ["slack"])));

    [Fact]
    public void Hiding_without_auto_unhide_does_not()
        => Assert.Equal(
            ComputerUseGrantPrompt.HiddenWhileWorking,
            ComputerUseGrantPrompt.HideNotice(Request(willHide: ["slack"], autoUnhide: false)));

    [Fact]
    public void The_flag_rows_are_in_the_references_order()
        => Assert.Equal(
            [
                ComputerUseGrantPrompt.ReadClipboard,
                ComputerUseGrantPrompt.WriteClipboard,
                ComputerUseGrantPrompt.SystemShortcuts,
            ],
            ComputerUseGrantPrompt.FlagRows(
                Request(clipboardRead: true, clipboardWrite: true, systemKeys: true)));
}
