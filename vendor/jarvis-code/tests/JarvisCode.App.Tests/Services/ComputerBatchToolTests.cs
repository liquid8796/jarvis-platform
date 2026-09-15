using System.Drawing;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The batch tool drives the real desktop, so every case here stops before any
/// input is synthesized: argument validation, sequencing and the reference's
/// result wording. The only actions actually executed are the harmless ones
/// (wait 0, cursor_position, screenshot).
/// </summary>
public class ComputerBatchToolTests
{
    private static readonly ToolExecutionContext Context = new() { WorkingDirectory = @"C:\" };

    private static ComputerBatchTool Tool() => new(new ComputerUseService());

    private static JsonObject Batch(params JsonObject[] actions) =>
        new() { ["actions"] = new JsonArray([.. actions.Cast<JsonNode>()]) };

    private static JsonObject Action(string name, params (string Key, JsonNode? Value)[] pairs)
    {
        var action = new JsonObject { ["action"] = name };
        foreach (var (key, value) in pairs)
        {
            action[key] = value;
        }

        return action;
    }

    // ---- shape validation ----

    [Fact]
    public async Task RejectsAMissingOrEmptyActionList()
    {
        var tool = Tool();

        var missing = await tool.ExecuteAsync(new JsonObject(), Context, default);
        var empty = await tool.ExecuteAsync(new JsonObject { ["actions"] = new JsonArray() }, Context, default);
        var wrongType = await tool.ExecuteAsync(new JsonObject { ["actions"] = "click" }, Context, default);

        Assert.All([missing, empty, wrongType], r =>
        {
            Assert.True(r.IsError);
            Assert.Equal("actions must be a non-empty array", r.Content);
        });
    }

    [Fact]
    public async Task RejectsAnActionThatIsNotAnObject()
    {
        var result = await Tool().ExecuteAsync(
            new JsonObject { ["actions"] = new JsonArray(Action("wait", ("duration", 0)), "left_click") },
            Context,
            default);

        Assert.True(result.IsError);
        Assert.Equal("actions[1] must be an object", result.Content);
    }

    [Fact]
    public async Task RejectsAnActionWithoutAName()
    {
        var result = await Tool().ExecuteAsync(
            new JsonObject { ["actions"] = new JsonArray(new JsonObject { ["coordinate"] = new JsonArray(1, 2) }) },
            Context,
            default);

        Assert.True(result.IsError);
        Assert.Equal("actions[0].action must be a string", result.Content);
    }

    [Fact]
    public async Task RejectsAnUnknownActionAndListsTheAllowedOnes()
    {
        var result = await Tool().ExecuteAsync(Batch(Action("teleport")), Context, default);

        Assert.True(result.IsError);
        Assert.StartsWith("actions[0].action=\"teleport\" is not allowed in a batch. Allowed: ", result.Content);
        Assert.Contains("triple_click", result.Content);
        Assert.Contains("left_mouse_up", result.Content);
    }

    /// <summary>The whole batch is checked before anything runs, like the reference's.</summary>
    [Fact]
    public async Task ValidatesEveryActionBeforeExecutingTheFirst()
    {
        var result = await Tool().ExecuteAsync(
            Batch(Action("wait", ("duration", 0)), Action("fly")), Context, default);

        Assert.True(result.IsError);
        Assert.StartsWith("actions[1].action=\"fly\"", result.Content);
        // Nothing ran, so no per-action line was rendered.
        Assert.DoesNotContain("[1/2]", result.Content);
    }

    // ---- per-action argument validation ----

    [Theory]
    [InlineData("left_click")]
    [InlineData("double_click")]
    [InlineData("triple_click")]
    [InlineData("right_click")]
    [InlineData("middle_click")]
    [InlineData("mouse_move")]
    [InlineData("scroll")]
    [InlineData("left_click_drag")]
    public async Task MouseActionsRequireACoordinate(string action)
    {
        var result = await Tool().ExecuteAsync(Batch(Action(action)), Context, default);

        Assert.True(result.IsError);
        // The reference's own V(): a missing coordinate is "required", not "malformed".
        Assert.Contains("coordinate is required", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsACoordinateOutsideTheScreenshotFrame()
    {
        var frame = ComputerUseService.Displays()[0].Bounds;
        var result = await Tool().ExecuteAsync(
            Batch(Action("left_click", ("coordinate", new JsonArray(frame.Width + 10, 5)))), Context, default);

        Assert.True(result.IsError);
        Assert.Contains(
            $"coordinate [{frame.Width + 10}, 5] is outside the coordinate frame " +
            $"({frame.Width}x{frame.Height}) — coordinates are pixels in the full-resolution " +
            "coordinate frame. Take a new screenshot and pick a point inside it.",
            result.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUnknownKeyChords()
    {
        var result = await Tool().ExecuteAsync(
            Batch(Action("key", ("text", "ctrl+notakey"))), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("Unknown key 'notakey'.", result.Content);
    }

    [Theory]
    [InlineData(0, "repeat must be a positive integer")]
    [InlineData(1.5, "repeat must be a positive integer")]
    [InlineData(101, "repeat exceeds maximum of 100")]
    public async Task RejectsBadKeyRepeats(double repeat, string expected)
    {
        var result = await Tool().ExecuteAsync(
            Batch(Action("key", ("text", "a"), ("repeat", repeat))), Context, default);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
    }

    [Fact]
    public async Task RejectsAScrollWithoutAValidDirection()
    {
        var result = await Tool().ExecuteAsync(
            Batch(Action("scroll", ("coordinate", new JsonArray(4, 4)), ("scroll_direction", "sideways"),
                ("scroll_amount", 2))),
            Context,
            default);

        Assert.True(result.IsError);
        Assert.Contains("scroll_direction must be 'up', 'down', 'left', or 'right'", result.Content);
    }

    [Theory]
    [InlineData(null, "scroll_amount must be a non-negative int")]
    [InlineData(-1.0, "scroll_amount must be a non-negative int")]
    [InlineData(2.5, "scroll_amount must be a non-negative int")]
    [InlineData(101.0, "scroll_amount exceeds maximum of 100")]
    public async Task RejectsBadScrollAmounts(double? amount, string expected)
    {
        var action = Action("scroll", ("coordinate", new JsonArray(4, 4)), ("scroll_direction", "down"));
        if (amount is { } value)
        {
            action["scroll_amount"] = value;
        }

        var result = await Tool().ExecuteAsync(Batch(action), Context, default);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
    }

    [Theory]
    [InlineData(null, "duration must be a number")]
    [InlineData(-1.0, "duration must be non-negative")]
    [InlineData(101.0, "duration is too long. Duration is in seconds.")]
    public async Task RejectsBadDurations(double? duration, string expected)
    {
        var action = Action("wait");
        if (duration is { } value)
        {
            action["duration"] = value;
        }

        var result = await Tool().ExecuteAsync(Batch(action), Context, default);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
    }

    [Fact]
    public async Task HoldKeyNeedsBothAChordAndADuration()
    {
        var noText = await Tool().ExecuteAsync(Batch(Action("hold_key", ("duration", 1))), Context, default);
        var noDuration = await Tool().ExecuteAsync(Batch(Action("hold_key", ("text", "shift"))), Context, default);

        Assert.Contains("text is required", noText.Content);
        Assert.Contains("duration must be a number", noDuration.Content);
    }

    // ---- zoom ----

    [Theory]
    [InlineData("[1,2,3]", "region must be an array of length 4: [x0, y0, x1, y1]")]
    [InlineData("[-1,0,10,10]", "region values must be non-negative numbers")]
    [InlineData("[10,0,10,10]", "region x1 must be greater than x0")]
    [InlineData("[0,10,10,10]", "region y1 must be greater than y0")]
    public async Task RejectsMalformedZoomRegions(string region, string expected)
    {
        var result = await Tool().ExecuteAsync(
            Batch(Action("zoom", ("region", JsonNode.Parse(region)))), Context, default);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
    }

    [Fact]
    public async Task ZoomNeedsAScreenshotFirstBecauseItsRegionIsRelativeToOne()
    {
        var result = await Tool().ExecuteAsync(
            Batch(Action("zoom", ("region", new JsonArray(0, 0, 10, 10)))), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("take a screenshot before zooming (region coords are relative to it)", result.Content);
    }

    [Fact]
    public async Task ZoomRefusesARegionLargerThanTheFrame()
    {
        var service = new ComputerUseService();
        var frame = service.Frame;
        service.CaptureScreenshot();

        var result = await new ComputerBatchTool(service).ExecuteAsync(
            Batch(Action("zoom", ("region", new JsonArray(0, 0, frame.Width + 1, 10)))), Context, default);

        Assert.True(result.IsError);
        Assert.Contains($"region exceeds the coordinate frame ({frame.Width}×{frame.Height})", result.Content);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(1.5)]
    public async Task RejectsAScaleOutsideTheAllowedRange(double scale)
    {
        var result = await Tool().ExecuteAsync(
            Batch(Action("screenshot", ("scale", scale))), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("scale must be a number in [0.1, 1]", result.Content);
    }

    // ---- sequencing ----

    [Fact]
    public async Task NumbersEveryActionAndStopsAtTheFirstFailure()
    {
        var result = await Tool().ExecuteAsync(
            Batch(
                Action("wait", ("duration", 0)),
                Action("cursor_position"),
                Action("key", ("text", "a"), ("repeat", 0)),
                Action("wait", ("duration", 0))),
            Context,
            default);

        Assert.True(result.IsError);
        var lines = result.Content.Split('\n');
        Assert.Equal("[1/4] wait: Waited 0s.", lines[0]);
        Assert.StartsWith("[2/4] cursor_position: {", lines[1]);
        Assert.Equal("[3/4] key: FAILED — repeat must be a positive integer", lines[2]);
        Assert.Equal("Batch stopped at actions[2] (key). 2 completed, 1 remaining.", lines[3]);
    }

    [Fact]
    public async Task ReportsEveryActionWhenAllOfThemSucceed()
    {
        var result = await Tool().ExecuteAsync(
            Batch(Action("wait", ("duration", 0)), Action("wait", ("duration", 0))), Context, default);

        Assert.False(result.IsError);
        Assert.Equal("[1/2] wait: Waited 0s.\n[2/2] wait: Waited 0s.", result.Content);
        Assert.Null(result.Images);
    }

    [Fact]
    public async Task CarriesTheScreenshotsAnActionTook()
    {
        var result = await Tool().ExecuteAsync(
            Batch(Action("screenshot", ("scale", 0.2)), Action("wait", ("duration", 0))), Context, default);

        Assert.False(result.IsError);
        var image = Assert.Single(result.Images!);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Contains("coordinate frame", result.Content);
    }

    /// <summary>A failed batch keeps its lines but drops the frames they described.</summary>
    [Fact]
    public async Task DropsImagesWhenTheBatchFails()
    {
        var result = await Tool().ExecuteAsync(
            Batch(Action("screenshot", ("scale", 0.2)), Action("key", ("text", "a"), ("repeat", 0))),
            Context,
            default);

        Assert.True(result.IsError);
        Assert.Null(result.Images);
        Assert.Contains("[Image omitted due to error]", result.Content);
        Assert.Contains("Batch stopped at actions[1] (key). 1 completed, 0 remaining.", result.Content);
    }

    [Fact]
    public async Task ACancelledBatchSaysHowFarItGot()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await Tool().ExecuteAsync(Batch(Action("wait", ("duration", 0))), Context, cancellation.Token);

        Assert.True(result.IsError);
        Assert.Equal("Batch aborted after 0 of 1 actions (user interrupt).", result.Content);
    }

    // ---- the coordinate frame ----

    [Fact]
    public void FramePixelsMapOntoTheCapturedRegionOneToOne()
    {
        var frame = new Rectangle(1920, 0, 2560, 1440);

        Assert.Equal(new Point(1920, 0), ComputerUseService.ToScreen(frame, 0, 0));
        Assert.Equal(new Point(2020, 140), ComputerUseService.ToScreen(frame, 100, 140));
        Assert.True(ComputerUseService.InFrame(frame, 2559, 1439));
        Assert.False(ComputerUseService.InFrame(frame, 2560, 0));
        Assert.False(ComputerUseService.InFrame(frame, -1, 0));
    }

    [Fact]
    public void AbsoluteCoordinatesSpanTheWholeVirtualDesktop()
    {
        var virtualScreen = new Rectangle(-1920, 0, 3840, 1080);

        Assert.Equal(new Point(0, 0), ComputerUseService.ToAbsolute(virtualScreen, new Point(-1920, 0)));
        Assert.Equal(new Point(65535, 65535), ComputerUseService.ToAbsolute(virtualScreen, new Point(1919, 1079)));
    }

    [Fact]
    public void AScreenshotBecomesTheCoordinateFrameForTheNextCall()
    {
        var service = new ComputerUseService();
        Assert.False(service.HasCaptured);

        var capture = service.CaptureScreenshot(scale: 0.2);

        Assert.True(service.HasCaptured);
        Assert.Equal(service.Frame.Width, capture.FrameWidth);
        Assert.Equal(service.Frame.Height, capture.FrameHeight);
        // The image shrank; the frame the model addresses did not.
        Assert.True(capture.ImageWidth < capture.FrameWidth);
        Assert.Contains(
            $"coordinates are in the {capture.FrameWidth}x{capture.FrameHeight} coordinate frame, " +
            "not the scaled screenshot image's own pixels",
            capture.Summary);
    }

    [Fact]
    public void AFullSizeScreenshotSaysCoordinatesAreItsOwnPixels()
    {
        var capture = new ComputerUseService().CaptureScreenshot();

        Assert.Equal(capture.FrameWidth, capture.ImageWidth);
        Assert.Contains("Coordinates for computer_batch are pixels in this frame.", capture.Summary);
        Assert.Equal("image/jpeg", capture.Image.MediaType);
    }
}
