using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class DisplayTextTests
{
    [Fact]
    public void LeavesOrdinaryTextAlone() =>
        Assert.Equal("Refactor the parser", DisplayText.Sanitize("Refactor the parser"));

    [Fact]
    public void KeepsEveryAstralCharacter()
    {
        // The category filter runs on code points; reading it as UTF-16 units would
        // see each half of a surrogate pair as a lone surrogate and delete it.
        const string text = "ship it 🚀 and 😀 too";

        Assert.Equal(text, DisplayText.Sanitize(text));
    }

    [Fact]
    public void DropsBidiControls() =>
        Assert.Equal("repognp.exe", DisplayText.Sanitize("repo‮gnp.exe"));

    [Theory]
    [InlineData("​")]
    [InlineData("‎")]
    [InlineData("﻿")]
    [InlineData("⁦")]
    [InlineData("ㅤ")]
    [InlineData("­")]
    public void DropsTheInvisibleCharacters(string invisible) =>
        Assert.Equal("ab", DisplayText.Sanitize("a" + invisible + "b"));

    [Fact]
    public void DropsTheTagBlock() =>
        Assert.Equal("ab", DisplayText.Sanitize("a\U000E0041b"));

    [Fact]
    public void DropsALoneSurrogate() =>
        Assert.Equal("ab", DisplayText.Sanitize("a\ud800b"));

    [Fact]
    public void KeepsTheFourCharactersTheReferenceKeeps()
    {
        foreach (var kept in new[] { "‌", "‍", "︎", "️" })
        {
            Assert.Equal("a" + kept + "b", DisplayText.Sanitize("a" + kept + "b"));
        }
    }

    [Fact]
    public void KeepsAnEmojiSequenceWholeIncludingItsJoiner()
    {
        // Family emoji: three faces joined by ZWJ. Dropping the joiners would break
        // one glyph into three.
        const string family = "\U0001F468‍\U0001F469‍\U0001F467";

        Assert.Equal(family, DisplayText.Sanitize(family));
    }

    [Fact]
    public void KeepsTheThreeWhitespaceControlsAndDropsTheRest() =>
        Assert.Equal("a\tb\nc", DisplayText.Sanitize("a\tb\nc"));

    [Fact]
    public void NormalizesCompatibilityForms() =>
        // NFKC folds the fullwidth letters, which is what stops a title from
        // spelling one thing and sorting as another.
        Assert.Equal("ABC", DisplayText.Sanitize("ＡＢＣ"));
}
