namespace JarvisCode.Parity.Tests;

/// <summary>
/// The reference names the assistant "Claude"; this app is Jarvis Code and names
/// it "Jarvis". Every UI string this port took from the reference and rebranded
/// is still checked against the reference — the reference's own text with the
/// brand swapped must be exactly what we render — so a reference rewording still
/// fails, while the deliberate rename does not.
///
/// This is a *UI* rule and deliberately separate from
/// <see cref="ReferenceInstall.Rebrand"/>, which the CLI help check applies to
/// the reference's terminal output. That one also rewrites the bare `claude`
/// command name, and it must never learn this swap: the reference's help text
/// says "Claude subscription", "Claude Desktop" and "Claude in Chrome" about
/// products that keep their names here, and rebranding those would demand
/// HelpTexts.cs lie about them.
/// </summary>
internal static class UiBrand
{
    /// <summary>The reference's wording as this app says it.</summary>
    public static string Apply(string reference) => reference
        .Replace("Claude Code", "Jarvis Code", StringComparison.Ordinal)
        .Replace("Claude", "Jarvis", StringComparison.Ordinal);

    /// <summary>
    /// Whether our string is the reference's, either verbatim or rebranded.
    ///
    /// The choice is left open per string rather than recorded per row because
    /// both answers are legitimate and neither weakens the check: a string with
    /// no brand in it has one candidate, and one with a brand has two, both
    /// derived from what the reference says *now*. That the rebranded strings
    /// stay rebranded is a separate question, and
    /// <see cref="AssistantBrandTests"/> is what asks it.
    /// </summary>
    public static bool Matches(string reference, string ours) =>
        string.Equals(reference, ours, StringComparison.Ordinal) ||
        string.Equals(Apply(reference), ours, StringComparison.Ordinal);
}
