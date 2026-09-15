using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The parsing and arithmetic the Android emulator's <c>control</c> tool runs
/// on — all ported from the reference's own bridge, and all testable without a
/// device on the machine.
/// </summary>
public sealed class AndroidEmulatorTests
{
    [Fact]
    public void ParseDevices_KeepsEmulatorsAndReadsTheirModelNames()
    {
        const string output = """
            List of devices attached
            emulator-5554          device product:sdk_gphone64_x86_64 model:Pixel_10_Pro transport_id:1
            emulator-5556          offline
            R58M12ABCDE            device product:a52q model:SM_A525F
            emulator-5558          device product:sdk_gphone64_x86_64 transport_id:4
            """;

        var devices = AndroidEmulator.ParseDevices(output.ReplaceLineEndings("\n"));

        // The physical device is gone; the offline emulator keeps its own state.
        Assert.Equal(
            ["emulator-5554", "emulator-5556", "emulator-5558"],
            devices.Select(static d => d.Serial));
        Assert.Equal(["Booted", "offline", "Booted"], devices.Select(static d => d.State));

        // model: with its underscores read as spaces; without one, the serial stands in.
        Assert.Equal("Pixel 10 Pro", devices[0].Name);
        Assert.Equal("emulator-5558", devices[2].Name);
    }

    [Fact]
    public void ParseAvds_KeepsOnlyWellFormedNames()
    {
        const string output = "  Pixel_10_Pro_API_36  \n\nnot a name\nTablet_API_34\n";
        Assert.Equal(["Pixel_10_Pro_API_36", "Tablet_API_34"], AndroidEmulator.ParseAvds(output));
    }

    [Fact]
    public void ParseDisplaySize_TakesTheLastLine_SoAnOverrideWins()
    {
        Assert.Equal((1080, 2400), AndroidEmulator.ParseDisplaySize(
            "Physical size: 1440x3120\nOverride size: 1080x2400\n"));
        Assert.Null(AndroidEmulator.ParseDisplaySize("nothing here"));

        // Its own plausibility bound: a size outside 1..8192 reads as unparsed.
        Assert.Null(AndroidEmulator.ParseDisplaySize("Physical size: 99999x100"));
    }

    [Theory]
    [InlineData("SurfaceOrientation: 0", 0)]
    [InlineData("SurfaceOrientation: 1", 1)]
    [InlineData("SurfaceOrientation: 3", 3)]
    [InlineData("SurfaceOrientation: 9", 0)]
    [InlineData("no such line", 0)]
    public void ParseRotation_AcceptsOnlyOneTwoAndThree(string output, int expected) =>
        Assert.Equal(expected, AndroidEmulator.ParseRotation(output));

    [Fact]
    public void ApplyRotation_SwapsTheSidesOnAQuarterTurn()
    {
        Assert.Equal((1080, 2400), AndroidEmulator.ApplyRotation((1080, 2400), 0));
        Assert.Equal((2400, 1080), AndroidEmulator.ApplyRotation((1080, 2400), 1));
        Assert.Equal((1080, 2400), AndroidEmulator.ApplyRotation((1080, 2400), 2));
        Assert.Equal((2400, 1080), AndroidEmulator.ApplyRotation((1080, 2400), 3));
    }

    /// <summary>
    /// The reference's image budget: a display inside its pixel and token caps
    /// is sent as it is, and a bigger one shrinks to the largest size the caps
    /// admit while keeping its aspect ratio.
    /// </summary>
    [Fact]
    public void ScreenshotSize_LeavesASmallDisplayAloneAndShrinksALargeOne()
    {
        Assert.Equal((640, 480), AndroidEmulator.ScreenshotSize(640, 480));

        var (width, height) = AndroidEmulator.ScreenshotSize(1080, 2400);
        Assert.True(width < 1080 && height < 2400);
        Assert.True(width <= 1568 && height <= 1568);

        // Its token cap: ceil(w/28) * ceil(h/28) must stay within 1568.
        static int Tokens(int side) => ((side - 1) / 28) + 1;
        Assert.True(Tokens(width) * Tokens(height) <= 1568);

        // And the ratio survives, to within the rounding its binary search does.
        Assert.InRange(Math.Abs(((double)width / height) - (1080.0 / 2400.0)), 0, 0.01);
    }

    [Fact]
    public void MapCoordinate_RoundsHalfUpAndClampsToTheDisplay()
    {
        // JavaScript's Math.round is half away from zero; .NET's default is not,
        // and the two disagree on every exact .5.
        Assert.Equal(3, AndroidEmulator.MapCoordinate(1.25, 4, 8));
        Assert.Equal(0, AndroidEmulator.MapCoordinate(-5, 100, 200));
        Assert.Equal(199, AndroidEmulator.MapCoordinate(1000, 100, 200));

        // A screenshot coordinate maps straight through when the two spaces match.
        Assert.Equal(42, AndroidEmulator.MapCoordinate(42, 500, 500));
    }

    [Fact]
    public void SanitizeText_DropsWhatInputTextCannotCarry()
    {
        Assert.Equal("hello world", AndroidEmulator.SanitizeText("hello world"));

        // Non-printable-ASCII and the shell metacharacters adb's shell would eat.
        Assert.Equal("ab", AndroidEmulator.SanitizeText("a\u00e9b"));
        Assert.Equal("ab", AndroidEmulator.SanitizeText("a$b"));
        Assert.Equal("hi", AndroidEmulator.SanitizeText("h\ni"));
    }

    [Fact]
    public void EncodeInputText_EscapesSpacesAndRefusesTheRest()
    {
        Assert.Equal("hello%sworld", AndroidEmulator.EncodeInputText("hello world"));
        Assert.Null(AndroidEmulator.EncodeInputText("nope$"));
    }

    [Fact]
    public void StreamSize_ScalesToTheLongestSideAndKeepsEachSideEven()
    {
        // Under the cap, each side is only floored to an even number.
        Assert.Equal("720x1280", AndroidEmulator.StreamSize(721, 1280));

        // Over it, the longest side lands on 1440 and both stay even.
        var scaled = AndroidEmulator.StreamSize(1440, 3120);
        var parts = scaled.Split('x').Select(int.Parse).ToArray();
        Assert.True(parts[1] <= 1440);
        Assert.All(parts, side => Assert.Equal(0, side % 2));
    }

    [Fact]
    public void CountsAreCodePointsAndLineBreaks()
    {
        // A surrogate pair is one code point, which is what the reference's
        // /./gsu counts and what its "dropped" number is measured against.
        Assert.Equal(2, AndroidEmulator.CodePointCount("a\U0001F600"));
        Assert.Equal(3, AndroidEmulator.CountLineBreaks("a\r\nb\nc\u2028d"));
    }

    [Fact]
    public void NormalizeUrl_RefusesTheFiveBlockedSchemes()
    {
        Assert.Equal("https://example.test/x", AndroidEmulator.NormalizeUrl("https://example.test/x"));
        foreach (var blocked in new[] { "file:///c:/x", "javascript:alert(1)", "data:text/plain,x", "blob:x" })
        {
            Assert.Null(AndroidEmulator.NormalizeUrl(blocked));
        }

        Assert.Null(AndroidEmulator.NormalizeUrl("not a url"));
        Assert.Null(AndroidEmulator.NormalizeUrl(null));
    }

    /// <summary>
    /// A listed device's name carries its serial (the reference's
    /// <c>{AVD} ({serial})</c>), and every sentence that names a device adds one
    /// itself — so the label takes the bare half back off.
    /// </summary>
    [Fact]
    public void StripSerialSuffix_TakesOffOnlyAnEmulatorSerial()
    {
        Assert.Equal("Pixel 4 XL", AndroidEmulator.StripSerialSuffix("Pixel 4 XL (emulator-5554)"));
        Assert.Equal("Pixel 4 XL", AndroidEmulator.StripSerialSuffix("Pixel 4 XL"));

        // Only the trailing serial, and only a serial: a device named after
        // something in parentheses keeps its name.
        Assert.Equal("Tablet (big)", AndroidEmulator.StripSerialSuffix("Tablet (big)"));
        Assert.Equal(
            "(emulator-5554) Tablet",
            AndroidEmulator.StripSerialSuffix("(emulator-5554) Tablet"));
    }

    [Fact]
    public void AvdNamesMatch_ReadsUnderscoresAsSpaces()
    {
        Assert.True(AndroidEmulator.AvdNamesMatch("Pixel_4_XL", "Pixel 4 XL"));
        Assert.True(AndroidEmulator.AvdNamesMatch("Pixel_4_XL", "Pixel_4_XL"));
        Assert.False(AndroidEmulator.AvdNamesMatch("Pixel_4_XL", "Pixel 5"));
    }

    [Fact]
    public void IsEmulatorSerial_IsTheReferenceRegex_SoPhysicalDevicesNeverMatch()
    {
        Assert.True(AndroidEmulator.IsEmulatorSerial("emulator-5554"));
        Assert.False(AndroidEmulator.IsEmulatorSerial("R58M12ABCDE"));
        Assert.False(AndroidEmulator.IsEmulatorSerial("emulator-"));
    }

    // ---- the result sentences ----

    [Fact]
    public void Typed_ReportsWhatWasDroppedAndWhatWasCut()
    {
        Assert.Equal(
            "Typed 5 characters on emulator-5554.",
            AndroidEmulatorMessages.Typed("hello", 5, 0, 0, "emulator-5554"));

        Assert.Equal(
            "Typed 4 characters (1 unsupported character dropped — `input text` covers printable ASCII " +
            "minus shell metacharacters ` $ ; | & < > ( )) on emulator-5554.",
            AndroidEmulatorMessages.Typed("hell$", 4, 1, 0, "emulator-5554"));

        // Line breaks are named inside the dropped-characters clause, not beside it.
        Assert.Contains(
            "(2 unsupported characters dropped including 2 line breaks — adjacent lines were joined and " +
            "'button' cannot re-inject ENTER —",
            AndroidEmulatorMessages.Typed("a\nb\nc", 3, 2, 2, "emulator-5554"));

        var long_ = new string('x', 5000);
        Assert.Contains(
            "(first 4096 of 5000 sent; resume from index 4096 in a follow-up call).",
            AndroidEmulatorMessages.Typed(long_, 4096, 0, 0, "emulator-5554"));
    }

    [Fact]
    public void TouchPath_NamesTheCapWhenItApplied()
    {
        Assert.Equal(
            "Touch path of 3 points on emulator-5554.",
            AndroidEmulatorMessages.TouchPath(3, "emulator-5554"));
        Assert.Equal(
            "Touch path of 256 points (capped from 400) on emulator-5554.",
            AndroidEmulatorMessages.TouchPath(400, "emulator-5554"));
    }

    [Fact]
    public void DeviceLabel_NamesTheSerialAloneWhenThereIsNoOtherName()
    {
        Assert.Equal("emulator-5554", AndroidEmulatorMessages.DeviceLabel(null, "emulator-5554"));
        Assert.Equal("emulator-5554", AndroidEmulatorMessages.DeviceLabel("emulator-5554", "emulator-5554"));
        Assert.Equal("Pixel 10 Pro (emulator-5554)", AndroidEmulatorMessages.DeviceLabel("Pixel 10 Pro", "emulator-5554"));
    }

    [Fact]
    public void NoRunningEmulator_NamesTheAvdsOrSaysThereAreNone()
    {
        Assert.Equal(
            "No running emulator. Available AVDs: A, B. Pass one as 'device' to 'attach' or 'launch' to boot it.",
            AndroidEmulatorMessages.NoRunningEmulator(["A", "B"]));
        Assert.StartsWith("No running emulator, and no AVDs to boot", AndroidEmulatorMessages.NoRunningEmulator([]));
    }

    // ---- the validator ----

    [Fact]
    public void Validate_RefusesAnActionOutsideTheSharedSet()
    {
        var (call, error) = AndroidEmulatorMessages.Validate(new JsonObject { ["action"] = "fly" });
        Assert.Null(call);
        Assert.Equal(
            "'action' must be one of: attach, launch, screenshot, tap, swipe, touch_path, text, button, " +
            "open_url, detach",
            error);
    }

    /// <summary>
    /// An action the shared handler knows but this server does not — build,
    /// build_status, touch2_path — passes validation and is refused by the
    /// handler with its own iOS-only sentence, which is what the reference does.
    /// </summary>
    [Theory]
    [InlineData("build")]
    [InlineData("build_status")]
    [InlineData("touch2_path")]
    public void Validate_LetsTheIosOnlyActionsThrough(string action)
    {
        var (call, error) = AndroidEmulatorMessages.Validate(new JsonObject { ["action"] = action });
        Assert.Null(error);
        Assert.Equal(action, call!.Action);
    }

    [Fact]
    public void Validate_NamesUnknownParameters_AndPointsAtDeviceWhenOneLooksLikeIt()
    {
        var (_, error) = AndroidEmulatorMessages.Validate(new JsonObject
        {
            ["action"] = "tap",
            ["avd"] = "Pixel",
        });

        Assert.Equal(
            "Unknown parameter: avd. To target a device, use 'device'. Valid parameters: action, app_path, " +
            "device, udid, serial, bundle_id, x, y, points, x2, y2, duration, text, name, url.",
            error);

        var (_, plural) = AndroidEmulatorMessages.Validate(new JsonObject
        {
            ["action"] = "tap",
            ["nope"] = 1,
            ["alsoNope"] = 2,
        });
        Assert.StartsWith("Unknown parameters: nope, alsoNope. Valid parameters:", plural);
    }

    [Fact]
    public void Validate_TakesAtMostOneOfTheThreeDeviceAliases()
    {
        var (call, error) = AndroidEmulatorMessages.Validate(new JsonObject
        {
            ["action"] = "tap",
            ["device"] = "emulator-5554",
            ["serial"] = "emulator-5556",
        });
        Assert.Null(call);
        Assert.Equal("'device' and 'serial' each name the device to act on; pass only one of them.", error);

        // One alone resolves onto the same field.
        var (parsed, _) = AndroidEmulatorMessages.Validate(new JsonObject
        {
            ["action"] = "tap",
            ["udid"] = "emulator-5554",
        });
        Assert.Equal("emulator-5554", parsed!.Device);
    }

    [Fact]
    public void Validate_RefusesAnEmptyDeviceName()
    {
        var (call, error) = AndroidEmulatorMessages.Validate(new JsonObject
        {
            ["action"] = "tap",
            ["device"] = "",
        });
        Assert.Null(call);
        Assert.Equal("'device' must be a non-empty string naming a device.", error);
    }

    /// <summary>
    /// A button name outside the union of every platform's buttons is dropped,
    /// so 'button' answers "requires name"; one inside that union but outside
    /// Android's four survives and earns "is not supported by this tool".
    /// </summary>
    [Fact]
    public void Validate_KeepsAKnownButtonAndDropsAnInventedOne()
    {
        var (android, _) = AndroidEmulatorMessages.Validate(
            new JsonObject { ["action"] = "button", ["name"] = "RECENTS" });
        Assert.Equal("RECENTS", android!.Name);

        var (ios, _) = AndroidEmulatorMessages.Validate(
            new JsonObject { ["action"] = "button", ["name"] = "SIRI" });
        Assert.Equal("SIRI", ios!.Name);

        var (invented, _) = AndroidEmulatorMessages.Validate(
            new JsonObject { ["action"] = "button", ["name"] = "FOO" });
        Assert.Null(invented!.Name);
    }

    [Fact]
    public void Validate_DropsAWholePointArrayWhenOneSampleIsBad()
    {
        var (good, _) = AndroidEmulatorMessages.Validate(new JsonObject
        {
            ["action"] = "touch_path",
            ["points"] = new JsonArray(
                new JsonObject { ["x"] = 1, ["y"] = 2 },
                new JsonObject { ["x"] = 3, ["y"] = 4, ["dt_ms"] = 50 }),
        });
        Assert.Equal(2, good!.Points!.Count);
        Assert.Equal(50, good.Points[1].DtMs);

        var (bad, _) = AndroidEmulatorMessages.Validate(new JsonObject
        {
            ["action"] = "touch_path",
            ["points"] = new JsonArray(
                new JsonObject { ["x"] = 1, ["y"] = 2 },
                new JsonObject { ["x"] = 3 }),
        });
        Assert.Null(bad!.Points);
    }

    [Fact]
    public void Validate_ClampsDurationAndDropsANegativeOne()
    {
        var (clamped, _) = AndroidEmulatorMessages.Validate(
            new JsonObject { ["action"] = "swipe", ["duration"] = 120 });
        Assert.Equal(30, clamped!.Duration);

        var (negative, _) = AndroidEmulatorMessages.Validate(
            new JsonObject { ["action"] = "swipe", ["duration"] = -1 });
        Assert.Null(negative!.Duration);
    }
}
