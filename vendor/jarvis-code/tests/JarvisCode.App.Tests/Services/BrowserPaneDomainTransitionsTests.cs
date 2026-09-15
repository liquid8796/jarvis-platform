using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The pane's domain-transition consent, against the reference's own ladder:
/// what counts as the same site, what makes a transition need consent at all,
/// and which of the five sentences each outcome answers with.
/// </summary>
public class BrowserPaneDomainTransitionsTests
{
    private static BrowserPaneDomainTransitions Gate(params string[] allowed) =>
        new(() => allowed);

    private static DomainTransitionCard Card(bool? answer) => (_, _, _) => Task.FromResult(answer);

    // ---- normalising an address to the origin the policy reasons about ----------

    [Theory]
    [InlineData("https://example.com/a/b?q=1", "https://example.com")]
    [InlineData("https://www.example.com/", "https://example.com")]
    [InlineData("https://example.com./", "https://example.com")]
    [InlineData("http://example.com:8080/x", "http://example.com:8080")]
    [InlineData("https://EXAMPLE.com", "https://example.com")]
    public void An_address_reduces_to_its_origin(string url, string expected) =>
        Assert.Equal(expected, BrowserPaneDomainTransitions.NormalizeOrigin(url));

    [Theory]
    // "www." only comes off a host of three or more labels, so this two-label
    // host keeps it — the reference's KM, and the reason www.com is not com.
    [InlineData("https://www.com/")]
    [InlineData("https://www.site.local/")]
    public void The_www_fold_leaves_the_reference_s_exceptions_alone(string url) =>
        Assert.Equal(url.TrimEnd('/'), BrowserPaneDomainTransitions.NormalizeOrigin(url));

    [Theory]
    [InlineData("file:///c:/tmp/page.html")]
    [InlineData("about:blank")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_web_page_has_no_origin(string? url) =>
        Assert.Null(BrowserPaneDomainTransitions.NormalizeOrigin(url));

    [Fact]
    public void An_origin_s_scheme_twin_is_the_other_scheme() =>
        Assert.Equal("http://example.com", BrowserPaneDomainTransitions.SchemeTwin("https://example.com"));

    // ---- what the tab remembers it came from -----------------------------------

    [Fact]
    public void A_committed_web_page_becomes_the_tab_s_last_external_origin() =>
        Assert.Equal("https://example.com",
            BrowserPaneDomainTransitions.CommittedOrigin("https://example.com/page"));

    [Theory]
    [InlineData("http://localhost:5173/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://app.localhost:3000/")]
    [InlineData("file:///c:/tmp/page.html")]
    public void A_dev_server_or_a_file_is_not_an_external_origin(string url) =>
        Assert.Null(BrowserPaneDomainTransitions.CommittedOrigin(url));

    // ---- what counts as a transition -------------------------------------------

    [Fact]
    public void A_tab_that_has_committed_nothing_external_yet_is_not_transitioning() =>
        Assert.Null(Gate().Transition(null, "https://example.com/"));

    [Fact]
    public void Staying_on_the_same_site_is_not_a_transition() =>
        Assert.Null(Gate().Transition("https://example.com", "https://example.com/other/page"));

    [Fact]
    public void Changing_only_the_scheme_is_not_a_transition() =>
        Assert.Null(Gate().Transition("http://example.com", "https://example.com/"));

    [Fact]
    public void A_different_port_is_a_different_origin() =>
        Assert.Equal(("http://example.com:8080", "http://example.com:9090"),
            Gate().Transition("http://example.com:8080", "http://example.com:9090"));

    [Fact]
    public void Moving_to_another_site_is_a_transition() =>
        Assert.Equal(("https://example.com", "https://other.test"),
            Gate().Transition("https://example.com", "https://other.test/page"));

    [Fact]
    public void A_pair_already_allowed_is_no_longer_a_transition()
    {
        var gate = Gate();
        gate.Admit("https://example.com", "https://other.test");

        Assert.Null(gate.Transition("https://example.com", "https://other.test/page"));
    }

    [Fact]
    public void An_allowed_pair_is_remembered_one_way_round()
    {
        var gate = Gate();
        gate.Admit("https://example.com", "https://other.test");

        Assert.Equal(("https://other.test", "https://example.com"),
            gate.Transition("https://other.test", "https://example.com/"));
    }

    // ---- the precondition: both sides are origins the pane may act on ----------

    [Fact]
    public void An_origin_the_user_listed_is_granted() =>
        Assert.True(Gate("https://example.com").IsGranted("https://example.com"));

    [Fact]
    public void An_https_origin_is_covered_by_a_grant_of_its_http_twin() =>
        Assert.True(Gate("http://example.com").IsGranted("https://example.com"));

    [Fact]
    public void An_http_origin_is_not_covered_by_a_grant_of_its_https_twin() =>
        Assert.False(Gate("https://example.com").IsGranted("http://example.com"));

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.2:80")]
    [InlineData("http://[::1]")]
    public void A_loopback_origin_is_never_granted(string origin) =>
        Assert.False(Gate(origin).IsGranted(origin));

    [Fact]
    public void A_listed_site_is_matched_after_the_same_normalisation()
    {
        // Settings stores what the user typed, reduced to its authority — so a
        // listed "www." host has to be folded the way every other address is
        // before the two can be compared at all.
        Assert.True(Gate("https://www.example.com").IsGranted("https://example.com"));
        Assert.True(Gate("https://example.com/").IsGranted("https://example.com"));
    }

    [Fact]
    public void A_listed_entry_that_is_not_a_web_origin_grants_nothing() =>
        Assert.False(Gate("not a url", "file:///c:/tmp").IsGranted("https://example.com"));

    [Fact]
    public async Task A_transition_between_sites_the_pane_may_not_act_on_needs_no_consent()
    {
        var asked = false;
        var gate = Gate("https://example.com");

        var outcome = await gate.DecideAsync(
            "https://example.com", "https://other.test/",
            (_, _, _) => { asked = true; return Task.FromResult<bool?>(true); }, null, default);

        Assert.Equal(DomainTransitionOutcome.NotRequired, outcome);
        Assert.False(asked);
    }

    // ---- the outcomes ----------------------------------------------------------

    private const string Src = "https://example.com";
    private const string Dest = "https://other.test";

    private static BrowserPaneDomainTransitions Both() => Gate(Src, Dest);

    [Fact]
    public async Task Allowing_admits_the_pair_so_the_next_move_asks_nothing()
    {
        var gate = Both();

        Assert.Equal(DomainTransitionOutcome.Allowed,
            await gate.DecideAsync(Src, Dest + "/a", Card(true), null, default));
        Assert.Equal(DomainTransitionOutcome.NotRequired,
            await gate.DecideAsync(Src, Dest + "/b", Card(false), null, default));
    }

    [Fact]
    public async Task Declining_answers_with_the_reference_s_declined_sentence()
    {
        var outcome = await Both().DecideAsync(Src, Dest, Card(false), null, default);

        Assert.Equal(DomainTransitionOutcome.Denied, outcome);
        Assert.Equal(
            "The user declined this domain transition. Do not retry — ask what they'd like to do instead.",
            BrowserPaneDomainTransitions.Refusal(outcome));
    }

    [Fact]
    public async Task A_third_decline_of_one_pair_suppresses_the_card()
    {
        var gate = Both();
        var shown = 0;
        Task<bool?> Deny(string a, string b, CancellationToken c)
        {
            shown++;
            return Task.FromResult<bool?>(false);
        }

        for (var i = 0; i < BrowserPaneDomainTransitions.DeclinesPerPair; i++)
        {
            Assert.Equal(DomainTransitionOutcome.Denied, await gate.DecideAsync(Src, Dest, Deny, null, default));
        }

        Assert.Equal(DomainTransitionOutcome.Suppressed, await gate.DecideAsync(Src, Dest, Deny, null, default));
        Assert.Equal(BrowserPaneDomainTransitions.DeclinesPerPair, shown);
    }

    [Fact]
    public async Task Declines_across_the_session_suppress_every_pair()
    {
        // Three pairs declined to their own cap reach the session cap of nine,
        // so a fourth pair is suppressed without ever being shown.
        string[] sites = ["https://a.test", "https://b.test", "https://c.test", "https://d.test"];
        var gate = new BrowserPaneDomainTransitions(() => [Src, .. sites]);

        foreach (var site in sites[..3])
        {
            for (var i = 0; i < BrowserPaneDomainTransitions.DeclinesPerPair; i++)
            {
                Assert.Equal(DomainTransitionOutcome.Denied,
                    await gate.DecideAsync(Src, site, Card(false), null, default));
            }
        }

        var shown = false;
        var outcome = await gate.DecideAsync(Src, sites[3],
            (_, _, _) => { shown = true; return Task.FromResult<bool?>(true); }, null, default);

        Assert.Equal(DomainTransitionOutcome.Suppressed, outcome);
        Assert.False(shown);
    }

    [Fact]
    public async Task Declines_are_counted_against_the_pair_that_earned_them()
    {
        var gate = new BrowserPaneDomainTransitions(() => [Src, Dest, "https://third.test"]);
        for (var i = 0; i < BrowserPaneDomainTransitions.DeclinesPerPair; i++)
        {
            await gate.DecideAsync(Src, Dest, Card(false), null, default);
        }

        Assert.Equal(DomainTransitionOutcome.Suppressed,
            await gate.DecideAsync(Src, Dest, Card(false), null, default));
        Assert.Equal(DomainTransitionOutcome.Allowed,
            await gate.DecideAsync(Src, "https://third.test", Card(true), null, default));
    }

    [Fact]
    public async Task With_nowhere_to_show_the_card_the_navigation_is_refused()
    {
        var outcome = await Both().DecideAsync(Src, Dest, card: null, null, default);

        Assert.Equal(DomainTransitionOutcome.Refused, outcome);
        Assert.Equal(BrowserPaneDomainTransitions.UnavailableRefusal,
            BrowserPaneDomainTransitions.Refusal(outcome));
    }

    [Fact]
    public async Task A_card_that_answers_nothing_is_refused_rather_than_denied() =>
        Assert.Equal(DomainTransitionOutcome.Refused,
            await Both().DecideAsync(Src, Dest, Card(null), null, default));

    [Fact]
    public async Task A_tab_that_moved_while_the_card_was_up_asks_for_a_retry()
    {
        var outcome = await Both().DecideAsync(
            Src, Dest, Card(true), _ => Task.FromResult(false), default);

        Assert.Equal(DomainTransitionOutcome.Retry, outcome);
        Assert.Equal(BrowserPaneDomainTransitions.RetryRefusal,
            BrowserPaneDomainTransitions.Refusal(outcome));
    }

    [Fact]
    public async Task An_answer_about_a_page_the_tab_has_left_does_not_admit_the_pair()
    {
        var gate = Both();
        await gate.DecideAsync(Src, Dest, Card(true), _ => Task.FromResult(false), default);

        Assert.False(gate.IsAdmitted(Src, Dest));
    }

    [Fact]
    public async Task A_second_card_cannot_go_up_while_one_is_open()
    {
        var gate = Gate(Src, Dest, "https://third.test");
        var release = new TaskCompletionSource<bool?>();
        DomainTransitionOutcome? nested = null;

        var first = gate.DecideAsync(Src, Dest, async (_, _, _) =>
        {
            nested = await gate.DecideAsync(Src, "https://third.test", Card(true), null, default);
            return await release.Task;
        }, null, default);

        release.SetResult(true);
        Assert.Equal(DomainTransitionOutcome.Allowed, await first);
        Assert.Equal(DomainTransitionOutcome.Retry, nested);
    }

    [Fact]
    public async Task Resetting_the_pane_forgets_what_the_session_decided()
    {
        var gate = Both();
        await gate.DecideAsync(Src, Dest, Card(true), null, default);
        gate.Reset();

        Assert.False(gate.IsAdmitted(Src, Dest));
        Assert.Equal(DomainTransitionOutcome.Denied,
            await gate.DecideAsync(Src, Dest, Card(false), null, default));
    }

    // ---- the sentence each outcome answers with --------------------------------

    [Fact]
    public void An_outcome_that_lets_the_navigation_run_answers_nothing()
    {
        Assert.Null(BrowserPaneDomainTransitions.Refusal(DomainTransitionOutcome.NotRequired));
        Assert.Null(BrowserPaneDomainTransitions.Refusal(DomainTransitionOutcome.Allowed));
    }

    [Fact]
    public void Suppression_answers_with_the_reference_s_own_sentence() =>
        Assert.Equal(
            "The user has repeatedly declined this domain transition, so the prompt is suppressed and the " +
            "navigation was not performed. Do not retry; the user can navigate there manually if they want.",
            BrowserPaneDomainTransitions.Refusal(DomainTransitionOutcome.Suppressed));

    [Fact]
    public void An_outcome_the_ladder_does_not_name_answers_the_unconfirmed_sentence() =>
        Assert.Equal(
            "The domain transition could not be confirmed, so the navigation was not performed.",
            BrowserPaneDomainTransitions.Refusal((DomainTransitionOutcome)99));

    [Fact]
    public void A_remembered_pair_is_keyed_the_way_the_reference_writes_it() =>
        Assert.Equal("https://example.com\u2192https://other.test",
            BrowserPaneDomainTransitions.PairKey(Src, Dest));

    // ---- navigate, which is where the gate actually runs ------------------------

    private static (BrowserPaneHandlers Handlers, StubPaneDriver Driver) Navigator(
        BrowserPaneDomainTransitions? gate)
    {
        var driver = new StubPaneDriver { LastExternalOrigin = Src };
        return (new BrowserPaneHandlers(driver, gate), driver);
    }

    private static JsonObject Url(string url) => new() { ["url"] = url };

    [Fact]
    public async Task Navigate_does_not_ask_because_the_shipped_reference_does_not()
    {
        // BZt in desktop 1.44121.2.0 is `return "allowed"`, so no navigation in
        // that build reaches a card. This pane matches it: the transition is
        // computed, found, and allowed anyway.
        var asked = false;
        var (handlers, driver) = Navigator(Both());

        var result = await handlers.NavigateAsync(
            Url(Dest + "/page"),
            (_, _, _) => { asked = true; return Task.FromResult<bool?>(false); },
            default);

        Assert.False(result.IsError);
        Assert.False(asked);
        Assert.Contains(driver.Calls, c => c.StartsWith("navigate:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_turn_with_nowhere_to_ask_navigates_rather_than_refusing()
    {
        // Nothing to show a card in - a batch step, a subagent, a headless run -
        // is not a refusal either, for the same reason.
        var (handlers, driver) = Navigator(Both());

        var result = await handlers.NavigateAsync(Url(Dest), card: null, default);

        Assert.False(result.IsError);
        Assert.Contains(driver.Calls, c => c.StartsWith("navigate:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_shipped_request_allows_whatever_it_is_given()
    {
        // The stub answers before it looks at anything, so an ungranted pair,
        // a declined card and a stale tab all come back the same.
        var gate = Both();
        var asked = false;
        DomainTransitionCard deny = (_, _, _) => { asked = true; return Task.FromResult<bool?>(false); };

        Assert.Equal(
            DomainTransitionOutcome.Allowed,
            await gate.RequestAsync(Src, Dest + "/page", deny, _ => Task.FromResult(false), default));
        Assert.Equal(
            DomainTransitionOutcome.Allowed,
            await gate.RequestAsync(null, null, null, null, default));
        Assert.False(asked);
    }

    [Fact]
    public async Task An_allowed_transition_navigates()
    {
        var (handlers, driver) = Navigator(Both());
        driver.Navigation = new PaneNavigation("to", Dest);

        var result = await handlers.NavigateAsync(Url(Dest + "/page"), Card(true), default);

        Assert.False(result.IsError);
        Assert.Contains(driver.Calls, c => c.StartsWith("navigate:"));
    }

    [Fact]
    public async Task Navigating_within_the_same_site_is_never_asked_about()
    {
        var (handlers, driver) = Navigator(Both());
        driver.Navigation = new PaneNavigation("to", Src);
        var asked = false;

        var result = await handlers.NavigateAsync(Url(Src + "/elsewhere"),
            (_, _, _) => { asked = true; return Task.FromResult<bool?>(false); }, default);

        Assert.False(result.IsError);
        Assert.False(asked);
    }

    
    [Fact]
    public async Task With_no_gate_configured_navigate_behaves_as_it_did()
    {
        var (handlers, driver) = Navigator(null);
        driver.Navigation = new PaneNavigation("to", Dest);

        var result = await handlers.NavigateAsync(Url(Dest), card: null, default);

        Assert.False(result.IsError);
        Assert.Contains(driver.Calls, c => c.StartsWith("navigate:"));
    }

    [Fact]
    public async Task A_history_move_is_resolved_against_the_entry_it_would_land_on()
    {
        // The routing is what this pins, and it outlives the stub: back and
        // forward are put through the gate against the entry they would land
        // on, not against the address bar. What the gate answers today is
        // allowed, so the move goes through.
        var (handlers, driver) = Navigator(Both());
        driver.HistoryTarget = Dest + "/seen-before";

        var result = await handlers.NavigateAsync(Url("back"), Card(false), default);

        Assert.False(result.IsError);
        Assert.Contains("history-target:back", driver.Calls);
    }

    [Fact]
    public async Task A_history_move_back_to_the_same_site_is_not_asked_about()
    {
        var (handlers, driver) = Navigator(Both());
        driver.HistoryTarget = Src + "/seen-before";
        driver.Navigation = new PaneNavigation("back", null);

        var result = await handlers.NavigateAsync(Url("back"), Card(false), default);

        Assert.False(result.IsError);
        Assert.StartsWith("navigated back", result.Content);
    }

    [Fact]
    public async Task An_empty_history_is_reported_before_anyone_is_asked()
    {
        var (handlers, _) = Navigator(Both());
        var asked = false;

        var result = await handlers.NavigateAsync(Url("forward"),
            (_, _, _) => { asked = true; return Task.FromResult<bool?>(true); }, default);

        Assert.True(result.IsError);
        Assert.Equal("no forward history", result.Content);
        Assert.False(asked);
    }

    [Fact]
    public async Task A_tab_that_moves_mid_navigation_still_gets_there()
    {
        // The stale-tab retry is the machine's, and is covered against
        // DecideAsync. On the shipped path there is no prompt to go stale
        // across, so the navigation simply happens.
        var (handlers, driver) = Navigator(Both());
        driver.NavigateOnMarkCall = 1;

        var result = await handlers.NavigateAsync(Url(Dest), Card(true), default);

        Assert.False(result.IsError);
        Assert.Contains(driver.Calls, c => c.StartsWith("navigate:", StringComparison.Ordinal));
    }
}
