using JarvisCode.Core.Agent;

namespace JarvisCode.Core.Tests.Agent;

/// <summary>
/// The reference's task token budget: it re-arms on every prompt and counts the
/// context the task has grown by since (its <c>reanchorTaskBudget</c> and
/// <c>cumulativeUsed</c>).
/// </summary>
public sealed class TotalTokensReminderTests
{
    private static TotalTokensReminder Budget() => new(TotalTokensMode.PaddedCountdown);

    private static string Block(long left) => $"<total_tokens>{left} tokens left</total_tokens>";

    [Fact]
    public void TheBlockFollowsTheContextEachTurnAdds()
    {
        var budget = Budget();

        // A fresh session: the block that follows the prompt reads the full
        // figure, then the one after the tool results counts the whole context
        // the first call reported, because the task started from nothing.
        budget.ReanchorTaskBudget(0);
        Assert.Equal(Block(15_000_000), budget.RenderLive());
        budget.Observe(14_101);
        Assert.Equal(Block(14_985_899), budget.RenderLive());
        budget.Observe(19_900);

        // The next prompt re-arms on the context the task starts from, so the
        // block beside it reads the full figure again — and the one after that
        // turn's tool results counts only what this turn added.
        budget.ReanchorTaskBudget(19_900);
        Assert.Equal(Block(15_000_000), budget.RenderLive());
        budget.Observe(20_055);
        Assert.Equal(Block(14_999_845), budget.RenderLive());
    }

    [Fact]
    public void AContextReportedSmallerThanTheAnchorReadsAsNothingUsed()
    {
        // A context cannot really shrink inside a task — the conversation only
        // grows between compactions — so a call reporting less than the turn
        // started from is a bad reading, and the reference's own
        // max(previous, rolled + current - anchor) clamps it to nothing used.
        // That is how the block froze at the full budget for a whole session
        // against an endpoint that dropped its cache counts. The fix belongs
        // upstream, in what the wire reports (AnthropicStream now reads the
        // usage fields message_delta carries); this arithmetic is the
        // reference's and stays as it is.
        var budget = Budget();
        budget.ReanchorTaskBudget(19_900);
        budget.Observe(240);

        Assert.Equal(Block(15_000_000), budget.RenderLive());
    }

    [Fact]
    public void ACompactionBanksWhatItRemovedSoTheCountKeepsRising()
    {
        // The reference's rollOverContext: the compacted context is banked, so
        // the summary does not read as a context that shrank.
        var budget = Budget();
        budget.ReanchorTaskBudget(0);
        budget.Observe(120_000);
        Assert.Equal(Block(14_880_000), budget.RenderLive());

        budget.RollOverContext(120_000);
        budget.Observe(8_000);

        Assert.Equal(Block(14_872_000), budget.RenderLive());
    }

    [Fact]
    public void TheStaticBlockIsTheBudgetItselfSoTheCachedPrefixNeverMoves()
    {
        var budget = Budget();
        budget.ReanchorTaskBudget(0);
        budget.Observe(14_101);

        Assert.Equal(Block(15_000_000), budget.RenderStatic());
        Assert.Equal(Block(14_985_899), budget.RenderLive());
    }
}
