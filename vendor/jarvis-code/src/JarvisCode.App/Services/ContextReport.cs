using System.Globalization;
using System.Text;
using JarvisCode.Core.Agent;

namespace JarvisCode.App.Services;

/// <summary>
/// The markdown "## Context Usage" report the reference's <c>/context</c>
/// prints on its thin-client path (its <c>Rrt</c>) — the variant the desktop
/// runs, as against the terminal's colored grid. Percentages are measured
/// against the model's raw window, and the two room slices are appended after
/// the spent ones rather than sorted in among them.
/// </summary>
public static class ContextReport
{
    public static string Render(ContextSnapshot snapshot)
    {
        var report = new StringBuilder("## Context Usage\n\n");
        report.Append($"**Model:** {snapshot.ModelName}  \n");
        if (snapshot.IsEstimated)
            report.Append("Token counts are estimates. The browser does not report exact usage; the context limit is configured locally.\n\n");
        int percent = snapshot.Cap <= 0
            ? 0
            : (int)Math.Round(snapshot.UsedTokens * 100.0 / snapshot.Cap, MidpointRounding.AwayFromZero);
        report.Append(
            $"**Tokens:** {ContextWindows.FormatTokens(snapshot.UsedTokens)} / " +
            $"{ContextWindows.FormatTokens(snapshot.Cap)} ({percent}%)\n");
        if (OverLimitNotice(snapshot) is { } notice)
            report.Append($"**Over limit:** {notice}\n");
        report.Append('\n');

        string Percentage(long tokens) => snapshot.Cap <= 0
            ? "0.0"
            : (tokens / (double)snapshot.Cap * 100).ToString("0.0", CultureInfo.InvariantCulture);

        void Row(ContextCategory category) => report.Append(
            $"| {category.Label} | {ContextWindows.FormatTokens(category.Tokens)} | " +
            $"{Percentage(category.Tokens)}% |\n");

        // The reference holds back only these two labels — "Compact buffer"
        // stays in the main table, which is where its own filter leaves it.
        var spent = snapshot.Categories
            .Where(c => c.Tokens > 0 &&
                c.Label != ContextBreakdown.FreeSpaceLabel &&
                c.Label != ContextBreakdown.AutocompactBufferLabel)
            .ToList();
        if (spent.Count > 0)
        {
            report.Append("### Estimated usage by category\n\n");
            report.Append("| Category | Tokens | Percentage |\n");
            report.Append("|----------|--------|------------|\n");
            foreach (var category in spent)
                Row(category);
            foreach (var label in new[] { ContextBreakdown.FreeSpaceLabel, ContextBreakdown.AutocompactBufferLabel })
            {
                if (snapshot.Categories.FirstOrDefault(c => c.Label == label) is { Tokens: > 0 } room)
                    Row(room);
            }

            report.Append('\n');
        }

        Table(report, "MCP Tools", "Tool", "Server", snapshot.McpTools);
        Table(report, "Custom Agents", "Agent Type", "Source", snapshot.Agents);
        Table(report, "Memory Files", "Type", "Path", snapshot.MemoryFiles);
        Table(report, "Skills", "Skill", "Source", snapshot.Skills);
        return report.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// The reference's <c>Hrt</c>: nothing until the raw window is passed, then
    /// a hard limit when the model's own window is the one in force and an
    /// overshot compaction window when a setting narrowed it.
    /// </summary>
    public static string? OverLimitNotice(ContextSnapshot snapshot)
    {
        if (!snapshot.IsOverLimit)
            return null;

        string over = ContextWindows.FormatTokens(snapshot.UsedTokens - snapshot.Cap);
        string limit = ContextWindows.FormatTokens(snapshot.Cap);
        bool compactDisabled = Environment.GetEnvironmentVariable("DISABLE_COMPACT") is { Length: > 0 };
        return snapshot.WindowSource == AutoCompactWindowSource.Auto
            ? $"Context exceeds the {limit}-token limit by {over} tokens — " +
              $"run {(compactDisabled ? "/clear" : "/compact or /clear")} to continue."
            : $"Context is {over} tokens past the {limit}-token compaction window — " +
              $"run {(compactDisabled ? "/clear" : "/compact")} to reduce usage.";
    }

    private static void Table(
        StringBuilder report,
        string heading,
        string firstColumn,
        string secondColumn,
        IReadOnlyList<ContextDetail> rows)
    {
        if (rows.Count == 0)
            return;

        report.Append($"### {heading}\n\n");
        report.Append($"| {firstColumn} | {secondColumn} | Tokens |\n");
        report.Append(
            $"|{new string('-', firstColumn.Length + 2)}|{new string('-', secondColumn.Length + 2)}|--------|\n");
        foreach (var row in rows)
            report.Append($"| {row.Name} | {row.Source} | {ContextWindows.FormatTokens(row.Tokens)} |\n");
        report.Append('\n');
    }
}
