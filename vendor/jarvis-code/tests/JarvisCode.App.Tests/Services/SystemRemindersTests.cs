using System.Diagnostics;
using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Core.Models;

namespace JarvisCode.App.Tests.Services;

public class SystemRemindersTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reminders-").FullName;

    public void Dispose()
    {
        // Git object files are read-only; strip the flag or the delete throws.
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private void RunGit(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        process.WaitForExit(10_000);
        Assert.Equal(0, process.ExitCode);
    }

    private const string PlanPath = @"C:\repo\.jarvis\plans\s1.md";

    [Fact]
    public void Attach_appends_and_visible_text_hides_the_reminder()
    {
        var message = ChatMessage.FromUserText("fix the bug");
        var withReminder = SystemReminders.Attach(message, SystemReminders.PlanMode(PlanPath));
        Assert.Equal(2, withReminder.Content.Count);
        Assert.Equal("fix the bug", SystemReminders.VisibleText(withReminder));
        Assert.Contains("Plan mode is active.", withReminder.GetText());
    }

    [Fact]
    public void Harness_system_turn_is_never_visible()
    {
        // The orchestrator's trailing turn: the batching reminder and the token
        // block, one content block each. It is harness-authored end to end and
        // Core composes it, so no provenance counts are stamped on it — the flag
        // is the provenance. The reference sends this as a message of role
        // "system" and its transcript shows none of it.
        var trailer = new ChatMessage(Role.User, [
            new TextBlock("First privately list what you need next; then request every item that " +
                "doesn't depend on another's result in this one response."),
            new TextBlock("<total_tokens>15000000 tokens left</total_tokens>"),
        ])
        { HarnessSystemTurn = true };

        Assert.Equal("", SystemReminders.VisibleText(trailer));
        Assert.Equal("", SystemReminders.DisplayText(trailer));

        // The roster message is the same turn stamped, and answers the same.
        var roster = SystemReminders.HarnessSystemMessage("Available agent types for the Agent tool:");
        Assert.Equal("", SystemReminders.VisibleText(roster));
    }

    [Fact]
    public void A_typed_message_opening_with_a_reminder_tag_still_shows()
    {
        // The counts decide what the user wrote, not a prefix sniff — and the
        // harness flag must not have taken that away.
        var typed = SystemReminders.UserMessage(
            ChatMessage.FromUserText("<system-reminder> is the tag I mean"));
        Assert.Equal("<system-reminder> is the tag I mean", SystemReminders.VisibleText(typed));
    }

    [Fact]
    public void Plan_mode_reminder_carries_the_reference_sentences()
    {
        var reminder = SystemReminders.PlanMode(PlanPath);
        Assert.StartsWith("<system-reminder>", reminder);
        Assert.Contains(
            "you MUST NOT make any edits (with the exception of the plan file mentioned below), run any " +
            "non-readonly tools (including changing configs or making commits), or otherwise make any " +
            "changes to the system. This supercedes any other instructions",
            reminder);
        Assert.Contains("## Plan File Info:", reminder);
        Assert.Contains($"You should create your plan at {PlanPath} using the Write tool.", reminder);
        Assert.Contains("this is the only file you are allowed to edit", reminder);
        Assert.Contains("AskUserQuestion", reminder);
        Assert.Contains("ExitPlanMode", reminder);
        Assert.EndsWith("</system-reminder>", reminder);
    }

    [Fact]
    public void Wrap_context_frames_a_named_section()
    {
        var wrapped = SystemReminders.WrapContext("gitStatus", "body");
        Assert.Contains("As you answer the user's questions, you can use the following context:", wrapped);
        Assert.Contains("# gitStatus\nbody", wrapped);
        Assert.Contains("may or may not be relevant to your tasks", wrapped);
    }

    [Fact]
    public void Git_status_is_null_outside_a_repository()
        => Assert.Null(SystemReminders.BuildGitStatus(_dir));

    [Fact]
    public void Git_status_reports_branch_status_and_commits()
    {
        RunGit("init -b master");
        RunGit("config user.email t@t");
        RunGit("config user.name tester");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "one");
        RunGit("add a.txt");
        RunGit("commit -m first");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "two");

        var reminder = SystemReminders.BuildGitStatus(_dir);
        Assert.NotNull(reminder);
        // It is the system prompt's gitStatus body now, not a wrapped reminder:
        // the prompt supplies the "gitStatus: " label the reference puts there.
        Assert.DoesNotContain("<system-reminder>", reminder);
        Assert.DoesNotContain("# gitStatus", reminder);
        Assert.DoesNotContain('\r', reminder);
        Assert.StartsWith("This is the git status at the start of the conversation.", reminder);
        Assert.Contains("Current branch: master", reminder);
        // No origin in this fixture, so the reference's "main" default applies
        // rather than the branch the repo happens to be on.
        Assert.Contains("Main branch (you will usually use this for PRs): main", reminder);
        Assert.Contains("Git user: tester", reminder);
        Assert.Contains("?? b.txt", reminder);
        Assert.Contains("Recent commits:", reminder);
        Assert.Contains("first", reminder);
    }

    [Fact]
    public void Git_status_shows_clean_when_nothing_changed()
    {
        RunGit("init -b main");
        RunGit("config user.email t@t");
        RunGit("config user.name tester");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "one");
        RunGit("add a.txt");
        RunGit("commit -m first");

        var reminder = SystemReminders.BuildGitStatus(_dir);
        Assert.NotNull(reminder);
        Assert.Contains("Status:\n(clean)", reminder!.Replace("\r\n", "\n"));
    }
}

/// <summary>The ultracode/ultrathink harness reminders (reference 2.x wording).
/// "ultracode" is the reference's workflow keyword trigger, so the reminders point
/// at the workflow tool with Agent named beside it.</summary>
public class UltracodeReminderTests
{
    [Fact]
    public void EveryReminderIsAHiddenSystemReminderBlock()
    {
        foreach (var text in new[]
        {
            SystemReminders.Ultrathink,
            SystemReminders.UltracodeKeyword,
            SystemReminders.UltracodeEnter,
            SystemReminders.UltracodeStillOn,
            SystemReminders.UltracodeExit,
        })
        {
            Assert.StartsWith("<system-reminder>", text);
            Assert.EndsWith("</system-reminder>", text);
            Assert.True(SystemReminders.IsReminder(new TextBlock(text)));
        }
    }

    [Fact]
    public void TheReferenceSentencesSurvive()
    {
        // The ultrathink reminder is the reference text verbatim.
        Assert.Contains(
            "The user included the keyword \"ultrathink\", requesting deeper reasoning on this turn. " +
            "Reason as thoroughly as the task warrants.",
            SystemReminders.Ultrathink);
        // The ultracode set keeps the reference's key sentences and its pointer
        // at the Workflow tool — the keyword trigger opts the turn into it.
        Assert.Contains("optimize for the most exhaustive, correct answer", SystemReminders.UltracodeEnter);
        Assert.Contains("token cost is not a constraint", SystemReminders.UltracodeEnter);
        Assert.Contains("workflow tool", SystemReminders.UltracodeEnter);
        Assert.Contains("Agent", SystemReminders.UltracodeEnter);
        Assert.Contains("workflow tool", SystemReminders.UltracodeKeyword);
        Assert.Contains("Ultracode is still on", SystemReminders.UltracodeStillOn);
        Assert.Contains("Ultracode is off", SystemReminders.UltracodeExit);
        Assert.Contains("opting this turn into multi-agent", SystemReminders.UltracodeKeyword);
    }
}
