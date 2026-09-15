using System.Linq;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The block a pointed-at window puts on the next message — the reference's
/// <c>L</c> over <c>noteCuWindowMentions</c> / <c>appendCuWindowHint</c>
/// (desktop 1.44121.2.0).
/// </summary>
public class ComputerUseWindowHintsTests
{
    [Fact]
    public void Nothing_pointed_at_renders_nothing()
        => Assert.Null(ComputerUseWindowHints.Render([]));

    [Fact]
    public void One_window_carries_its_title_its_app_and_its_id()
    {
        var hint = ComputerUseWindowHints.Render([new WindowMention("notepad", "Untitled - Notepad", 66)])!;

        Assert.StartsWith("\n\n<cu_window_hints>The user is pointing at: ", hint);
        Assert.Contains("window \"Untitled - Notepad\" (already open — pass \"notepad\" to request_access;", hint);
        Assert.Contains("window_id: 66", hint);
        Assert.EndsWith("Do not open_application for it.</cu_window_hints>", hint);
    }

    [Fact]
    public void Two_windows_are_joined_by_a_comma()
    {
        var hint = ComputerUseWindowHints.Render(
            [new WindowMention("notepad", "a", 1), new WindowMention("chrome", "b", 2)])!;

        Assert.Contains("\"notepad\" to request_access", hint);
        Assert.Contains("\"chrome\" to request_access", hint);
        Assert.Contains("), window \"b\"", hint);
    }

    [Fact]
    public void An_untitled_window_says_so()
        => Assert.Contains("window \"(untitled)\"", ComputerUseWindowHints.Render([new WindowMention("app", "", 1)])!);

    [Fact]
    public void A_long_title_is_cut_at_the_references_hundred_and_twenty()
    {
        var title = new string('x', 200);
        var hint = ComputerUseWindowHints.Render([new WindowMention("app", title, 1)])!;

        Assert.Contains("window \"" + new string('x', ComputerUseWindowHints.MaxTitle) + "\"", hint);
        Assert.DoesNotContain(new string('x', ComputerUseWindowHints.MaxTitle + 1), hint);
    }

    [Fact]
    public void A_pick_rides_one_message_and_is_then_forgotten()
    {
        ComputerUseWindowHints.Point("s1", new WindowMention("notepad", "Untitled", 7));

        Assert.Contains("notepad", ComputerUseWindowHints.Drain("s1")!);
        Assert.Null(ComputerUseWindowHints.Drain("s1"));
    }

    [Fact]
    public void A_pick_belongs_to_the_session_it_was_made_in()
    {
        ComputerUseWindowHints.Point("a", new WindowMention("notepad", "Untitled", 8));

        Assert.Null(ComputerUseWindowHints.Drain("b"));
        Assert.NotNull(ComputerUseWindowHints.Drain("a"));
    }

    [Fact]
    public void The_same_window_pointed_at_twice_is_listed_once()
    {
        ComputerUseWindowHints.Point("c", new WindowMention("notepad", "Untitled", 9));
        ComputerUseWindowHints.Point("c", new WindowMention("notepad", "Untitled", 9));

        var hint = ComputerUseWindowHints.Drain("c")!;

        Assert.Equal(1, hint.Split("already open").Length - 1);
    }

    [Fact]
    public void The_window_list_names_no_blank_application()
        => Assert.All(
            ComputerUseWindowHints.Windows(),
            static window => Assert.False(string.IsNullOrWhiteSpace(window.App)));
}
