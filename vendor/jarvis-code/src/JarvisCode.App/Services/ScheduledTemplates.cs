namespace JarvisCode.App.Services;

/// <summary>
/// One of the six starter templates the reference offers under "Or start from a
/// template". Each carries the cron it proposes and the body of the prompt it
/// hands the model.
/// </summary>
public sealed record ScheduledTemplate(
    string Id,
    string Title,
    string Description,
    string Cron,
    string PromptBody)
{
    /// <summary>
    /// The message the "Create with Claude" flow sends, the reference's
    /// <c>Xn</c>: its opener plus the template's body, unchanged.
    /// </summary>
    public string Prompt => $"Set up a routine that {PromptBody}";
}

/// <summary>
/// The reference's starter templates, verbatim from its list route
/// (<c>ccd3f68fe-BCipNHOa.js</c>, its <c>Yn</c> array and <c>Jn</c> message
/// table) in its own order.
/// </summary>
public static class ScheduledTemplates
{
    public const string Heading = "Or start from a template";

    public static readonly IReadOnlyList<ScheduledTemplate> All =
    [
        new(
            "daily_briefing",
            "Daily briefing",
            "What needs your attention today across calendar, email, and messages.",
            "0 8 * * 1-5",
            "gives me a morning brief each weekday at 8am: what's on my calendar, important unread emails or " +
            "messages, and anything that needs my attention today. Keep it short and scannable."),
        new(
            "inbox_triage",
            "Inbox triage",
            "Categorize your inbox and draft replies to anything urgent.",
            "0 8 * * 1-5",
            "triages my inbox each weekday morning: group new emails by urgency, summarize each in one line, and " +
            "draft replies to anything that needs a response today."),
        new(
            "meeting_prep",
            "Meeting prep",
            "A short brief before each meeting on your calendar, covering attendees, context, and agenda.",
            "0 8 * * 1-5",
            "preps me for the day's meetings each weekday morning: for each event on my calendar, give me a short " +
            "brief on the attendees, the agenda, and any relevant context."),
        new(
            "weekly_review",
            "Weekly review",
            "A Friday summary of what happened this week.",
            "0 16 * * 5",
            "runs each Friday at 4pm and writes a short summary of what I worked on this week, covering key " +
            "accomplishments, decisions, and anything still open. Make it suitable for a status update."),
        new(
            "content_ideas",
            "Content ideas",
            "Draft a few post ideas each week from the latest news in your industry.",
            "0 9 * * 1",
            "runs each Monday morning and drafts three post ideas based on the past week's news in my industry. " +
            "Ask me which industry or topics to focus on before you set it up."),
        new(
            "monitor_topic",
            "Monitor a topic",
            "Watch for news or mentions of a topic, competitor, or keyword.",
            "0 9 * * *",
            "checks once a day for news or mentions of a topic I care about and summarizes anything new. Ask me " +
            "which topic, competitor, or keyword to watch before you set it up."),
    ];
}
