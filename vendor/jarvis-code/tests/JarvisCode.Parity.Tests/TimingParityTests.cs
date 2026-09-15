using System.IO;
using JarvisCode.App.Controls;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The timings that decide *when* the status line does things. Each value was
/// read out of the reference app's own bundle, and each test states where — so
/// the number can be re-found rather than taken on trust.
///
/// Two layers on purpose: the constants are asserted unconditionally (they are
/// facts about our code and must not drift silently), and the ones that can be
/// re-derived from the installed bundle are additionally re-measured against it,
/// which is what catches the reference itself moving.
/// </summary>
public sealed class TimingParityTests
{
    [Fact]
    public void Label_dwell_matches_the_reference()
        // Reference: the label hook is called as `Yp(label, 650)` and holds a
        // label for that long, scheduling the remainder when a newer one arrives.
        => Assert.Equal(650, TurnStatusLine.LabelDwellMs);

    [Fact]
    public void Label_morph_matches_the_reference()
        // Reference: `element.animate([...], {duration:180, easing:`cubic-bezier(${Dd})`})`
        // with `Dd=[.2,0,0,1]`.
        => Assert.Equal(180, TurnStatusLine.LabelMorphMs);

    [Fact]
    public void Cluster_fade_matches_the_reference()
        // Reference: `const e = setTimeout(() => a(s), 150)` around the swap.
        => Assert.Equal(150, TurnStatusLine.ClusterFadeMs);

    [Fact]
    public void Shimmer_matches_the_reference_stylesheet()
    {
        // Reference CSS: `.epitaxy-thinking-shimmer{animation:2s ease-in-out 3s infinite …}`
        // over `@keyframes{0%,to{opacity:1}50%{opacity:.75}}`.
        Assert.Equal(2000, TurnStatusLine.ShimmerPeriodMs);
        Assert.Equal(3000, TurnStatusLine.ShimmerDelayMs);
        Assert.Equal(0.75, TurnStatusLine.ShimmerMinOpacity);
    }

    [Fact]
    public void Stats_appear_after_the_reference_threshold()
        // Reference: `C = f >= 2` gates the elapsed timer, and the token counter
        // rides that same flag (`A = C && m > 0`).
        => Assert.Equal(2, TurnStatusLine.StatsVisibleAfterSeconds);

    [Fact]
    public void Token_count_up_matches_the_reference()
        => Assert.Equal(400, TurnStatusLine.TokenCountUpMs);

    /// <summary>
    /// Re-derives the shimmer timing from the installed app rather than trusting
    /// the constant: the stylesheet ships as plain text in the bundle, so the
    /// declaration itself can be read back and compared.
    /// </summary>
    [ReferenceAppFact]
    public void Shimmer_declaration_is_still_what_the_bundle_ships()
    {
        var declaration = FindInAssets("epitaxy-thinking-shimmer{will-change:opacity;animation:");
        Assert.True(declaration is not null,
            "the reference's thinking-shimmer rule was not found in the installed bundle — " +
            "the class was renamed or the animation restructured; re-measure before trusting " +
            $"{nameof(TurnStatusLine.ShimmerPeriodMs)}/{nameof(TurnStatusLine.ShimmerDelayMs)}.");

        var expected =
            $"animation:{TurnStatusLine.ShimmerPeriodMs / 1000}s ease-in-out " +
            $"{TurnStatusLine.ShimmerDelayMs / 1000}s infinite";
        Assert.Contains(expected, declaration);
    }

    /// <summary>
    /// The same for the label morph. Both halves ride one Tailwind utility the
    /// bundle ships as plain text — <c>cds-morph-in_0.18s_cubic-bezier(.2,0,0,1)</c>
    /// — so the anchor is the duration and the curve themselves rather than the
    /// minified names they were bound to. 1.44121.2.0 renamed those (the array
    /// is <c>Ue</c> in one chunk and <c>sS</c> in another) without moving either
    /// value, which is exactly the drift an anchor on a name reports as a change.
    /// </summary>
    [ReferenceAppFact]
    public void Morph_duration_and_easing_are_still_what_the_bundle_ships()
    {
        var seconds = (TurnStatusLine.LabelMorphMs / 1000.0)
            .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var utility = $"cds-morph-in_{seconds}s_cubic-bezier(.2,0,0,1)";

        var declaration = FindInAssets(utility);
        Assert.True(declaration is not null,
            $"the reference's morph utility `{utility}` is gone — its duration or its curve moved, " +
            "so TurnStatusLine.LabelMorphMs and TurnStatusLine.MorphSpline need re-measuring.");

        // The same numbers again, as the animation the component builds from them.
        var duration = FindInAssets($"={TurnStatusLine.LabelMorphMs},");
        Assert.True(duration is not null,
            $"no `={TurnStatusLine.LabelMorphMs},` duration constant remains beside it.");
    }

    [ReferenceAppFact]
    public void Dwell_constant_is_still_what_the_bundle_ships()
    {
        // The dwell rides the hook's second argument at its only call site. What
        // follows that call is minifier output and changed in 1.44121.2.0 (the
        // result is bound to a local now rather than compared inline), so the
        // anchor stops at the return.
        var call = FindInAssets($",{TurnStatusLine.LabelDwellMs});return");
        Assert.True(call is not null,
            $"the reference's {TurnStatusLine.LabelDwellMs}ms label dwell call was not found; " +
            "the status line's label hook changed upstream.");
    }

    /// <summary>
    /// Searches the packaged app's script assets for a literal, returning a
    /// window around the first hit. The bundle is ~2500 files, so this reads
    /// them once and stops at the first match.
    /// </summary>
    private static string? FindInAssets(string literal)
    {
        var assets = Path.Combine(ReferenceInstall.AppDirectory!, "resources", "ion-dist", "assets", "v1");
        if (!Directory.Exists(assets))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(assets, "*.js"))
        {
            var text = File.ReadAllText(file);
            int index = text.IndexOf(literal, StringComparison.Ordinal);
            if (index >= 0)
            {
                int end = Math.Min(text.Length, index + literal.Length + 120);
                return text[index..end];
            }
        }

        return null;
    }
}
