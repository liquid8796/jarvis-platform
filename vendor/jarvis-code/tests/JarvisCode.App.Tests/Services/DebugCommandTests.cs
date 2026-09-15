using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// /debug, against the reference CLI 2.1.257's Debug Skill: its size formatter
/// <c>Ut</c>, its twenty-line tail block <c>vo</c>, and the "Debug Logging Just
/// Enabled" section it adds only when the log was off.
/// </summary>
public sealed class DebugCommandTests
{
    private static readonly DebugCommand.SettingsPaths Settings =
        new(@"C:\p\settings.json", @"C:\w\.jarvis\settings.json", @"C:\w\.jarvis\settings.local.json");

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1KB")]
    [InlineData(1536, "1.5KB")]
    [InlineData(1024 * 1024, "1MB")]
    [InlineData(1024L * 1024 * 1024, "1GB")]
    public void The_size_line_is_the_references_formatter(long bytes, string expected) =>
        Assert.Equal(expected, DebugCommand.FormatSize(bytes));

    [Fact]
    public void The_tail_block_shows_the_last_twenty_lines()
    {
        var content = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));
        var block = DebugCommand.TailBlock(content, 4096);
        Assert.StartsWith("Log size: 4KB\n\n### Last 20 lines\n", block, StringComparison.Ordinal);
        Assert.Contains("line 11", block, StringComparison.Ordinal);
        Assert.DoesNotContain("line 10\n", block, StringComparison.Ordinal);
        Assert.EndsWith("line 30\n```", block, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_log_answers_the_references_sentence() =>
        Assert.Equal("No log file exists yet.", DebugCommand.ReadTail(null));

    [Fact]
    public void The_just_enabled_section_rides_only_when_the_log_was_off()
    {
        var off = DebugCommand.Prompt("", "C:/log.txt", "(tail)", Settings, wasAlreadyOn: false);
        Assert.Contains("## Debug Logging Just Enabled", off, StringComparison.Ordinal);
        Assert.Contains(
            "Debug logging was OFF for this session until now. Nothing prior to this /debug invocation was captured.",
            off,
            StringComparison.Ordinal);
        Assert.Contains("restart with `jarvis --debug`", off, StringComparison.Ordinal);

        var on = DebugCommand.Prompt("", "C:/log.txt", "(tail)", Settings, wasAlreadyOn: true);
        Assert.DoesNotContain("Debug Logging Just Enabled", on, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undescribed_issue_gets_the_references_standing_instruction()
    {
        var prompt = DebugCommand.Prompt("   ", "C:/log.txt", "(tail)", Settings, wasAlreadyOn: true);
        Assert.Contains(
            "The user did not describe a specific issue. Read the debug log and summarize any errors, " +
            "warnings, or notable issues.",
            prompt,
            StringComparison.Ordinal);

        var described = DebugCommand.Prompt(" streams stall ", "C:/log.txt", "(tail)", Settings, true);
        Assert.Contains("## Issue Description\n\nstreams stall\n", described, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_names_the_log_the_settings_and_the_guide_agent()
    {
        var prompt = DebugCommand.Prompt("x", "C:/log.txt", "(tail)", Settings, wasAlreadyOn: true);
        Assert.Contains("The debug log for the current session is at: `C:/log.txt`", prompt, StringComparison.Ordinal);
        Assert.Contains(@"* user - C:\p\settings.json", prompt, StringComparison.Ordinal);
        Assert.Contains(@"* project - C:\w\.jarvis\settings.json", prompt, StringComparison.Ordinal);
        Assert.Contains(@"* local - C:\w\.jarvis\settings.local.json", prompt, StringComparison.Ordinal);
        Assert.Contains("launching the claude-code-guide subagent", prompt, StringComparison.Ordinal);
        Assert.Contains("2. The last 20 lines show the debug file format.", prompt, StringComparison.Ordinal);
    }
}
