using System.Linq;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// Where a computer-use grant lives. The reference keeps it on the session
/// (<c>cuAllowedApps</c> / <c>cuGrantFlags</c>, desktop 1.44121.2.0), which is
/// what its own tool description promises when it says "for this session".
/// </summary>
public class ComputerUseSessionGrantsTests
{
    private static UiSettings Settings() => new() { ComputerUseRequireGrants = true };

    private static void Grant(UiSettings settings, string session, string app, AppTier tier = AppTier.Full)
    {
        var set = ComputerUseSessionGrants.For(settings, session);
        set.Apps.Add(app);
        set.Tiers[app] = ComputerUseGrants.TierName(tier);
    }

    [Fact]
    public void A_grant_in_one_session_is_not_a_grant_in_the_next()
    {
        var settings = Settings();
        Grant(settings, "session-a", "notepad");

        Assert.Equal(AppTier.Full, ComputerUseGrants.GrantedTier(settings, "session-a", "notepad"));
        Assert.Null(ComputerUseGrants.GrantedTier(settings, "session-b", "notepad"));
    }

    [Fact]
    public void The_standing_allowlist_applies_to_every_session()
    {
        var settings = Settings();
        settings.ComputerUseGrantedApps.Add("notepad");

        Assert.Equal(AppTier.Full, ComputerUseGrants.GrantedTier(settings, "session-a", "notepad"));
        Assert.Equal(AppTier.Full, ComputerUseGrants.GrantedTier(settings, "session-b", "notepad"));
    }

    [Fact]
    public void A_sessions_tier_is_the_one_it_was_granted_at()
    {
        var settings = Settings();
        Grant(settings, "s", "chrome", AppTier.Read);

        Assert.Equal(AppTier.Read, ComputerUseGrants.GrantedTier(settings, "s", "chrome"));
    }

    [Fact]
    public void A_name_arrives_normalised_so_notepad_exe_matches_notepad()
    {
        var settings = Settings();
        Grant(settings, "s", "notepad");

        Assert.Equal(AppTier.Full, ComputerUseGrants.GrantedTier(settings, "s", "notepad.exe"));
        Assert.Equal(AppTier.Full, ComputerUseGrants.GrantedTier(settings, "s", @"C:\Windows\notepad.exe"));
    }

    [Fact]
    public void Revoking_takes_one_app_from_one_session_only()
    {
        var settings = Settings();
        Grant(settings, "a", "notepad");
        Grant(settings, "b", "notepad");

        Assert.True(ComputerUseSessionGrants.Revoke(settings, "a", "notepad.exe"));
        Assert.Null(ComputerUseGrants.GrantedTier(settings, "a", "notepad"));
        Assert.Equal(AppTier.Full, ComputerUseGrants.GrantedTier(settings, "b", "notepad"));
        Assert.False(ComputerUseSessionGrants.Revoke(settings, "a", "notepad"));
    }

    [Fact]
    public void The_listing_carries_the_sessions_grants_ahead_of_the_standing_ones()
    {
        var settings = Settings();
        settings.ComputerUseGrantedApps.Add("explorer");
        Grant(settings, "s", "notepad");

        var granted = ComputerUseGrants.Granted(settings, "s").Select(static row => row.App).ToList();

        Assert.Equal(["notepad", "explorer"], granted);
    }

    [Fact]
    public void An_app_in_both_lists_is_listed_once()
    {
        var settings = Settings();
        settings.ComputerUseGrantedApps.Add("notepad");
        Grant(settings, "s", "notepad.exe", AppTier.Click);

        var granted = ComputerUseGrants.Granted(settings, "s");

        Assert.Single(granted);
        Assert.Equal(AppTier.Click, granted[0].Tier);
    }

    [Fact]
    public void A_clipboard_flag_is_the_sessions_own()
    {
        var settings = Settings();
        ComputerUseSessionGrants.For(settings, "a").ClipboardRead = true;

        Assert.Null(ComputerUseGrants.ClipboardRefusal(settings, "a", read: true));
        Assert.NotNull(ComputerUseGrants.ClipboardRefusal(settings, "b", read: true));
        Assert.NotNull(ComputerUseGrants.ClipboardRefusal(settings, "a", read: false));
    }

    [Fact]
    public void Only_the_hundred_most_recent_sessions_keep_a_set()
    {
        var settings = Settings();
        for (var i = 0; i < ComputerUseSessionGrants.MaxSessions + 5; i++)
        {
            ComputerUseSessionGrants.For(settings, $"session-{i}");
        }

        Assert.Equal(ComputerUseSessionGrants.MaxSessions, settings.ComputerUseGrantsBySession.Count);
        Assert.Null(ComputerUseSessionGrants.Peek(settings, "session-0"));
        Assert.NotNull(ComputerUseSessionGrants.Peek(settings, "session-104"));
    }

    [Fact]
    public void The_first_request_warning_is_the_reference_text_for_the_category_that_earned_it()
    {
        Assert.Equal(
            ComputerUseGrantResults.BrowserFirstRequest,
            ComputerUseGrantResults.FirstRequestWarning(["browser"]));
        Assert.Equal(
            ComputerUseGrantResults.TerminalFirstRequest,
            ComputerUseGrantResults.FirstRequestWarning(["terminal"]));
        Assert.Equal(
            ComputerUseGrantResults.BrowserFirstRequest + "\n\n" + ComputerUseGrantResults.TerminalFirstRequest,
            ComputerUseGrantResults.FirstRequestWarning(["browser", "terminal"]));
    }

    [Fact]
    public void Both_first_request_warnings_say_the_retry_belongs_in_this_turn()
    {
        // The reference's `Zt` is what makes the refusal a confirmation step
        // rather than a block, so both categories have to carry it.
        Assert.Contains("in THIS SAME turn", ComputerUseGrantResults.BrowserFirstRequest);
        Assert.Contains("in THIS SAME turn", ComputerUseGrantResults.TerminalFirstRequest);
    }
}
