using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The reference's grant answers: which sentence a tier draws, and which keys an
/// answer carries. Both are what the model reads, and both are shaped rather
/// than copied, so they are worth measuring.
/// </summary>
public sealed class ComputerUseGrantResultsTests
{
    [Fact]
    public void Nothing_restricted_draws_no_tier_guidance() =>
        Assert.Equal("", ComputerUseGrantResults.TierGuidance([("Notepad", AppTier.Full, "")]));

    [Fact]
    public void A_browser_is_pointed_at_the_chrome_mcp()
    {
        var guidance = ComputerUseGrantResults.TierGuidance([("chrome", AppTier.Read, "browser")]);

        Assert.Contains("\"chrome\" is a browser — granted at tier \"read\"", guidance, StringComparison.Ordinal);
        Assert.Contains("mcp__claude-in-chrome__*", guidance, StringComparison.Ordinal);
        Assert.EndsWith(ComputerUseGrantResults.NoWorkaround, guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_browsers_take_the_plural_form()
    {
        var guidance = ComputerUseGrantResults.TierGuidance(
            [("chrome", AppTier.Read, "browser"), ("firefox", AppTier.Read, "browser")]);

        Assert.Contains("\"chrome\", \"firefox\" are browsers", guidance, StringComparison.Ordinal);
        Assert.Contains("cannot navigate, click, or type into them", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void A_terminal_and_the_shell_are_two_different_paragraphs()
    {
        var guidance = ComputerUseGrantResults.TierGuidance(
            [("Code", AppTier.Click, "terminal"), ("explorer", AppTier.Click, "shell")]);

        Assert.Contains("has terminal or IDE capabilities", guidance, StringComparison.Ordinal);
        Assert.Contains("is the Windows desktop shell", guidance, StringComparison.Ordinal);
        Assert.Equal(2, guidance.Split("\n\n").Length);
    }

    [Fact]
    public void An_unknown_app_says_the_user_never_saw_the_request()
    {
        var refusal = ComputerUseGrantResults.NotInstalled([("Notpad", ["Notepad", "Notepad++"])], flagsRequested: false);

        Assert.StartsWith(
            "\"Notpad\" doesn't match any installed or running application. The request was NOT shown to the user.",
            refusal,
            StringComparison.Ordinal);
        Assert.Contains("Did you mean: Notpad → \"Notepad\" or \"Notepad++\"?", refusal, StringComparison.Ordinal);
        Assert.Contains("Retry request_access with the corrected name (", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("clipboard/systemKeyCombos", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_unknown_apps_pluralise_and_name_the_short_circuited_flags()
    {
        var refusal = ComputerUseGrantResults.NotInstalled(
            [("Notpad", []), ("Edg", [])], flagsRequested: true);

        Assert.StartsWith("\"Notpad\", \"Edg\" don't match", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", refusal, StringComparison.Ordinal);
        Assert.Contains("corrected names (", refusal, StringComparison.Ordinal);
        Assert.Contains(", as were the clipboard/systemKeyCombos flags you passed", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Granted_applications_answer_with_the_reference_json()
    {
        var json = JsonNode.Parse(ComputerUseGrantResults.GrantedApplications(
            [("Notepad", AppTier.Full)], clipboardRead: true, clipboardWrite: false, systemKeyCombos: false))!
            .AsObject();

        Assert.Equal(["allowedApps", "grantFlags"], json.Select(static p => p.Key));
        var app = json["allowedApps"]!.AsArray()[0]!.AsObject();
        Assert.Equal(["bundleId", "displayName", "tier"], app.Select(static p => p.Key));
        Assert.Equal("full", (string)app["tier"]!);
        Assert.True((bool)json["grantFlags"]!["clipboardRead"]!);
        Assert.False((bool)json["grantFlags"]!["systemKeyCombos"]!);
    }

    [Fact]
    public void Access_granted_omits_tier_guidance_when_there_is_none()
    {
        var json = JsonNode.Parse(ComputerUseGrantResults.AccessGranted(
            [("Notepad", AppTier.Full)], [], ""))!.AsObject();

        Assert.Equal(["granted", "denied", "screenshotFiltering"], json.Select(static p => p.Key));
        Assert.Equal("mask", (string)json["screenshotFiltering"]!);
    }

    [Fact]
    public void Access_granted_carries_the_guidance_before_the_filtering_note()
    {
        var json = JsonNode.Parse(ComputerUseGrantResults.AccessGranted(
            [("chrome", AppTier.Read)], ["Steam"], "guidance"))!.AsObject();

        Assert.Equal(
            ["granted", "denied", "tierGuidance", "screenshotFiltering"],
            json.Select(static p => p.Key));
        Assert.Equal("Steam", (string)json["denied"]!.AsArray()[0]!);
    }

    [Fact]
    public void A_string_argument_refusal_names_the_argument() =>
        Assert.Equal("\"reason\" must be a string.", ComputerUseGrantResults.MustBeAString("reason"));

    [Fact]
    public void Clipboard_read_answers_with_json() =>
        Assert.Equal("{\"text\":\"hi\"}", ComputerUseGrantResults.ClipboardText("hi"));
}
