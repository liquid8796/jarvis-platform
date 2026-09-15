using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The API-error classifier, which is the reference's own table read in its own
/// order. Order is the rule here: several patterns match the same message, and
/// the first one decides what the card says and offers.
/// </summary>
public class ErrorCardsTests
{
    [Theory]
    [InlineData("API Error: image exceeds the dimension limit", ApiErrorCategory.ImageDimension)]
    [InlineData("You have hit your usage limit for this month", ApiErrorCategory.RateLimit)]
    [InlineData("rate_limit_error: slow down", ApiErrorCategory.ServerRateLimit)]
    [InlineData("Stream idle timeout after 60s", ApiErrorCategory.StreamIdleTimeout)]
    [InlineData("overloaded_error", ApiErrorCategory.Overloaded)]
    [InlineData("Prompt is too long: 210000 tokens", ApiErrorCategory.ContextLength)]
    [InlineData("Request too large for model", ApiErrorCategory.RequestTooLarge)]
    [InlineData("This violates our Usage Policy", ApiErrorCategory.UsagePolicy)]
    [InlineData("Could not process image", ApiErrorCategory.ImageInvalid)]
    [InlineData("model not found: gpt-9", ApiErrorCategory.ModelNotFound)]
    [InlineData("this ChatGPT account has no gpt-6-astra in its model picker", ApiErrorCategory.ModelNotFound)]
    [InlineData("exceeded the 64000 output token limit", ApiErrorCategory.OutputTokenLimit)]
    [InlineData("Your credit balance is too low", ApiErrorCategory.Billing)]
    [InlineData("ECONNRESET while reading", ApiErrorCategory.Network)]
    [InlineData("authentication_error: bad key", ApiErrorCategory.Auth)]
    [InlineData("Internal server error", ApiErrorCategory.ServerError)]
    [InlineData("permission_error", ApiErrorCategory.Permission)]
    [InlineData("invalid_request_error", ApiErrorCategory.InvalidRequest)]
    public void TheMessageDecidesTheCategory(string message, ApiErrorCategory expected) =>
        Assert.Equal(expected, ErrorCards.Classify(message));

    [Fact]
    public void TheLookbehindKeepsANegatedUsageLimitOutOfTheRateLimitRow()
    {
        // The reference's own guard: "not your usage limit" must not classify as
        // the user having hit theirs.
        Assert.NotEqual(
            ApiErrorCategory.RateLimit,
            ErrorCards.Classify("This is not your usage limit problem; invalid_request_error"));
    }

    [Fact]
    public void AnImageDimensionWinsOverTheRateLimitRowThatAlsoMatches()
    {
        // Both patterns match; the table's order is what decides.
        Assert.Equal(
            ApiErrorCategory.ImageDimension,
            ErrorCards.Classify("Image was too large and you hit your usage limit"));
    }

    [Fact]
    public void ARefusalIsAUsagePolicyBlockWhateverItSays() =>
        Assert.Equal(
            ApiErrorCategory.UsagePolicy,
            ErrorCards.Classify("nothing in particular", stopReason: "refusal"));

    [Theory]
    [InlineData("rate_limit", ApiErrorCategory.ServerRateLimit)]
    [InlineData("authentication_failed", ApiErrorCategory.Auth)]
    [InlineData("max_output_tokens", ApiErrorCategory.OutputTokenLimit)]
    [InlineData("overloaded_error", ApiErrorCategory.Overloaded)]
    public void ANamedErrorTypeDecidesWhenNoPatternMatched(string type, ApiErrorCategory expected) =>
        Assert.Equal(expected, ErrorCards.Classify("something went wrong", type));

    [Theory]
    [InlineData(429, ApiErrorCategory.ServerRateLimit)]
    [InlineData(529, ApiErrorCategory.Overloaded)]
    [InlineData(503, ApiErrorCategory.ServerError)]
    [InlineData(401, ApiErrorCategory.Auth)]
    [InlineData(402, ApiErrorCategory.Billing)]
    [InlineData(403, ApiErrorCategory.Permission)]
    [InlineData(413, ApiErrorCategory.RequestTooLarge)]
    [InlineData(422, ApiErrorCategory.InvalidRequest)]
    public void TheStatusDecidesWhenNothingElseDid(int status, ApiErrorCategory expected) =>
        Assert.Equal(expected, ErrorCards.FromStatus(status));

    [Fact]
    public void AStatusIsReadOutOfTheMessage()
    {
        Assert.Equal(429, ErrorCards.TryReadStatus("API error: 429 too many"));
        Assert.Equal(500, ErrorCards.TryReadStatus("HTTP 500"));
        Assert.Null(ErrorCards.TryReadStatus("no status here"));
    }

    [Fact]
    public void AnUnrecognisedMessageIsUnknown() =>
        Assert.Equal(ApiErrorCategory.Unknown, ErrorCards.Classify("the sky fell"));

    [Fact]
    public void TheRequestIdIsReadOffItsOwnLine()
    {
        Assert.Equal("req_0123", ErrorCards.RequestIdIn("Something failed\nRequest ID: req_0123"));
        Assert.Null(ErrorCards.RequestIdIn("Something failed"));
    }

    [Fact]
    public void ContextLengthOffersCompactAndRewind()
    {
        var card = ErrorCards.Describe(ApiErrorCategory.ContextLength);

        Assert.Equal("Your context window is full", card.Headline);
        Assert.True(card.CompactHelps);
        Assert.True(card.RewindHelps);
        Assert.False(card.RetryHelps);
    }

    [Fact]
    public void WithNothingToRewindToTheOtherHintIsUsed()
    {
        var card = ErrorCards.Describe(ApiErrorCategory.ContextLength, canRewind: false);

        Assert.Equal("This chat is too long to continue. Start a new chat.", card.Hint);
        Assert.False(card.RewindHelps);
    }

    [Fact]
    public void TheServerErrorHintNamesTheStatusPage() =>
        Assert.Contains(ErrorCards.StatusUrl, ErrorCards.Describe(ApiErrorCategory.ServerError).Hint);

    [Fact]
    public void ACollapsedMarkerUsesTheTablesOwnLabelWhereItHasOne()
    {
        Assert.Equal("Service was busy", ErrorCards.MarkerLabelFor(ApiErrorCategory.Overloaded));
        // With no marker label of its own the headline stands.
        Assert.Equal("Billing issue", ErrorCards.MarkerLabelFor(ApiErrorCategory.Billing));
    }

    [Fact]
    public void EverySessionErrorHasATitleAndABody()
    {
        foreach (var kind in Enum.GetValues<ErrorCards.SessionErrorKind>())
        {
            var (title, body) = ErrorCards.Describe(kind);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.False(string.IsNullOrWhiteSpace(body));
        }
    }

    [Fact]
    public void TheDiskFullBodyNamesTheNumbersWhenItHasThem() =>
        Assert.Equal(
            "The disk containing C:/wt has 2 GB free, but at least 5 GB is needed to set up a worktree for this " +
            "session. Free up disk space and try again.",
            ErrorCards.DiskFullBody("C:/wt", 2, 5));
}
