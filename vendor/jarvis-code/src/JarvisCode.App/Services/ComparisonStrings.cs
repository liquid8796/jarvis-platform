namespace JarvisCode.App.Services;

/// <summary>
/// Every sentence the side-by-side model comparison view says, and the labels it
/// builds. Measured out of the reference's own comparison route — ion-dist chunk
/// <c>cf6c0e3a1-Bi64U0E_.js</c>, desktop 1.40609.1.0, where it exports
/// <c>ComparisonRoute</c> and reports to <c>claudeai.iron_swift.*</c>.
///
/// None of it is in the string catalogue: the route writes plain literals rather
/// than <c>defaultMessage</c> entries, so these are pinned against the bundle by
/// <c>PortedTextParityTests</c> instead of by message id.
/// </summary>
internal static class ComparisonStrings
{
    // ---- header ----

    public const string Title = "Model Comparison";

    public const string HideModelNames = "Hide model names";

    public const string HoldResponses = "Hold responses";

    public const string NewChat = "New chat";

    /// <summary>
    /// Why the "Hide model names" switch is refused: hiding shuffles the sides,
    /// and a running memory-off arm cannot be shuffled under the reader.
    /// </summary>
    public const string HideBlockedByMemory =
        "A memory-off arm is running. Start a New chat to hide model names.";

    /// <summary>What a screen reader is told while both answers are held back.</summary>
    public const string WaitingForBoth = "Waiting for both responses to finish";

    // ---- panel ----

    /// <summary>The empty panel's second line, under its own label.</summary>
    public const string PanelEmpty =
        "Both models answer the same prompt, independently. Vote on each pair.";

    public const string PanelSettings = "Panel settings";

    public const string Memory = "Memory";

    public const string MemoryDescription =
        "Use your saved memory in this panel’s replies. Fixed once the chat starts.";

    public const string MemoryLocked =
        "Memory is fixed once the chat starts. Use New chat to change it.";

    public const string MemoryBlind = "Turn off Hide model names to compare memory.";

    /// <summary>The qualifier the model picker carries while this panel's memory is off.</summary>
    public const string MemoryOff = "Memory off";

    /// <summary>The panel's own settings button, named by its position.</summary>
    public static string PanelSettingsLabel(int panel) => $"Panel {panel + 1} settings";

    // ---- voting ----

    public const string WhichDoYouPrefer = "Which response do you prefer?";

    public const string Tie = "Tie";

    public const string BackToVote = "Back to vote";

    public const string TieReason = "What made it a tie?";

    public const string CommentPlaceholder =
        "Add a comment…";

    public const string Save = "Save";

    public const string Comment = "Comment";

    public const string Change = "Change";

    /// <summary>
    /// The reference's default reason chips for a winner. Its own list can be
    /// replaced by a remote configuration; there is none to read here, so the
    /// defaults are what this build offers.
    /// </summary>
    public static readonly IReadOnlyList<string> WinReasons =
    [
        "More accurate", "Better reasoning", "Clearer writing", "Better formatting",
        "Followed instructions", "Better tone", "Other",
    ];

    /// <summary>The same for a tie.</summary>
    public static readonly IReadOnlyList<string> TieReasons =
    [
        "Both good", "Both wrong", "Too similar to tell", "Not enough signal", "Other",
    ];

    /// <summary>"Why {label}?" over a winner, and its own sentence over a tie.</summary>
    public static string ReasonQuestion(string? winnerLabel) =>
        winnerLabel is null ? TieReason : $"Why {winnerLabel}?";

    /// <summary>
    /// The counter beside the header's actions once something has been voted on.
    /// The reference writes the number, a space, then "vote" or "votes".
    /// </summary>
    public static string VotesThisSession(int votes) =>
        votes + " " + (votes == 1 ? "vote" : "votes") + " this session";

    /// <summary>
    /// What the saved row says: the winner, then the reasons lower-cased and
    /// comma-joined behind a middle dot.
    /// </summary>
    public static string PreferenceRecorded(string winner, IReadOnlyList<string>? reasons)
    {
        var tail = reasons is { Count: > 0 }
            ? " · " + string.Join(", ", reasons.Select(r => r.ToLowerInvariant()))
            : "";
        return "Preference recorded — " + winner + tail;
    }

    // ---- labels ----

    /// <summary>The two blind labels, which the reference builds from its own ["a","b"].</summary>
    public static readonly IReadOnlyList<string> Sides = ["a", "b"];

    public static string BlindLabel(int panel) => "Model " + Sides[panel].ToUpperInvariant();

    /// <summary>
    /// A panel's label when the models are shown: the model's name, and its
    /// effort's name after it when the effort has one.
    /// </summary>
    public static string PanelLabel(string modelName, string? effortName) =>
        string.IsNullOrEmpty(effortName) ? modelName : modelName + " " + effortName;

    /// <summary>
    /// What each arm's conversation is called. The reference names it after the
    /// panel and the first 48 characters of the prompt, and leaves the model out
    /// while the names are hidden — a title naming the model would give the blind
    /// comparison away in the chat list.
    /// </summary>
    public static string SessionName(int panel, string modelName, string prompt, bool blind)
    {
        var opening = prompt.Length > 48 ? prompt[..48] : prompt;
        return blind
            ? $"Compare {panel + 1}: {opening}"
            : $"Compare {panel + 1} ({modelName}): {opening}";
    }

    /// <summary>
    /// Two panels running the same model would read as one label twice, so the
    /// reference numbers them where it has to name both at once.
    /// </summary>
    public static IReadOnlyList<string> Disambiguate(IReadOnlyList<string> labels) =>
        labels.Count == 2 && labels[0] == labels[1]
            ? [.. labels.Select((label, index) => $"{label} #{index + 1}")]
            : labels;
}
